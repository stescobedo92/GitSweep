using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// T4R2-01: three rounds of name-based rollback were each bypassed by a
/// hostile ref store (self-upstream alias, packed prefixes, a stale packed
/// copy of the branch that <c>-d</c> leaves behind). The durable fix is a
/// GitSweep keep-ref <c>refs/gitsweep/trash/&lt;id&gt;</c> created before the
/// delete: while it exists, the branch's commit is referenced no matter what
/// the ref store does, so <c>git gc</c> cannot discard it. These tests use
/// real temp repos under the system temp dir, on Linux and Windows.
/// </summary>
public sealed class KeepRefTests : IAsyncLifetime
{
    private const string WipBranch = "feature/wip";
    private const string TrashId = "0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _git = new();
    private string _repoPath = "";

    public async Task InitializeAsync() => _repoPath = await CreateRepositoryAsync("repo");

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // --- The end-to-end data-loss payloads from T4R2-01 ---

    [DirectoryLinkTheory]
    [InlineData("lyingsorted")]
    [InlineData("dup")]
    public async Task Delete_payload_leaves_the_tip_referenced_by_the_keep_ref(string payload)
    {
        // The deletion path that bypasses the name-based rollback: a packed
        // copy of the branch that `-d` leaves behind. git's exact outcome
        // varies by version (on some, `-d` leaves the name resolving and
        // DeleteBranchAsync throws; on others it recreates the branch), but
        // the invariant holds either way: after clean's save+delete the user's
        // tip is still referenced and its commit object survives, because the
        // keep-ref created on save pins it regardless of what `-d` did.
        var tip = await BuildHostileSelfUpstreamRepoAsync(_repoPath, payload);

        // clean's lifecycle: save (creates the keep-ref) then delete.
        var trash = new TrashService(_git);
        var entry = await trash.SaveAsync(_repoPath, WipBranch, tip, Guid.NewGuid());
        await DeleteToleratingVerificationFailureAsync(WipBranch);

        // The keep-ref holds the tip and the commit object is intact.
        Assert.Equal(tip, (await RunRawGitAsync(_repoPath, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{entry.Id}")).Output.Trim());
        Assert.Equal(0, (await RunRawGitAsync(_repoPath, "cat-file", "-e", $"{tip}^{{commit}}")).ExitCode);
    }

    [DirectoryLinkFact]
    public async Task User_commit_survives_a_hostile_gc_because_the_keep_ref_pins_it()
    {
        // The `dup` payload leaves a well-formed packed-refs (two plain lines,
        // no lying header), so `git gc` runs cleanly and the loss is real.
        // (The `lyingsorted` payload makes git's own gc abort on the malformed
        // file, so it cannot demonstrate the gc step.)
        const string payload = "dup";

        // Positive control: plain `git branch -d` with NO keep-ref loses the
        // commit to a hostile `git gc --prune=now`. (The control never
        // recreates the branch, so its tip is unreferenced on every git
        // version.)
        var control = await CreateRepositoryAsync("control");
        var controlTip = await BuildHostileSelfUpstreamRepoAsync(control, payload);
        Assert.Equal(0, (await RunRawGitAsync(control, "branch", "-d", "--", WipBranch)).ExitCode);
        await SetHostileGcConfigAsync(control);
        await RunRawGitAsync(control, "reflog", "expire", "--all", "--expire=now", "--expire-unreachable=now");
        Assert.Equal(0, (await RunRawGitAsync(control, "gc", "--prune=now")).ExitCode);
        Assert.NotEqual(0, (await RunRawGitAsync(control, "cat-file", "-e", $"{controlTip}^{{commit}}")).ExitCode);

        // Through GitSweep: the keep-ref is created before the delete, so
        // whether `-d` leaves the branch gone (keep-ref protects) or the
        // post-check recreates it (branch protects), the commit is referenced.
        var tip = await BuildHostileSelfUpstreamRepoAsync(_repoPath, payload);
        var trash = new TrashService(_git);
        var entry = await trash.SaveAsync(_repoPath, WipBranch, tip, Guid.NewGuid());
        await DeleteToleratingVerificationFailureAsync(WipBranch);

        // Same hostile gc, but the keep-ref pins the commit: it survives.
        await SetHostileGcConfigAsync(_repoPath);
        await RunRawGitAsync(_repoPath, "reflog", "expire", "--all", "--expire=now", "--expire-unreachable=now");
        Assert.Equal(0, (await RunRawGitAsync(_repoPath, "gc", "--prune=now")).ExitCode);
        Assert.Equal(0, (await RunRawGitAsync(_repoPath, "cat-file", "-e", $"{tip}^{{commit}}")).ExitCode);
        Assert.Equal(tip, (await RunRawGitAsync(_repoPath, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{entry.Id}")).Output.Trim());
    }

    // `-d` + the post-check either succeeds (branch recreated) or throws when
    // the name still resolves and recreation fails; both keep the commit
    // referenced. The exact path is git-version dependent, so tolerate either.
    private async Task DeleteToleratingVerificationFailureAsync(string branchName)
    {
        try
        {
            await _git.DeleteBranchAsync(_repoPath, branchName);
        }
        catch (BranchDeletionVerificationException)
        {
            // The branch stayed deleted and could not be recreated; the
            // keep-ref is the safety net the rest of the test verifies.
        }
    }

    // --- Keep-ref lifecycle through the trash service ---

    [Fact]
    public async Task SaveAsync_creates_a_keep_ref_at_the_saved_sha()
    {
        var tip = await CreateUnmergedBranchAsync(_repoPath);
        var trash = new TrashService(_git);

        var entry = await trash.SaveAsync(_repoPath, WipBranch, tip, Guid.NewGuid());

        Assert.Equal(tip, (await RunRawGitAsync(_repoPath, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{entry.Id}")).Output.Trim());
    }

    [Fact]
    public async Task RemoveAsync_deletes_the_keep_ref()
    {
        var tip = await CreateUnmergedBranchAsync(_repoPath);
        var trash = new TrashService(_git);
        var entry = await trash.SaveAsync(_repoPath, WipBranch, tip, Guid.NewGuid());

        Assert.True(await trash.RemoveAsync(_repoPath, entry.Id));

        Assert.Equal(1, (await RunRawGitAsync(_repoPath, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{entry.Id}")).ExitCode);
    }

    [Fact]
    public async Task RemoveAsync_with_a_missing_keep_ref_still_removes_the_entry()
    {
        var tip = await CreateUnmergedBranchAsync(_repoPath);
        var trash = new TrashService(_git);
        var entry = await trash.SaveAsync(_repoPath, WipBranch, tip, Guid.NewGuid());
        // Someone else already removed the keep-ref.
        Assert.True(await _git.DeleteTrashKeepRefAsync(_repoPath, entry.Id, tip));

        Assert.True(await trash.RemoveAsync(_repoPath, entry.Id));
    }

    [Fact]
    public async Task DeleteTrashKeepRefAsync_never_deletes_a_ref_whose_value_differs()
    {
        var tip = await CreateUnmergedBranchAsync(_repoPath);
        var mainTip = (await RunRawGitAsync(_repoPath, "rev-parse", "refs/heads/main")).Output.Trim();
        Assert.True((await _git.CreateTrashKeepRefAsync(_repoPath, TrashId, tip)).Success);

        // Asked to delete at the wrong value: it must refuse and leave the ref.
        Assert.False(await _git.DeleteTrashKeepRefAsync(_repoPath, TrashId, mainTip));
        Assert.Equal(tip, (await RunRawGitAsync(_repoPath, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{TrashId}")).Output.Trim());
    }

    [Fact]
    public async Task CreateTrashKeepRefAsync_is_create_only()
    {
        var tip = await CreateUnmergedBranchAsync(_repoPath);
        Assert.True((await _git.CreateTrashKeepRefAsync(_repoPath, TrashId, tip)).Success);
        // A second create for the same id must fail (the ref already exists).
        var mainTip = (await RunRawGitAsync(_repoPath, "rev-parse", "refs/heads/main")).Output.Trim();
        Assert.False((await _git.CreateTrashKeepRefAsync(_repoPath, TrashId, mainTip)).Success);
        // Unchanged.
        Assert.Equal(tip, (await RunRawGitAsync(_repoPath, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{TrashId}")).Output.Trim());
    }

    // --- Blockers: the keep-ref must be created and survive, or the save fails closed ---

    [Theory]
    [InlineData("refs")]
    [InlineData("refs/gitsweep")]
    [InlineData("refs/gitsweep/trash")]
    public async Task A_packed_blocker_at_the_keep_ref_path_makes_the_save_fail_closed(string blocker)
    {
        // A hostile packed-refs line at refs, refs/gitsweep or
        // refs/gitsweep/trash makes `update-ref` refuse to create the keep-ref.
        // The save must then fail, so the branch is never deleted.
        var tip = await CreateUnmergedBranchAsync(_repoPath);
        var mainTip = (await RunRawGitAsync(_repoPath, "rev-parse", "refs/heads/main")).Output.Trim();
        await File.AppendAllTextAsync(Path.Combine(_repoPath, ".git", "packed-refs"), $"{mainTip} {blocker}\n");

        Assert.False((await _git.CreateTrashKeepRefAsync(_repoPath, TrashId, tip)).Success);

        var trash = new TrashService(_git);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => trash.SaveAsync(_repoPath, WipBranch, tip, Guid.NewGuid()));
        // No trash entry and no keep-ref were left behind.
        Assert.Empty(await trash.ListAsync(_repoPath));
    }

    [Fact]
    public async Task A_file_in_the_way_of_the_keep_ref_makes_the_save_fail_closed()
    {
        var tip = await CreateUnmergedBranchAsync(_repoPath);
        Directory.CreateDirectory(Path.Combine(_repoPath, ".git", "refs", "gitsweep"));
        await File.WriteAllTextAsync(Path.Combine(_repoPath, ".git", "refs", "gitsweep", "trash"), "blocker");

        Assert.False((await _git.CreateTrashKeepRefAsync(_repoPath, TrashId, tip)).Success);
        var trash = new TrashService(_git);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => trash.SaveAsync(_repoPath, WipBranch, tip, Guid.NewGuid()));
    }

    // Links on the keep-ref's path (refs/gitsweep, refs/gitsweep/trash, refs)
    // and aliases elsewhere in the ref store: see KeepRefAliasTests (T4R3-01).

    // --- helpers ---

    private async Task<string> CreateRepositoryAsync(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        await RunGitAsync(path, "init", "-b", "main");
        await RunGitAsync(path, "config", "user.email", "tests@example.com");
        await RunGitAsync(path, "config", "user.name", "GitSweep Tests");
        await CommitFileAsync(path, "README.md", "initial commit");
        return path;
    }

    // Self-upstream branch with one unique unmerged commit, no payload.
    private static async Task<string> CreateUnmergedBranchAsync(string repo)
    {
        await RunGitAsync(repo, "checkout", "-b", WipBranch);
        var tip = await CommitFileAsync(repo, "wip.txt", "unpushed work");
        await RunGitAsync(repo, "checkout", "main");
        await RunGitAsync(repo, "config", $"branch.{WipBranch}.remote", ".");
        await RunGitAsync(repo, "config", $"branch.{WipBranch}.merge", $"refs/heads/{WipBranch}");
        return tip;
    }

    // Builds the T4R2-01 state: a stale packed copy of the branch that `-d`
    // leaves behind, the self-upstream config, the alias, and the user's
    // unique commit on the loose branch. Returns the user's tip.
    private async Task<string> BuildHostileSelfUpstreamRepoAsync(string repo, string payload)
    {
        var mainTip = (await RunGitAsync(repo, "rev-parse", "refs/heads/main")).Trim();
        await RunGitAsync(repo, "checkout", "-b", WipBranch);
        var shipped = await CommitFileAsync(repo, "wip.txt", "shipped work");
        await RunGitAsync(repo, "checkout", "main");

        var packed = payload switch
        {
            "lyingsorted" =>
                $"# pack-refs with: peeled fully-peeled sorted\n{mainTip} refs/tags/zzz\n{shipped} refs/heads/{WipBranch}\n",
            "dup" =>
                $"{shipped} refs/heads/{WipBranch}\n{mainTip} refs/heads/{WipBranch}\n",
            _ => throw new ArgumentOutOfRangeException(nameof(payload))
        };
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "packed-refs"), packed);

        await RunGitAsync(repo, "config", $"branch.{WipBranch}.remote", ".");
        await RunGitAsync(repo, "config", $"branch.{WipBranch}.merge", $"refs/heads/{WipBranch}");

        var refs = Path.Combine(repo, ".git", "refs");
        Directory.CreateDirectory(Path.Combine(refs, "remotes"));
        var alias = Path.Combine(refs, "remotes", "alias");
        var target = Path.Combine(refs, "heads", "feature");
        if (OperatingSystem.IsWindows()) TestLinks.CreateJunction(alias, target);
        else TestLinks.CreateDirectoryLink(alias, target);

        // The user commits unique work on the loose branch (it shadows the
        // packed copy until `-d` removes the loose file).
        await RunGitAsync(repo, "checkout", WipBranch);
        var tip = await CommitFileAsync(repo, "user.txt", "unique user work");
        await RunGitAsync(repo, "checkout", "main");
        return tip;
    }

    private static async Task SetHostileGcConfigAsync(string repo)
    {
        await RunGitAsync(repo, "config", "gc.reflogExpire", "now");
        await RunGitAsync(repo, "config", "gc.reflogExpireUnreachable", "now");
        await RunGitAsync(repo, "config", "gc.pruneExpire", "now");
    }

    private static async Task<string> CommitFileAsync(string repo, string fileName, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, fileName), message);
        await RunGitAsync(repo, "add", ".");
        await RunGitAsync(repo, "commit", "-m", message, "--no-gpg-sign");
        return (await RunGitAsync(repo, "rev-parse", "HEAD")).Trim();
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunRawGitAsync(workingDirectory, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}{result.Output}");
        return result.Output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunRawGitAsync(
        string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutableLocator.GetRequiredPath(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
