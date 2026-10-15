using GitSweep.Core.Models;

namespace GitSweep.Core.Services;

/// <summary>
/// Stores deleted branches under <c>.git/gitsweep/trash</c> so
/// <c>restore</c>/<c>undo</c> can recreate them from the saved SHA.
/// Implementations must fail closed: an entry is only reported as saved once
/// it is durably on disk, and storage identifiers are service-generated
/// (never derived from user input).
/// </summary>
public interface ITrashService
{
    /// <summary>
    /// Persists a trash entry and returns it with its generated
    /// <see cref="TrashEntry.Id"/>. Throws on invalid input or I/O failure so
    /// callers can abort the deletion instead of losing the branch.
    /// </summary>
    Task<TrashEntry> SaveAsync(string path, string branchName, string commitSha, Guid batchId, CancellationToken ct = default);

    /// <summary>
    /// Lists all valid trash entries newest-first. Corrupt or invalid files
    /// are ignored, never thrown and never returned.
    /// </summary>
    Task<IReadOnlyList<TrashEntry>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns the entry with the given service-generated id, or
    /// <see langword="null"/> when absent or the id is not well-formed.
    /// </summary>
    Task<TrashEntry?> GetAsync(string path, string id, CancellationToken ct = default);

    /// <summary>
    /// Removes the entry with the given service-generated id. Returns
    /// <see langword="false"/> when absent or the id is not well-formed.
    /// </summary>
    Task<bool> RemoveAsync(string path, string id, CancellationToken ct = default);
}
