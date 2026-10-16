using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

public sealed class TrashServiceTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), "GitSweepTrashTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _git = new();
    private readonly TrashService _sut;
    private string _branchSha = string.Empty;

    public TrashServiceTests()
    {
        _sut = new TrashService(_git);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoPath);
        await RunGitAsync("init -b main");
        await RunGitAsync("config user.email tests@example.com");
        await RunGitAsync("config user.name GitSweep Tests");
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "README.md"), "initial");
        await RunGitAsync("add .");
        await RunGitAsync("commit -m \"initial commit\" --no-gpg-sign");
        await RunGitAsync("checkout -b feature/to-delete");
        await File.AppendAllTextAsync(Path.Combine(_repoPath, "README.md"), " work");
        await RunGitAsync("add .");
        await RunGitAsync("commit -m \"feature work\" --no-gpg-sign");
        _branchSha = (await RunGitAsync("rev-parse HEAD")).Trim();
        await RunGitAsync("checkout main");
        // Merge it so safe `git branch -d` (never -D) is allowed to delete it.
        await RunGitAsync("merge feature/to-delete --no-ff -m \"merge feature/to-delete\" --no-gpg-sign");
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_repoPath))
            {
                Directory.Delete(_repoPath, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup errors in tests
        }

        return Task.CompletedTask;
    }

    private string TrashDirectory => Path.Combine(_repoPath, ".git", "gitsweep", "trash");

    [Fact]
    public async Task SaveAsync_persists_entry_under_git_gitsweep_trash()
    {
        var batchId = Guid.NewGuid();

        var entry = await _sut.SaveAsync(_repoPath, "feature/to-delete", _branchSha, batchId);

        Assert.True(Directory.Exists(TrashDirectory));
        var files = Directory.GetFiles(TrashDirectory, "*.json");
        var file = Assert.Single(files);
        Assert.Equal(entry.Id + ".json", Path.GetFileName(file));
        // The branch name (with its '/') must never become the file name.
        Assert.DoesNotContain("to-delete", Path.GetFileName(file));
        Assert.Equal("feature/to-delete", entry.BranchName);
        Assert.Equal(_branchSha, entry.CommitSha);
        Assert.Equal(batchId, entry.BatchId);

        var listed = await _sut.ListAsync(_repoPath);
        var listedEntry = Assert.Single(listed);
        Assert.Equal(entry, listedEntry);
    }

    [Fact]
    public async Task SaveAsync_rejects_invalid_branch_name()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.SaveAsync(_repoPath, "../evil", _branchSha, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.SaveAsync(_repoPath, "-D", _branchSha, Guid.NewGuid()));
        Assert.False(Directory.Exists(TrashDirectory));
    }

    [Fact]
    public async Task SaveAsync_rejects_invalid_sha_and_empty_batch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.SaveAsync(_repoPath, "feature/to-delete", "not-a-sha", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.SaveAsync(_repoPath, "feature/to-delete", _branchSha[..7], Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.SaveAsync(_repoPath, "feature/to-delete", _branchSha, Guid.Empty));
    }

    [Fact]
    public async Task ListAsync_returns_newest_first_and_skips_corrupt_files()
    {
        var first = await _sut.SaveAsync(_repoPath, "feature/to-delete", _branchSha, Guid.NewGuid());
        var second = await _sut.SaveAsync(_repoPath, "feature/other", _branchSha, Guid.NewGuid());
        await File.WriteAllTextAsync(Path.Combine(TrashDirectory, "deadbeefdeadbeefdeadbeefdeadbeef.json"), "{ not json");
        // Valid JSON but dangerous content: path-like branch and short SHA.
        await File.WriteAllTextAsync(
            Path.Combine(TrashDirectory, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json"),
            """{"Id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","BranchName":"../../evil","CommitSha":"abc","DeletedAtUtc":"2024-01-01T00:00:00+00:00","BatchId":"11111111-1111-1111-1111-111111111111"}""");

        var listed = await _sut.ListAsync(_repoPath);

        Assert.Equal(2, listed.Count);
        Assert.Equal(second.Id, listed[0].Id);
        Assert.Equal(first.Id, listed[1].Id);
    }

    [Fact]
    public async Task GetAndRemove_roundtrip_and_reject_traversal_ids()
    {
        var entry = await _sut.SaveAsync(_repoPath, "feature/to-delete", _branchSha, Guid.NewGuid());

        var fetched = await _sut.GetAsync(_repoPath, entry.Id);
        Assert.Equal(entry, fetched);

        Assert.Null(await _sut.GetAsync(_repoPath, "../../secret"));
        Assert.False(await _sut.RemoveAsync(_repoPath, "../" + entry.Id));
        Assert.False(await _sut.RemoveAsync(_repoPath, entry.Id + ".json"));

        Assert.True(await _sut.RemoveAsync(_repoPath, entry.Id));
        Assert.Null(await _sut.GetAsync(_repoPath, entry.Id));
        Assert.False(await _sut.RemoveAsync(_repoPath, entry.Id));
        Assert.Empty(await _sut.ListAsync(_repoPath));
    }

    [Fact]
    public async Task ListAsync_returns_empty_for_non_repository()
    {
        var path = Path.Combine(Path.GetTempPath(), "GitSweepTrashTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        Assert.Empty(await _sut.ListAsync(path));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SaveAsync(path, "feature/x", _branchSha, Guid.NewGuid()));
    }

    [Fact]
    public async Task Git_service_captures_tip_and_recreates_branch_at_same_sha()
    {
        var sha = await _git.GetBranchTipShaAsync(_repoPath, "feature/to-delete");
        Assert.Equal(_branchSha, sha);
        Assert.True(await _git.BranchExistsAsync(_repoPath, "feature/to-delete"));
        Assert.Null(await _git.GetBranchTipShaAsync(_repoPath, "feature/missing"));
        Assert.False(await _git.BranchExistsAsync(_repoPath, "../evil"));

        var (deleteSuccess, deleteError) = await _git.DeleteBranchAsync(_repoPath, "feature/to-delete");
        Assert.True(deleteSuccess, deleteError);
        Assert.False(await _git.BranchExistsAsync(_repoPath, "feature/to-delete"));

        var (createSuccess, createError) = await _git.CreateBranchAsync(_repoPath, "feature/to-delete", sha!);
        Assert.True(createSuccess, createError);
        Assert.Equal(_branchSha, await _git.GetBranchTipShaAsync(_repoPath, "feature/to-delete"));
    }

    [Fact]
    public async Task CreateBranchAsync_never_overwrites_existing_branch()
    {
        var otherSha = (await RunGitAsync("rev-parse main")).Trim();

        var (success, _) = await _git.CreateBranchAsync(_repoPath, "feature/to-delete", otherSha);

        Assert.False(success);
        // The existing branch still points at its original commit.
        Assert.Equal(_branchSha, await _git.GetBranchTipShaAsync(_repoPath, "feature/to-delete"));
    }

    [Fact]
    public async Task CreateBranchAsync_rejects_invalid_inputs()
    {
        Assert.False((await _git.CreateBranchAsync(_repoPath, "-f", _branchSha)).Success);
        Assert.False((await _git.CreateBranchAsync(_repoPath, "feature/new", "xyz")).Success);
        Assert.False((await _git.CreateBranchAsync(_repoPath, "feature/new", _branchSha[..7])).Success);
        Assert.False(await _git.BranchExistsAsync(_repoPath, "feature/new"));
    }

    [Fact]
    public async Task Branch_delete_and_create_do_not_execute_repository_hooks()
    {
        // Ref updates fire the repo's reference-transaction hook. A hostile
        // repo (config + hooks shipped in an archive) must not get code
        // execution out of a branch delete or restore.
        var hooksDirectory = Path.Combine(_repoPath, "hostile-hooks");
        Directory.CreateDirectory(hooksDirectory);
        var marker = Path.Combine(_repoPath, "gitsweep-hook-marker.txt");
        await TestScripts.WriteExecutableAsync(
            Path.Combine(hooksDirectory, "reference-transaction"),
            $"#!/bin/sh\necho ran >> {marker.Replace('\\', '/')}\n");
        await RunGitAsync($"config core.hooksPath {hooksDirectory}");

        // Positive control: plain git does run the hook, so the setup works.
        await RunGitAsync("branch hook-probe main");
        Assert.True(File.Exists(marker));
        File.Delete(marker);

        var (deleteSuccess, deleteError) = await _git.DeleteBranchAsync(_repoPath, "feature/to-delete");
        Assert.True(deleteSuccess, deleteError);
        var (createSuccess, createError) = await _git.CreateBranchAsync(_repoPath, "feature/to-delete", _branchSha);
        Assert.True(createSuccess, createError);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ListAsync_skips_oversized_entry_files()
    {
        Directory.CreateDirectory(TrashDirectory);
        var id = Guid.NewGuid().ToString("N");
        var json = RawEntryJson(id, DateTimeOffset.UtcNow) + new string(' ', 70_000);
        await File.WriteAllTextAsync(Path.Combine(TrashDirectory, id + ".json"), json);

        // Valid JSON, but far larger than any real entry: rejected unread.
        Assert.Empty(await _sut.ListAsync(_repoPath));
        Assert.Null(await _sut.GetAsync(_repoPath, id));
    }

    [Fact]
    public async Task ListAsync_skips_entries_dated_in_the_future()
    {
        Directory.CreateDirectory(TrashDirectory);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            Path.Combine(TrashDirectory, id + ".json"),
            RawEntryJson(id, DateTimeOffset.UtcNow.AddDays(1)));

        // A future-dated planted entry must not hijack undo's batch pick.
        Assert.Empty(await _sut.ListAsync(_repoPath));
        Assert.Null(await _sut.GetAsync(_repoPath, id));
    }

    [FileSymlinkFact]
    public async Task ListAsync_skips_symlinked_entry_files()
    {
        Directory.CreateDirectory(TrashDirectory);
        var id = Guid.NewGuid().ToString("N");
        var targetPath = Path.Combine(_repoPath, "outside-entry.json");
        await File.WriteAllTextAsync(targetPath, RawEntryJson(id, DateTimeOffset.UtcNow));
        TestLinks.CreateFileSymlink(Path.Combine(TrashDirectory, id + ".json"), targetPath);

        Assert.Empty(await _sut.ListAsync(_repoPath));
        Assert.Null(await _sut.GetAsync(_repoPath, id));
    }

    [DirectoryLinkFact]
    public async Task SaveAsync_refuses_symlinked_trash_directory()
    {
        var outside = Path.Combine(Path.GetTempPath(), "GitSweepTrashOutside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var gitsweepPath = Path.Combine(_repoPath, ".git", "gitsweep");
        try
        {
            // Symlink, or a junction on Windows without symlink privilege.
            TestLinks.CreateDirectoryLink(gitsweepPath, outside);
        }
        catch (Exception)
        {
            Directory.Delete(outside, recursive: true);
            throw;
        }

        try
        {
            // Writing through the link would land outside .git: refuse, and
            // report an empty trash instead of following it on reads.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _sut.SaveAsync(_repoPath, "feature/to-delete", _branchSha, Guid.NewGuid()));
            Assert.Empty(Directory.GetFiles(outside, "*.json", SearchOption.AllDirectories));
            Assert.Empty(await _sut.ListAsync(_repoPath));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_and_GetAsync_skip_entry_whose_id_does_not_match_its_file_name()
    {
        // A valid entry copied to <another-guid>.json keeps the original Id
        // inside. Without the Id == file name check it would be listed (and
        // restoring it would delete the original entry's file, the only SHA
        // record of another deleted branch).
        var original = await _sut.SaveAsync(_repoPath, "feature/to-delete", _branchSha, Guid.NewGuid());
        var foreignId = Guid.NewGuid().ToString("N");
        File.Copy(
            Path.Combine(TrashDirectory, original.Id + ".json"),
            Path.Combine(TrashDirectory, foreignId + ".json"));

        var listed = await _sut.ListAsync(_repoPath);

        var only = Assert.Single(listed);
        Assert.Equal(original, only);
        Assert.Null(await _sut.GetAsync(_repoPath, foreignId));
        Assert.Equal(original, await _sut.GetAsync(_repoPath, original.Id));
    }

    [Fact]
    public async Task ListAsync_skips_empty_entry_files()
    {
        Directory.CreateDirectory(TrashDirectory);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(Path.Combine(TrashDirectory, id + ".json"), []);

        Assert.Empty(await _sut.ListAsync(_repoPath));
        Assert.Null(await _sut.GetAsync(_repoPath, id));
    }

    [Fact]
    public async Task ListAsync_accepts_entry_file_of_exactly_the_size_cap()
    {
        // The bounded read holds cap + 1 bytes to detect an oversized file;
        // an entry of exactly the cap must still load (no off-by-one).
        Directory.CreateDirectory(TrashDirectory);
        var id = Guid.NewGuid().ToString("N");
        var json = RawEntryJson(id, DateTimeOffset.UtcNow);
        var path = Path.Combine(TrashDirectory, id + ".json");
        await File.WriteAllTextAsync(path, json + new string(' ', 64 * 1024 - json.Length));
        Assert.Equal(64 * 1024, new FileInfo(path).Length);

        var entry = Assert.Single(await _sut.ListAsync(_repoPath));
        Assert.Equal(id, entry.Id);
        Assert.NotNull(await _sut.GetAsync(_repoPath, id));
    }

    [FifoFact]
    public async Task ListAsync_and_GetAsync_skip_fifo_entry_instead_of_hanging()
    {
        // A FIFO named like an entry (shippable in a tar archive) reports
        // length 0 and blocks open(2) until a writer appears.
        var real = await _sut.SaveAsync(_repoPath, "feature/to-delete", _branchSha, Guid.NewGuid());
        var fifoId = Guid.NewGuid().ToString("N");
        var fifoPath = Path.Combine(TrashDirectory, fifoId + ".json");
        TestFifo.Create(fifoPath);

        var listed = await TestFifo.CompleteWithinTimeoutAsync(fifoPath, () => _sut.ListAsync(_repoPath));
        var fetched = await TestFifo.CompleteWithinTimeoutAsync(fifoPath, () => _sut.GetAsync(_repoPath, fifoId));

        Assert.Equal(real, Assert.Single(listed));
        Assert.Null(fetched);
    }

    private string RawEntryJson(string id, DateTimeOffset deletedAtUtc) =>
        $$"""{"Id":"{{id}}","BranchName":"feature/to-delete","CommitSha":"{{_branchSha}}","DeletedAtUtc":"{{deletedAtUtc:o}}","BatchId":"{{Guid.NewGuid()}}"}""";

    [Fact]
    public async Task EndToEnd_clean_style_save_delete_and_restore()
    {
        var batchId = Guid.NewGuid();
        var sha = await _git.GetBranchTipShaAsync(_repoPath, "feature/to-delete");
        var entry = await _sut.SaveAsync(_repoPath, "feature/to-delete", sha!, batchId);
        Assert.True((await _git.DeleteBranchAsync(_repoPath, "feature/to-delete")).Success);

        // Simulate restore/undo core: recreate at the trashed SHA, then drop the entry.
        Assert.True((await _git.CreateBranchAsync(_repoPath, entry.BranchName, entry.CommitSha)).Success);
        Assert.True(await _sut.RemoveAsync(_repoPath, entry.Id));
        Assert.Equal(_branchSha, await _git.GetBranchTipShaAsync(_repoPath, "feature/to-delete"));
        Assert.Empty(await _sut.ListAsync(_repoPath));
    }

    private async Task<string> RunGitAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {stderr}{stdout}");
        }

        return stdout;
    }
}
