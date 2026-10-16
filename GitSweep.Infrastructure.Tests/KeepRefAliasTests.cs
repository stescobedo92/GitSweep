using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// T4R3-01: a directory link at <c>.git/refs/gitsweep</c> (or
/// <c>.git/refs/gitsweep/trash</c>) into <c>refs/remotes/&lt;remote&gt;</c> or
/// <c>refs/tags</c> made git write the keep-ref there physically. The keep-ref
/// then doubled as an ordinary remote-tracking ref or tag: the pre-check
/// counted it as "another ref" (so a self-upstream branch with unique work was
/// deleted), and the next <c>fetch --prune</c> deleted it, leaving the commit
/// to gc. The keep-ref must be un-aliasable: a linked directory on its path
/// makes the save fail closed, an alias anywhere else is detected after the
/// create and also makes the save fail closed, and the reachability checks
/// never count a keep-ref alias even if one slipped through. Real temp repos
/// under the system temp dir; junctions on Windows, relative directory
/// symlinks elsewhere (what <c>ln -s</c> in a tarball ships).
/// </summary>
public sealed class KeepRefAliasTests : IAsyncLifetime
{
    private const string WipBranch = "feature/wip";
    private const string TrashId = "0123456789abcdef0123456789abcdef";
    private const string RemotesPruneRefspec = "+refs/heads/*:refs/remotes/origin/*";
    private const string TagsPruneRefspec = "+refs/tags/*:refs/tags/*";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _git = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private static string UnreachableMessage(string branchName) =>
        $"Branch '{branchName}' has commits that are not reachable from any other branch, remote-tracking branch or tag; it was not deleted.";

    // --- 1. A linked directory on the keep-ref's own path: the save fails closed ---

    [DirectoryLinkTheory]
    [InlineData("refs/gitsweep", "refs/remotes/origin", RemotesPruneRefspec)]
    [InlineData("refs/gitsweep", "refs/tags", TagsPruneRefspec)]
    [InlineData("refs/gitsweep/trash", "refs/remotes/origin", RemotesPruneRefspec)]
    public async Task Link_that_redirects_the_keep_ref_into_a_pruned_namespace_makes_the_save_fail_closed(
        string linkAt, string target, string pruneRefspec)
    {
        // Positive control (no guard, plain git): in the same state the
        // keep-ref lands physically under the target namespace, the
        // reachability listing counts it as another ref, and a prune of that
        // namespace deletes it.
        var (control, controlOrigin) = await CreateRepositoryWithOriginAsync("control");
        var controlTip = await CreateSelfUpstreamBranchAsync(control);
        PlantDirectoryLink(control, linkAt, target);
        await RawCreateKeepRefAsync(control, TrashId, controlTip);
        Assert.True(File.Exists(PhysicalKeepRefPath(control, linkAt, target, TrashId)),
            "control: the keep-ref was not redirected into the target namespace");
        Assert.Contains(await ContainingRefsAsync(control, controlTip), name => name != $"refs/heads/{WipBranch}");
        var prune = await RunRawGitAsync(control, "fetch", "--prune", "--no-tags", controlOrigin, pruneRefspec);
        Assert.True(prune.ExitCode == 0, $"control fetch failed: {prune.Error}");
        Assert.False(File.Exists(PhysicalKeepRefPath(control, linkAt, target, TrashId)), "control: the prune did not delete the keep-ref");
        Assert.NotEqual(0, (await RunRawGitAsync(control, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{TrashId}")).ExitCode);

        // Through GitSweep: the save refuses before any keep-ref exists, so
        // the caller never deletes the branch and nothing is written.
        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        PlantDirectoryLink(repo, linkAt, target);
        var refsBefore = await AllRefsAsync(repo);
        var targetEntriesBefore = EntriesUnder(repo, target);

        var trash = new TrashService(_git);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => trash.SaveAsync(repo, WipBranch, tip, Guid.NewGuid()));

        Assert.Contains($"'{linkAt}'", ex.Message);
        Assert.Contains("symbolic link", ex.Message);
        await AssertNothingChangedAsync(repo, tip, refsBefore);
        Assert.Equal(targetEntriesBefore, EntriesUnder(repo, target));
        Assert.Empty(await trash.ListAsync(repo));
    }

    [DirectoryLinkFact]
    public async Task Link_that_redirects_the_keep_ref_into_refs_heads_makes_the_save_fail_closed()
    {
        // Control: through a link into refs/heads the keep-ref would become a
        // branch named trash/<id>, which a later clean could offer for deletion.
        var (control, _) = await CreateRepositoryWithOriginAsync("control");
        var controlTip = await CreateSelfUpstreamBranchAsync(control);
        PlantDirectoryLink(control, "refs/gitsweep", "refs/heads");
        await RawCreateKeepRefAsync(control, TrashId, controlTip);
        Assert.Equal(controlTip, (await RunGitAsync(control, "rev-parse", "--verify", $"refs/heads/trash/{TrashId}")).Trim());

        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        PlantDirectoryLink(repo, "refs/gitsweep", "refs/heads");
        var refsBefore = await AllRefsAsync(repo);

        var trash = new TrashService(_git);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => trash.SaveAsync(repo, WipBranch, tip, Guid.NewGuid()));

        Assert.Contains("'refs/gitsweep'", ex.Message);
        await AssertNothingChangedAsync(repo, tip, refsBefore);
    }

    [DirectoryLinkFact]
    public async Task Dangling_link_at_refs_gitsweep_makes_the_save_fail_closed()
    {
        // A link whose target does not exist is still a link: it must not be
        // mistaken for "absent" (Directory.Exists follows links).
        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        PlantDirectoryLink(repo, "refs/gitsweep", "refs/remotes/origin/gone");
        Directory.Delete(Path.Combine(repo, ".git", "refs", "remotes", "origin", "gone"));
        var refsBefore = await AllRefsAsync(repo);

        var trash = new TrashService(_git);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => trash.SaveAsync(repo, WipBranch, tip, Guid.NewGuid()));

        Assert.Contains("'refs/gitsweep'", ex.Message);
        Assert.Contains("symbolic link", ex.Message);
        await AssertNothingChangedAsync(repo, tip, refsBefore);
    }

    [DirectoryLinkFact]
    public async Task Linked_refs_directory_makes_the_save_fail_closed()
    {
        // The whole ref store behind a link: git still works through it
        // (control), but GitSweep only creates a keep-ref through real
        // directories, so the save fails closed.
        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        var gitDir = Path.Combine(repo, ".git");
        Directory.Move(Path.Combine(gitDir, "refs"), Path.Combine(gitDir, "refs-real"));
        PlantDirectoryLink(repo, "refs", "refs-real");
        Assert.Equal(tip, (await RunGitAsync(repo, "rev-parse", "--verify", $"refs/heads/{WipBranch}")).Trim());
        var refsBefore = await AllRefsAsync(repo);

        var trash = new TrashService(_git);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => trash.SaveAsync(repo, WipBranch, tip, Guid.NewGuid()));

        Assert.Contains("'refs'", ex.Message);
        await AssertNothingChangedAsync(repo, tip, refsBefore);
    }

    // --- 2. Alias detection after the create, independent of the filesystem guard ---

    [DirectoryLinkTheory]
    [InlineData("refs/remotes/origin/trash")]
    // A broken name: for-each-ref only warns about it on stderr, yet
    // `fetch --prune` still deletes it (and the keep-ref file with it).
    [InlineData("refs/remotes/origin/a..b")]
    public async Task Alias_of_the_real_keep_ref_directory_is_detected_and_the_save_fails_closed(string aliasAt)
    {
        // refs/gitsweep and refs/gitsweep/trash are real directories, so the
        // filesystem guard on the keep-ref's own path passes; the alias is a
        // link elsewhere in the ref store that points at refs/gitsweep/trash.
        // Positive control: a plain create works there and a fetch --prune
        // deletes the keep-ref through the alias.
        var (control, controlOrigin) = await CreateRepositoryWithOriginAsync("control");
        var controlTip = await CreateSelfUpstreamBranchAsync(control);
        PlantDirectoryLink(control, aliasAt, "refs/gitsweep/trash");
        await RawCreateKeepRefAsync(control, TrashId, controlTip);
        var controlListing = await RunRawGitAsync(control, "for-each-ref", "--points-at", controlTip, "--format=%(refname)");
        Assert.True(
            controlListing.Output.Contains($"{aliasAt}/{TrashId}") || controlListing.Error.Contains($"{aliasAt}/{TrashId}"),
            "control: git does not see the alias");
        var prune = await RunRawGitAsync(control, "fetch", "--prune", "--no-tags", controlOrigin, RemotesPruneRefspec);
        Assert.True(prune.ExitCode == 0, $"control fetch failed: {prune.Error}");
        Assert.NotEqual(0, (await RunRawGitAsync(control, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{TrashId}")).ExitCode);

        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        PlantDirectoryLink(repo, aliasAt, "refs/gitsweep/trash");
        var refsBefore = await AllRefsAsync(repo);

        var trash = new TrashService(_git);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => trash.SaveAsync(repo, WipBranch, tip, Guid.NewGuid()));

        // Refused by the alias detection, not by the filesystem guard.
        Assert.Contains("another ref name", ex.Message);
        Assert.DoesNotContain("symbolic link", ex.Message);
        // The keep-ref it created was removed again, and nothing else changed.
        var keepRefDirectory = Path.Combine(repo, ".git", "refs", "gitsweep", "trash");
        Assert.True(!Directory.Exists(keepRefDirectory) || Directory.GetFiles(keepRefDirectory).Length == 0,
            "the refused keep-ref was left behind");
        await AssertNothingChangedAsync(repo, tip, refsBefore);
        Assert.Empty(await trash.ListAsync(repo));
    }

    [Fact]
    public async Task Distinct_ref_named_with_the_keep_ref_id_is_refused_as_an_alias_and_left_untouched()
    {
        // No link at all: another ref whose last component is the keep-ref id
        // at the same commit is indistinguishable, by name, from an alias, so
        // the create is refused. The cleanup is a compare-and-delete of the
        // keep-ref's own name only: the other ref is never touched.
        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        await RunGitAsync(repo, "update-ref", $"refs/remotes/origin/{TrashId}", tip);

        var (success, message) = await CreateKeepRefAsync(_git, repo, TrashId, tip);

        Assert.False(success);
        Assert.Contains("another ref name", message);
        Assert.Equal(1, (await RunRawGitAsync(repo, "rev-parse", "--verify", "--quiet", $"refs/gitsweep/trash/{TrashId}")).ExitCode);
        Assert.Equal(tip, (await RunGitAsync(repo, "rev-parse", "--verify", $"refs/remotes/origin/{TrashId}")).Trim());
    }

    // --- 3. The reachability checks never count a keep-ref alias that slipped through ---

    [DirectoryLinkTheory]
    [InlineData("refs/gitsweep", "refs/remotes/origin")]
    [InlineData("refs/gitsweep", "refs/tags")]
    [InlineData("refs/gitsweep/trash", "refs/remotes/origin")]
    public async Task Pre_check_never_counts_an_aliased_keep_ref_created_in_this_run(string linkAt, string target)
    {
        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        PlantDirectoryLink(repo, linkAt, target);

        // This run asks for the keep-ref (the guard refuses it), and then a
        // hypothetical regression lets the aliased keep-ref be created anyway.
        // (Through a leaf junction, git for Windows writes the file into the
        // target and then drops the junction, so the keep-ref name itself may
        // stop resolving; the alias under the target namespace is what counts.)
        await CreateKeepRefAsync(_git, repo, TrashId, tip);
        await RunRawGitAsync(repo, "update-ref", "--no-deref", $"refs/gitsweep/trash/{TrashId}", tip, new string('0', tip.Length));
        // Control: the raw listing the pre-check reads counts the alias.
        Assert.Contains(await ContainingRefsAsync(repo, tip), name => name.EndsWith("/" + TrashId, StringComparison.Ordinal));

        var (success, message) = await _git.DeleteBranchAsync(repo, WipBranch);

        Assert.False(success);
        Assert.Equal(UnreachableMessage(WipBranch), message);
        Assert.Equal(tip, await _git.GetBranchTipShaAsync(repo, WipBranch));
        // `git branch -d` never ran: the self-upstream config is still there.
        Assert.Equal(".", (await RunGitAsync(repo, "config", "--get", $"branch.{WipBranch}.remote")).Trim());
    }

    [DirectoryLinkFact]
    public async Task Pre_check_never_counts_an_aliased_keep_ref_listed_under_refs_gitsweep()
    {
        // A fresh service that never asked for the keep-ref still excludes it:
        // the ids of the keep-refs git lists under refs/gitsweep/ are excluded too.
        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        PlantDirectoryLink(repo, "refs/gitsweep", "refs/remotes/origin");
        await RawCreateKeepRefAsync(repo, TrashId, tip);
        Assert.Contains(await ContainingRefsAsync(repo, tip), name => name == $"refs/remotes/origin/trash/{TrashId}");

        var (success, message) = await new GitRepositoryService().DeleteBranchAsync(repo, WipBranch);

        Assert.False(success);
        Assert.Equal(UnreachableMessage(WipBranch), message);
        Assert.Equal(tip, await _git.GetBranchTipShaAsync(repo, WipBranch));
        Assert.Equal(".", (await RunGitAsync(repo, "config", "--get", $"branch.{WipBranch}.remote")).Trim());
    }

    [DirectoryLinkFact]
    public async Task Post_check_never_counts_an_aliased_keep_ref()
    {
        // The T4-01 alias of the branch's own ref file lets the pre-check pass
        // and vanishes with `-d`; an aliased keep-ref (refs/gitsweep -> refs/tags)
        // survives `-d`. The post-check must not count the keep-ref alias as
        // the other ref: it recreates the branch and reports it not deleted.
        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        PlantDirectoryLink(repo, "refs/remotes/alias", "refs/heads/feature");
        PlantDirectoryLink(repo, "refs/gitsweep", "refs/tags");
        await CreateKeepRefAsync(_git, repo, TrashId, tip);
        await RunRawGitAsync(repo, "update-ref", "--no-deref", $"refs/gitsweep/trash/{TrashId}", tip, new string('0', tip.Length));
        Assert.Equal(tip, (await RunGitAsync(repo, "rev-parse", "--verify", $"refs/tags/trash/{TrashId}")).Trim());

        var (success, message) = await _git.DeleteBranchAsync(repo, WipBranch);

        Assert.False(success);
        Assert.Equal(UnreachableMessage(WipBranch), message);
        Assert.Equal(tip, await _git.GetBranchTipShaAsync(repo, WipBranch));
        // `-d` did run (it removed the config section) and the post-check undid it.
        Assert.NotEqual(0, (await RunRawGitAsync(repo, "config", "--get", $"branch.{WipBranch}.remote")).ExitCode);
    }

    [Fact]
    public async Task Real_ref_whose_name_only_looks_like_a_trash_id_still_counts()
    {
        // The exclusion is by the ids of actual keep-refs, never by shape: a
        // tag named with some other 32-hex string still keeps the commits,
        // so the branch is deleted, and its own (un-aliased) keep-ref holds it.
        var (repo, _) = await CreateRepositoryWithOriginAsync("repo");
        var tip = await CreateSelfUpstreamBranchAsync(repo);
        await RunGitAsync(repo, "tag", Guid.NewGuid().ToString("N"), tip);
        var entry = await new TrashService(_git).SaveAsync(repo, WipBranch, tip, Guid.NewGuid());

        var (success, message) = await _git.DeleteBranchAsync(repo, WipBranch);

        Assert.True(success, message);
        Assert.Null(await _git.GetBranchTipShaAsync(repo, WipBranch));
        Assert.Equal(tip, (await RunGitAsync(repo, "rev-parse", "--verify", $"refs/gitsweep/trash/{entry.Id}")).Trim());
    }

    // --- helpers ---

    // The only direct call of the keep-ref API in this file.
    private static Task<(bool Success, string ErrorMessage)> CreateKeepRefAsync(
        GitRepositoryService git, string repo, string trashId, string sha) =>
        git.CreateTrashKeepRefAsync(repo, trashId, sha);

    private async Task<(string Repo, string Origin)> CreateRepositoryWithOriginAsync(string name)
    {
        var origin = Path.Combine(_root, name + ".origin");
        Directory.CreateDirectory(origin);
        await RunGitAsync(origin, "init", "--bare");

        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        await RunGitAsync(repo, "init", "-b", "main");
        await RunGitAsync(repo, "config", "user.email", "tests@example.com");
        await RunGitAsync(repo, "config", "user.name", "GitSweep Tests");
        await CommitFileAsync(repo, "README.md", "initial commit");
        await RunGitAsync(repo, "remote", "add", "origin", origin);
        await RunGitAsync(repo, "push", "origin", "main");
        await RunGitAsync(repo, "fetch", "origin");
        return (repo, origin);
    }

    // Self-upstream branch with one unique, unpushed commit: plain `-d` deletes it.
    private static async Task<string> CreateSelfUpstreamBranchAsync(string repo)
    {
        await RunGitAsync(repo, "checkout", "-b", WipBranch);
        var tip = await CommitFileAsync(repo, "wip.txt", "unique unpushed work");
        await RunGitAsync(repo, "checkout", "main");
        await RunGitAsync(repo, "config", $"branch.{WipBranch}.remote", ".");
        await RunGitAsync(repo, "config", $"branch.{WipBranch}.merge", $"refs/heads/{WipBranch}");
        Assert.Equal(new[] { $"refs/heads/{WipBranch}" }, await ContainingRefsAsync(repo, tip));
        return tip;
    }

    // Links .git/<linkAt> to .git/<target> (creating the target and the
    // link's parent as real directories): a junction on Windows (no privilege
    // needed), a relative directory symlink elsewhere.
    private static void PlantDirectoryLink(string repo, string linkAt, string target)
    {
        var gitDir = Path.Combine(repo, ".git");
        var link = Path.Combine(gitDir, linkAt.Replace('/', Path.DirectorySeparatorChar));
        var targetPath = Path.Combine(gitDir, target.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(targetPath);
        if (OperatingSystem.IsWindows())
        {
            TestLinks.CreateJunction(link, targetPath);
        }
        else
        {
            Directory.CreateSymbolicLink(link, Path.GetRelativePath(Path.GetDirectoryName(link)!, targetPath));
        }
    }

    // Where the keep-ref file physically lands when .git/<linkAt> links to .git/<target>.
    private static string PhysicalKeepRefPath(string repo, string linkAt, string target, string trashId)
    {
        var keepRef = $"refs/gitsweep/trash/{trashId}";
        var remainder = keepRef[(linkAt.Length + 1)..];
        return Path.Combine(repo, ".git", target.Replace('/', Path.DirectorySeparatorChar), remainder.Replace('/', Path.DirectorySeparatorChar));
    }

    private static async Task RawCreateKeepRefAsync(string repo, string trashId, string sha) =>
        await RunGitAsync(repo, "update-ref", "--no-deref", $"refs/gitsweep/trash/{trashId}", sha, new string('0', sha.Length));

    // What the reachability pre-check reads, without its filtering.
    private static async Task<string[]> ContainingRefsAsync(string repo, string sha) =>
        (await RunGitAsync(repo, "for-each-ref", "--contains", sha, "--format=%(refname)", "refs/heads/", "refs/remotes/", "refs/tags/"))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<string> AllRefsAsync(string repo)
    {
        var result = await RunRawGitAsync(repo, "for-each-ref", "--format=%(refname) %(objectname)");
        return result.Output;
    }

    private static string[] EntriesUnder(string repo, string relativeDirectory)
    {
        var directory = Path.Combine(repo, ".git", relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        return Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(entry => Path.GetRelativePath(directory, entry))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();
    }

    // The failed save left the repository as it was: every ref unchanged, the
    // branch at its tip with its config (so `-d` never ran), and no ref other
    // than the branch itself contains the tip.
    private async Task AssertNothingChangedAsync(string repo, string tip, string refsBefore)
    {
        Assert.Equal(refsBefore, await AllRefsAsync(repo));
        Assert.Equal(tip, await _git.GetBranchTipShaAsync(repo, WipBranch));
        Assert.Equal(".", (await RunGitAsync(repo, "config", "--get", $"branch.{WipBranch}.remote")).Trim());
        Assert.Equal(new[] { $"refs/heads/{WipBranch}" }, await ContainingRefsAsync(repo, tip));
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
