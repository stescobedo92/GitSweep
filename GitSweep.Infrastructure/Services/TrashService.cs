using GitSweep.Core.Models;
using GitSweep.Core.Services;
using System.Text.Json;

namespace GitSweep.Infrastructure.Services;

/// <summary>
/// File-backed <see cref="ITrashService"/> storing one JSON file per deleted
/// branch under <c>&lt;git-common-dir&gt;/gitsweep/trash</c>.
/// Safety properties:
/// <list type="bullet">
/// <item>File names are service-generated GUIDs, never branch names, so user
/// input cannot influence the stored path (no traversal via <c>../</c> or
/// separators hidden in a branch name).</item>
/// <item>Every resolved path is checked to stay inside the trash directory
/// before it is read, written or deleted.</item>
/// <item>Writes are atomic: serialize to a unique temp file in the same
/// directory, flush to disk, then move without overwrite.</item>
/// <item>Reads fail closed: entries with an invalid branch name, a non-hex
/// SHA, an empty batch or an id that does not match its file name are
/// skipped, never returned.</item>
/// <item>Symbolic links are never followed: a symlinked <c>gitsweep</c> or
/// <c>trash</c> directory makes saves throw and reads report an empty
/// trash, and a symlinked entry file is skipped, so a planted link cannot
/// redirect trash I/O outside <c>.git</c>.</item>
/// <item>Entry files that are empty or larger than <see cref="MaxTrashEntryBytes"/>,
/// and entries dated in the future, are rejected: a real entry is a few
/// hundred bytes written at deletion time, so any of them means the file was
/// planted or corrupted (a future date could otherwise hijack which batch
/// <c>undo</c> picks). Emptiness is checked before the file is opened
/// because a FIFO reports length 0 and opening it would block forever; the
/// read itself is bounded to the cap plus one byte
/// (see <see cref="BoundedFileReader"/>).</item>
/// </list>
/// </summary>
public sealed class TrashService : ITrashService
{
    // A serialized entry is a few hundred bytes; 64 KiB is generous
    // headroom and bounds what a planted file can make a reader allocate.
    private const int MaxTrashEntryBytes = 64 * 1024;

    // Small clock-skew allowance for the future-date rejection.
    private static readonly TimeSpan FutureDateSkew = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IGitRepositoryService _gitService;

    public TrashService(IGitRepositoryService gitService)
    {
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
    }

    public async Task<TrashEntry> SaveAsync(
        string path, string branchName, string commitSha, Guid batchId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!GitRepositoryService.IsValidBranchName(branchName))
        {
            throw new ArgumentException($"Invalid branch name: '{branchName}'.", nameof(branchName));
        }

        if (!GitRepositoryService.IsValidCommitSha(commitSha))
        {
            throw new ArgumentException("Commit SHA must be a full 40- or 64-character hex value.", nameof(commitSha));
        }

        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("Batch id must not be empty.", nameof(batchId));
        }

        var trashDirectory = await ResolveTrashDirectoryAsync(path, ct);

        // Never write through a planted symlink: if either directory
        // segment already exists as a link, CreateDirectory would happily
        // create the rest inside the link target, outside .git.
        ThrowIfReparsePoint(Path.GetDirectoryName(trashDirectory)!);
        ThrowIfReparsePoint(trashDirectory);
        Directory.CreateDirectory(trashDirectory);
        ThrowIfReparsePoint(trashDirectory);

        var entry = new TrashEntry(
            Guid.NewGuid().ToString("N"),
            branchName,
            commitSha.ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            batchId);

        var finalPath = GetContainedPath(trashDirectory, entry.Id + ".json");
        var tempPath = GetContainedPath(trashDirectory, $".{entry.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, entry, JsonOptions, ct);
                await stream.FlushAsync(ct);
                // The entry must survive a crash between save and delete:
                // flush to disk, not just to the OS page cache.
                stream.Flush(flushToDisk: true);
            }

            // Same-directory move without overwrite: atomic on the same volume
            // and refuses to clobber an existing entry.
            File.Move(tempPath, finalPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception)
            {
                // Best effort only: a leftover *.tmp is ignored by ListAsync.
            }
        }

        return entry;
    }

    public async Task<IReadOnlyList<TrashEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var trashDirectory = await TryResolveTrashDirectoryAsync(path, ct);
        if (trashDirectory is null || !Directory.Exists(trashDirectory))
        {
            return [];
        }

        var entries = new List<TrashEntry>();
        foreach (var file in Directory.EnumerateFiles(trashDirectory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var entry = await TryReadEntryAsync(file, ct);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries
            .OrderByDescending(entry => entry.DeletedAtUtc)
            .ThenBy(entry => entry.BranchName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TrashEntry?> GetAsync(string path, string id, CancellationToken ct = default)
    {
        if (!IsValidTrashId(id))
        {
            return null;
        }

        var trashDirectory = await TryResolveTrashDirectoryAsync(path, ct);
        if (trashDirectory is null)
        {
            return null;
        }

        var filePath = GetContainedPath(trashDirectory, id + ".json");
        return File.Exists(filePath) ? await TryReadEntryAsync(filePath, ct) : null;
    }

    public async Task<bool> RemoveAsync(string path, string id, CancellationToken ct = default)
    {
        if (!IsValidTrashId(id))
        {
            return false;
        }

        var trashDirectory = await TryResolveTrashDirectoryAsync(path, ct);
        if (trashDirectory is null)
        {
            return false;
        }

        var filePath = GetContainedPath(trashDirectory, id + ".json");
        if (!File.Exists(filePath))
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();
        File.Delete(filePath);
        return true;
    }

    /// <summary>
    /// A trash id is exactly the file-name form this service generates:
    /// a GUID in "N" format (32 hex chars). Anything else (separators,
    /// <c>..</c>, extensions) is rejected before a path is built.
    /// </summary>
    internal static bool IsValidTrashId(string? id) =>
        id is { Length: 32 } && id.All(Uri.IsHexDigit);

    private async Task<string> ResolveTrashDirectoryAsync(string path, CancellationToken ct)
    {
        return await TryResolveTrashDirectoryAsync(path, ct)
            ?? throw new InvalidOperationException(
                $"'{path}' is not a Git repository. Cannot resolve the trash directory.");
    }

    private async Task<string?> TryResolveTrashDirectoryAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        var gitDirectory = await _gitService.GetGitCommonDirectoryAsync(path, ct);
        if (string.IsNullOrWhiteSpace(gitDirectory))
        {
            return null;
        }

        var fullGitDirectory = Path.GetFullPath(gitDirectory);
        var trashDirectory = Path.GetFullPath(Path.Combine(fullGitDirectory, "gitsweep", "trash"));

        // Fail closed if the combined path somehow escapes the git directory
        // (it cannot with fixed segments, but the check keeps the invariant
        // explicit if the layout ever changes).
        var prefix = fullGitDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!trashDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved trash directory escapes the git directory.");
        }

        // Fail closed when a segment is a symbolic link: reading, writing
        // or deleting through it would leave the git directory. A missing
        // directory is fine (empty trash); SaveAsync re-checks after it
        // creates the directory.
        if (Directory.Exists(trashDirectory) &&
            (IsReparsePoint(Path.GetDirectoryName(trashDirectory)!) || IsReparsePoint(trashDirectory)))
        {
            return null;
        }

        return trashDirectory;
    }

    /// <summary>
    /// Whether <paramref name="path"/> itself is a symbolic link or other
    /// reparse point. Missing paths are not; unreadable ones are treated as
    /// links so callers fail closed instead of following them.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !Directory.Exists(path) && !File.Exists(path) ? false : true;
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if (Directory.Exists(path) && IsReparsePoint(path))
        {
            throw new InvalidOperationException(
                $"Refusing to use '{path}' as trash storage because it is a symbolic link.");
        }
    }

    private static string GetContainedPath(string trashDirectory, string fileName)
    {
        var fullPath = Path.GetFullPath(Path.Combine(trashDirectory, fileName));
        var prefix = Path.GetFullPath(trashDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(trashDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved trash path escapes the trash directory.");
        }

        return fullPath;
    }

    private static async Task<TrashEntry?> TryReadEntryAsync(string filePath, CancellationToken ct)
    {
        try
        {
            // Never follow a planted link out of the trash, never open an
            // empty file (a FIFO reports length 0 and would block open(2)
            // forever), and never read more than the cap into memory: real
            // entries are a few hundred bytes. Anything else is skipped.
            var read = await BoundedFileReader.ReadAsync(filePath, MaxTrashEntryBytes, ct);
            if (read.Status != BoundedReadStatus.Ok)
            {
                return null;
            }

            using var content = read.Content!;
            var entry = JsonSerializer.Deserialize<TrashEntry>(content, JsonOptions);

            if (entry is null || !IsValidEntry(entry))
            {
                return null;
            }

            // The id must match the file it was read from; a hand-crafted or
            // renamed file cannot mint an entry under a foreign id.
            var fileId = Path.GetFileNameWithoutExtension(filePath);
            return string.Equals(entry.Id, fileId, StringComparison.OrdinalIgnoreCase) ? entry : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsValidEntry(TrashEntry entry) =>
        IsValidTrashId(entry.Id) &&
        GitRepositoryService.IsValidBranchName(entry.BranchName) &&
        GitRepositoryService.IsValidCommitSha(entry.CommitSha) &&
        entry.BatchId != Guid.Empty &&
        entry.DeletedAtUtc != default &&
        // Entries are stamped with UtcNow when written. A future date
        // means the file was planted or corrupted, and because listings
        // are newest-first it could hijack which batch `undo` restores.
        entry.DeletedAtUtc <= DateTimeOffset.UtcNow + FutureDateSkew;
}
