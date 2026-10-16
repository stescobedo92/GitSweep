using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// SEC-B04: <c>git branch -d</c> accepts a branch that is "fully merged in
/// its upstream", and the upstream comes from repository config. A hostile
/// <c>.git/config</c> can make a branch its own upstream
/// (<c>branch.&lt;b&gt;.remote=.</c>, <c>branch.&lt;b&gt;.merge=refs/heads/&lt;b&gt;</c>),
/// so <c>-d</c> alone deletes unique, unpushed work whose only remaining
/// record is the trash SHA (the reflog goes with the branch, gc can prune
/// the commits). <see cref="GitRepositoryService.DeleteBranchAsync"/> must
/// first require, independently of any config, that the tip is contained in
/// another branch, remote-tracking branch or tag, and only then run
/// <c>git branch -d</c>. All repositories live under the system temp dir.
/// </summary>
public sealed class DeleteReachabilityTests : IAsyncLifetime
{
    private const string WipBranch = "feature/wip";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _sut = new();
    private string _repoPath = "";

    public async Task InitializeAsync()
    {
        _repoPath = await CreateRepositoryAsync("repo");
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private static string UnreachableMessage(string branchName) =>
        $"Branch '{branchName}' has commits that are not reachable from any other branch, remote-tracking branch or tag; it was not deleted.";

    [Fact]
    public async Task Self_upstream_branch_with_unique_commits_is_not_deleted()
    {
        // Positive control in a throwaway repository built the same way:
        // with the branch configured as its own upstream, plain
        // `git branch -d` does delete the unmerged branch. Without this
        // proof the refusal below could pass vacuously.
        var control = await CreateRepositoryAsync("control");
        var controlTip = await CreateSelfUpstreamBranchAsync(control, WipBranch);
        Assert.Equal(
            $"refs/heads/{WipBranch}",
            (await RunGitAsync(control, "rev-parse", "--symbolic-full-name", $"{WipBranch}@{{upstream}}")).Trim());
        Assert.Equal(1, (await RunRawGitAsync(control, null, "merge-base", "--is-ancestor", controlTip, "main")).ExitCode);
        var plainDelete = await RunRawGitAsync(control, null, "branch", "-d", "--", WipBranch);
        Assert.True(plainDelete.ExitCode == 0, $"plain `git branch -d` refused the self-upstream branch: {plainDelete.Error}");
        Assert.False(await _sut.BranchExistsAsync(control, WipBranch));

        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);

        Assert.False(success);
        Assert.Equal(UnreachableMessage(WipBranch), message);
        Assert.DoesNotContain("not fully merged", message, StringComparison.OrdinalIgnoreCase);
        // `git branch -d` was never reached: with this config it would have
        // deleted the branch (control above). It still exists, tip unchanged.
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, WipBranch));
    }

    [Fact]
    public async Task Branch_merged_into_the_default_branch_is_still_deleted()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/merged");
        await CommitFileAsync(_repoPath, "merged.txt", "merged work");
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "merge", "--no-ff", "--no-gpg-sign", "-m", "merge feature/merged", "feature/merged");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, "feature/merged");

        Assert.True(success, message);
        Assert.False(await _sut.BranchExistsAsync(_repoPath, "feature/merged"));
    }

    [Fact]
    public async Task Branch_contained_only_in_its_remote_tracking_ref_is_still_deleted()
    {
        // Pushed but not merged: the commits live on in
        // refs/remotes/origin/<b>, and git's own -d accepts the branch
        // because it is merged into that (genuine) upstream.
        await RunGitAsync(_repoPath, "remote", "add", "origin", Path.Combine(_root, "unused.git"));
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/pushed");
        var tip = await CommitFileAsync(_repoPath, "pushed.txt", "pushed work");
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "update-ref", "refs/remotes/origin/feature/pushed", tip);
        await RunGitAsync(_repoPath, "config", "branch.feature/pushed.remote", "origin");
        await RunGitAsync(_repoPath, "config", "branch.feature/pushed.merge", "refs/heads/feature/pushed");
        Assert.Equal(1, (await RunRawGitAsync(_repoPath, null, "merge-base", "--is-ancestor", tip, "main")).ExitCode);

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, "feature/pushed");

        Assert.True(success, message);
        Assert.False(await _sut.BranchExistsAsync(_repoPath, "feature/pushed"));
        Assert.Equal(tip, (await RunGitAsync(_repoPath, "rev-parse", "refs/remotes/origin/feature/pushed")).Trim());
    }

    [Fact]
    public async Task Branch_with_unique_commits_and_no_upstream_is_not_deleted()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/local");
        var tip = await CommitFileAsync(_repoPath, "local.txt", "local work");
        await RunGitAsync(_repoPath, "checkout", "main");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, "feature/local");

        // The reachability check runs first, so the refusal is GitSweep's
        // own message rather than git's "not fully merged".
        Assert.False(success);
        Assert.Equal(UnreachableMessage("feature/local"), message);
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, "feature/local"));
    }

    [Fact]
    public async Task Symbolic_ref_pointing_at_the_branch_does_not_count_as_another_ref()
    {
        // A hostile repository can also plant a symbolic ref (here
        // origin/HEAD) whose target is the branch itself. for-each-ref
        // --contains lists it, but it holds no commit of its own: once the
        // branch is deleted it dangles and the commits are unreachable.
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);
        await RunGitAsync(_repoPath, "symbolic-ref", "refs/remotes/origin/HEAD", $"refs/heads/{WipBranch}");
        var listed = await RunGitAsync(
            _repoPath, "for-each-ref", "--contains", tip, "--format=%(refname)", "refs/heads/", "refs/remotes/", "refs/tags/");
        Assert.Contains("refs/remotes/origin/HEAD", listed.Split('\n').Select(line => line.Trim()));

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);

        Assert.False(success);
        Assert.Equal(UnreachableMessage(WipBranch), message);
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, WipBranch));
    }

    [Fact]
    public async Task Self_upstream_branch_whose_commits_an_annotated_tag_keeps_is_deleted()
    {
        // The tag keeps the commits reachable (gc never prunes them, undo
        // can restore), so the hostile config cannot cause data loss here.
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);
        await RunGitAsync(_repoPath, "tag", "-a", "--no-sign", "-m", "keep", "keep/wip", tip);

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);

        Assert.True(success, message);
        Assert.False(await _sut.BranchExistsAsync(_repoPath, WipBranch));
    }

    [Fact]
    public async Task Limit_failure_in_the_reachability_check_throws_before_branch_d_is_reached()
    {
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);

        // 400 tags keep the tip reachable and make the for-each-ref listing
        // (~15,000 characters) far larger than the stdout of the guards that
        // run before it (current branch, worktree list, tip SHA: a few
        // hundred characters), so only the reachability listing hits the cap.
        var updates = new StringBuilder();
        for (var i = 0; i < 400; i++)
        {
            updates.Append($"create refs/tags/reachability/keep-{i:D3} {tip}\n");
        }

        await RunGitWithInputAsync(_repoPath, updates.ToString(), "update-ref", "--stdin");
        var capped = new GitRepositoryService(
            new GitCommandLimits(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 4096, 1024 * 1024));

        // Fail closed like the current/worktree guards: the limit failure is
        // never read as an answer, and `git branch -d` is never run.
        var ex = await Assert.ThrowsAsync<GitCommandLimitException>(() => capped.DeleteBranchAsync(_repoPath, WipBranch));

        Assert.Equal("git for-each-ref output exceeded the 4,096-character limit.", ex.Message);
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, WipBranch));

        // Same repository, default limits: the tags keep the tip reachable
        // and the self-upstream config makes `-d` accept it, so the cap alone
        // is what stopped the delete above.
        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);
        Assert.True(success, message);
        Assert.False(await _sut.BranchExistsAsync(_repoPath, WipBranch));
    }

    // T4-01: a filesystem alias of the branch's own loose ref file is listed
    // by for-each-ref as an ordinary ref (empty %(symref)), so it passes the
    // pre-delete check, and it vanishes together with the branch. Only the
    // post-delete check sees the real state; it must put the branch back.

    [DirectoryLinkFact]
    public async Task Directory_alias_of_the_branch_ref_cannot_get_its_commits_deleted()
    {
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);
        CreateBranchDirectoryAlias(_repoPath);
        await AssertListedAsOrdinaryRefAsync(_repoPath, tip, "refs/remotes/alias/wip");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);

        await AssertDeletionWasUndoneAsync(success, message, tip);
    }

    [DirectoryLinkFact]
    public async Task Post_delete_recreation_uses_the_tip_even_when_a_tag_is_named_like_it()
    {
        // O-F1: the post-delete recreation ran `git branch -- <name> <sha>`,
        // which git resolves through a ref named <sha>. With such a tag in
        // place the recreated branch must still point at its own tip.
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);
        var mainTip = (await RunGitAsync(_repoPath, "rev-parse", "refs/heads/main")).Trim();
        await RunGitAsync(_repoPath, "tag", tip, mainTip);

        // Positive control: plain `git branch -- <name> <tip>` follows the tag.
        await RunGitAsync(_repoPath, "branch", "--", "control", tip);
        Assert.Equal(mainTip, (await RunGitAsync(_repoPath, "rev-parse", "refs/heads/control")).Trim());
        await RunGitAsync(_repoPath, "branch", "-D", "control");

        CreateBranchDirectoryAlias(_repoPath);
        await AssertListedAsOrdinaryRefAsync(_repoPath, tip, "refs/remotes/alias/wip");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);

        await AssertDeletionWasUndoneAsync(success, message, tip);
    }

    [FileSymlinkFact]
    public async Task File_symlink_alias_of_the_branch_ref_cannot_get_its_commits_deleted()
    {
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);
        // Git reads a symlink as a symref only when its target text starts
        // with "refs/"; a relative target is followed and read as a ref.
        var tags = Path.Combine(_repoPath, ".git", "refs", "tags");
        Directory.CreateDirectory(tags);
        TestLinks.CreateFileSymlink(Path.Combine(tags, "alias"), Path.Combine("..", "heads", "feature", "wip"));
        await AssertListedAsOrdinaryRefAsync(_repoPath, tip, "refs/tags/alias");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);

        await AssertDeletionWasUndoneAsync(success, message, tip);
    }

    [DirectoryLinkFact]
    public async Task Branch_under_a_ref_that_blocks_its_recreation_is_not_deleted()
    {
        // A hostile packed-refs can hold refs/heads/feature/wip/sub next to
        // the loose refs/heads/feature/wip (git never creates that pair, but
        // the user can still commit on the branch). `-d` deletes the branch,
        // and git then refuses to create it again: the post-delete check
        // could not put it back. Such a branch must not be deleted at all.
        await AssertRecreationBlockerIsRefusedAsync(
            repo => PlantPackedRefAsync(repo, "refs/heads/feature/wip/sub"), NotRecreatableMessage(WipBranch));
    }

    [DirectoryLinkFact]
    public async Task Branch_whose_parent_name_is_a_ref_is_not_deleted()
    {
        // The other direction of the same directory/file conflict: a packed
        // refs/heads/feature blocks creating refs/heads/feature/wip.
        await AssertRecreationBlockerIsRefusedAsync(
            repo => PlantPackedRefAsync(repo, "refs/heads/feature"), NotRecreatableMessage(WipBranch));
    }

    // T4R1-01: every proper prefix of refs/heads/<name> blocks recreation,
    // up to `refs` itself. A packed-refs line can name any of them, and git
    // still lets the user check out and commit on existing branches.
    [DirectoryLinkTheory]
    [InlineData("refs/heads")]
    [InlineData("refs")]
    public async Task Branch_under_a_packed_ref_named_like_a_higher_prefix_is_not_deleted(string prefix)
    {
        await AssertRecreationBlockerIsRefusedAsync(
            repo => PlantPackedRefAsync(repo, prefix), NotRecreatableMessage(WipBranch));
    }

    [DirectoryLinkTheory]
    [InlineData("refs")]
    [InlineData("refs/heads")]
    [InlineData("refs/heads/feature")]
    [InlineData("refs/heads/feature/wip/x")]
    public async Task Packed_ref_with_a_missing_object_still_blocks_recreation_and_is_refused(string blocker)
    {
        // A ref whose object does not exist is broken, but it still occupies
        // its name for git's own directory/file check.
        await AssertRecreationBlockerIsRefusedAsync(
            repo => PlantPackedRefAsync(repo, blocker, DanglingSha), NotRecreatableMessage(WipBranch));
    }

    [DirectoryLinkFact]
    public async Task Descendant_with_a_broken_name_that_listing_ignores_is_refused()
    {
        // for-each-ref skips a ref with an invalid name (it only warns on
        // stderr), yet git's directory/file check still counts it.
        await AssertRecreationBlockerIsRefusedAsync(
            repo => PlantPackedRefAsync(repo, "refs/heads/feature/wip/a..b"), NotRecreatableMessage(WipBranch));
    }

    [DirectoryLinkFact]
    public async Task Exact_prefix_ref_hidden_by_an_ambiguous_namesake_is_refused()
    {
        // With both a packed refs/heads and a tag refs/tags/refs/heads,
        // `rev-parse --verify --symbolic-full-name refs/heads` is ambiguous:
        // it exits 0 and prints nothing. An empty answer is not "absent".
        await AssertRecreationBlockerIsRefusedAsync(
            async repo =>
            {
                await RunGitAsync(repo, "update-ref", "refs/tags/refs/heads", "refs/heads/main");
                await PlantPackedRefAsync(repo, "refs/heads");
            },
            RecreationUnverifiedMessage(WipBranch));
    }

    [DirectoryLinkTheory]
    [InlineData("refs/heads/feature/wip.lock")]
    [InlineData("packed-refs.lock")]
    public async Task Stale_lock_file_makes_git_branch_d_itself_fail_so_nothing_is_deleted(string lockFile)
    {
        // A lock left behind blocks the delete itself (`-d` needs both
        // locks), so no recreation guard is needed: git refuses before any
        // ref is touched, even with the alias letting the pre-check pass.
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);
        CreateBranchDirectoryAlias(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, ".git", lockFile), "stale");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);

        Assert.False(success);
        Assert.Contains(".lock", message);
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, WipBranch));
        Assert.Equal(".", (await RunGitAsync(_repoPath, "config", "--get", $"branch.{WipBranch}.remote")).Trim());
    }

    [Fact]
    public async Task Refs_that_only_resemble_a_prefix_do_not_block_deletion()
    {
        // rev-parse also resolves "refs" to a branch named refs and
        // "refs/heads" to a tag refs/tags/refs/heads. Neither is the exact
        // name, so neither blocks recreation, and neither may block deleting.
        await RunGitAsync(_repoPath, "branch", "refs");
        await RunGitAsync(_repoPath, "update-ref", "refs/tags/refs/heads", "refs/heads/main");
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/merged");
        var tip = await CommitFileAsync(_repoPath, "merged.txt", "merged work");
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "merge", "--no-ff", "--no-gpg-sign", "-m", "merge feature/merged", "feature/merged");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, "feature/merged");

        Assert.True(success, message);
        Assert.False(await _sut.BranchExistsAsync(_repoPath, "feature/merged"));
        Assert.Equal(0, (await RunRawGitAsync(_repoPath, null, "branch", "--", "feature/merged", tip)).ExitCode);
    }

    [Fact]
    public async Task Branch_named_HEAD_is_not_deleted_because_git_cannot_recreate_it()
    {
        // A hostile repository can ship refs/heads/HEAD (and point .git/HEAD
        // at it, so the user's commits land there). Positive control in a
        // throwaway repository: `git branch -d HEAD` deletes such a branch
        // (here merged into main), and git then refuses to create a branch
        // with that name, so a deletion could never be undone.
        var control = await CreateRepositoryAsync("control");
        var controlTip = (await RunGitAsync(control, "rev-parse", "refs/heads/main")).Trim();
        await RunGitAsync(control, "update-ref", "refs/heads/HEAD", controlTip);
        var plainDelete = await RunRawGitAsync(control, null, "branch", "-d", "--", "HEAD");
        Assert.True(plainDelete.ExitCode == 0, $"plain `git branch -d HEAD` refused: {plainDelete.Error}");
        Assert.False(await _sut.BranchExistsAsync(control, "HEAD"));
        Assert.NotEqual(0, (await RunRawGitAsync(control, null, "branch", "--", "HEAD", controlTip)).ExitCode);
        Assert.False(await _sut.BranchExistsAsync(control, "HEAD"));

        var tip = await CreateBranchNamedHeadAsync(_repoPath);

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, "HEAD");

        Assert.False(success);
        Assert.Equal(NotRecreatableMessage("HEAD"), message);
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, "HEAD"));
    }

    [DirectoryLinkFact]
    public async Task Failed_recreation_after_the_delete_throws_instead_of_reporting_not_deleted()
    {
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);
        CreateBranchDirectoryAlias(_repoPath);

        // Test-only trigger (not an attack: in this state, with default
        // config, the user could not commit on the branch either): a
        // non-empty directory where the branch's reflog file belongs. `-d`
        // still deletes the ref (it only warns about the reflog), but git
        // refuses to create the branch again while logs remain at that path.
        var reflog = Path.Combine(_repoPath, ".git", "logs", "refs", "heads", "feature", "wip");
        File.Delete(reflog);
        Directory.CreateDirectory(reflog);
        await File.WriteAllTextAsync(Path.Combine(reflog, "blocker"), "blocks recreation");

        var ex = await Assert.ThrowsAsync<BranchDeletionVerificationException>(
            () => _sut.DeleteBranchAsync(_repoPath, WipBranch));

        // Never (false, ...): clean would roll back the trash entry, the only
        // record of the SHA now. The exception type derives straight from
        // Exception, so no filtered catch in clean can swallow it.
        Assert.Equal(typeof(Exception), typeof(BranchDeletionVerificationException).BaseType);
        Assert.Contains($"'{WipBranch}'", ex.Message);
        Assert.Contains(tip, ex.Message);
        Assert.False(await _sut.BranchExistsAsync(_repoPath, WipBranch));
        Assert.Contains(
            "still logs",
            (await RunRawGitAsync(_repoPath, null, "branch", "--", WipBranch, tip)).Error);
        Assert.Equal(0, (await RunRawGitAsync(_repoPath, null, "cat-file", "-e", $"{tip}^{{commit}}")).ExitCode);
    }

    private static string NotRecreatableMessage(string branchName) =>
        $"Branch '{branchName}' cannot be recreated by git under this name, so a deletion could not be undone if it left commits unreachable; it was not deleted.";

    private static string RecreationUnverifiedMessage(string branchName) =>
        $"Could not verify that branch '{branchName}' could be recreated by git if its deletion had to be undone; it was not deleted.";

    private const string DanglingSha = "0123456789abcdef0123456789abcdef01234567";

    private async Task AssertRecreationBlockerIsRefusedAsync(Func<string, Task> plantBlocker, string expectedMessage)
    {
        // Positive control in a throwaway repository with the same state:
        // the user can still commit on the branch, plain `git branch -d`
        // deletes it, and then recreating it fails.
        var control = await CreateRepositoryAsync("control");
        await CreateSelfUpstreamBranchAsync(control, WipBranch);
        await plantBlocker(control);
        await RunGitAsync(control, "checkout", WipBranch);
        var controlTip = await CommitFileAsync(control, "more.txt", "more unpushed work");
        await RunGitAsync(control, "checkout", "main");
        var plainDelete = await RunRawGitAsync(control, null, "branch", "-d", "--", WipBranch);
        Assert.True(plainDelete.ExitCode == 0, $"plain `git branch -d` refused: {plainDelete.Error}");
        var recreate = await RunRawGitAsync(control, null, "branch", "--", WipBranch, controlTip);
        Assert.True(recreate.ExitCode != 0, "recreating the branch unexpectedly succeeded");

        // The alias makes the reachability pre-check pass, so only the
        // recreation guard stands between the branch and `git branch -d`.
        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, WipBranch);
        await plantBlocker(_repoPath);
        CreateBranchDirectoryAlias(_repoPath);
        await AssertListedAsOrdinaryRefAsync(_repoPath, tip, "refs/remotes/alias/wip");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, WipBranch);

        Assert.False(success);
        Assert.Equal(expectedMessage, message);
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, WipBranch));
        // `git branch -d` never ran: it would have removed the config section.
        Assert.Equal(".", (await RunGitAsync(_repoPath, "config", "--get", $"branch.{WipBranch}.remote")).Trim());
    }

    // refs/remotes/alias -> .git/refs/heads/feature, so refs/remotes/alias/wip
    // is the very file refs/heads/feature/wip. A junction on Windows (no
    // privilege needed, what a hostile archive would ship), a directory
    // symlink elsewhere.
    private static void CreateBranchDirectoryAlias(string repo)
    {
        var refs = Path.Combine(repo, ".git", "refs");
        Directory.CreateDirectory(Path.Combine(refs, "remotes"));
        var alias = Path.Combine(refs, "remotes", "alias");
        var target = Path.Combine(refs, "heads", "feature");
        if (OperatingSystem.IsWindows())
        {
            TestLinks.CreateJunction(alias, target);
        }
        else
        {
            TestLinks.CreateDirectoryLink(alias, target);
        }
    }

    // Positive control: git lists the alias as an ordinary ref containing the
    // tip (empty %(symref)), so the pre-delete check alone counts it as
    // "another ref" and lets `git branch -d` run.
    private static async Task AssertListedAsOrdinaryRefAsync(string repo, string tip, string aliasRef)
    {
        var listed = await RunGitAsync(
            repo, "for-each-ref", "--contains", tip, "--format=%(refname)%09%(symref)",
            "refs/heads/", "refs/remotes/", "refs/tags/");
        Assert.Contains($"{aliasRef}\t", listed.Split('\n').Select(line => line.TrimEnd('\r')));
    }

    private async Task AssertDeletionWasUndoneAsync(bool success, string message, string tip)
    {
        Assert.False(success);
        Assert.Equal(UnreachableMessage(WipBranch), message);
        // The branch exists again at its original tip, so the commit is
        // reachable (clean then rolls the trash entry back consistently).
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, WipBranch));
        var containing = await RunGitAsync(_repoPath, "for-each-ref", "--contains", tip, "--format=%(refname)", "refs/heads/");
        Assert.Contains($"refs/heads/{WipBranch}", containing.Split('\n').Select(line => line.TrimEnd('\r')));
        // The refusal came from the post-delete check: `git branch -d` did
        // run (it removed the branch's config section, hostile upstream
        // included) before the branch was recreated.
        Assert.Equal(1, (await RunRawGitAsync(_repoPath, null, "config", "--get", $"branch.{WipBranch}.remote")).ExitCode);
    }

    // Branch named HEAD holding a unique commit (git refuses to create one,
    // so it is written with update-ref, as a hostile archive could ship it).
    private static async Task<string> CreateBranchNamedHeadAsync(string repo)
    {
        await RunGitAsync(repo, "checkout", "-b", "scratch");
        var tip = await CommitFileAsync(repo, "head.txt", "work on a branch named HEAD");
        await RunGitAsync(repo, "checkout", "main");
        await RunGitAsync(repo, "update-ref", "refs/heads/HEAD", tip);
        await RunGitAsync(repo, "update-ref", "-d", "refs/heads/scratch");
        return tip;
    }

    // O-3/RR-6: on a case-sensitive file system where `Feature` and `feature`
    // coexist, recreating `feature` after `-d` would be refused by
    // CreateBranchAsync's case-variant guard, so the delete would throw
    // BranchDeletionVerificationException after `-d` already removed the ref.
    // The recreatability pre-check now refuses up front instead.
    [CaseSensitiveFileSystemFact]
    public async Task Branch_with_a_case_variant_sibling_is_refused_before_the_delete_runs()
    {
        // Positive control: in a repository built the same way, plain
        // `git branch -d feature` deletes the self-upstream branch, so the
        // refusal below is GitSweep's, not git's.
        var control = await CreateRepositoryAsync("case-control");
        await CreateSelfUpstreamBranchAsync(control, "feature");
        await RunGitAsync(control, "branch", "Feature", "main");
        var plainDelete = await RunRawGitAsync(control, null, "branch", "-d", "--", "feature");
        Assert.True(plainDelete.ExitCode == 0, $"plain `git branch -d` refused the self-upstream branch: {plainDelete.Error}");

        var tip = await CreateSelfUpstreamBranchAsync(_repoPath, "feature");
        await RunGitAsync(_repoPath, "branch", "Feature", "main");

        var (success, message) = await _sut.DeleteBranchAsync(_repoPath, "feature");

        Assert.False(success);
        Assert.Contains("only in letter case", message);
        Assert.Contains("it was not deleted", message);
        // `-d` never ran: the branch and its hostile upstream config survive.
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(_repoPath, "feature"));
        Assert.Equal(0, (await RunRawGitAsync(_repoPath, null, "config", "--get", "branch.feature.remote")).ExitCode);
    }

    // Appends a packed ref (at main's tip unless a SHA is given; git sorts an
    // unsorted packed-refs file on load), the way a hostile archive can ship
    // one. git itself never writes most of these names.
    private static async Task PlantPackedRefAsync(string repo, string refName, string sha = null)
    {
        sha ??= (await RunGitAsync(repo, "rev-parse", "refs/heads/main")).Trim();
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "packed-refs"), $"{sha} {refName}\n");
    }

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

    // Branch with one commit that no other ref contains, configured (as a
    // hostile .git/config would) to be its own upstream.
    private static async Task<string> CreateSelfUpstreamBranchAsync(string repo, string branchName)
    {
        await RunGitAsync(repo, "checkout", "-b", branchName);
        var tip = await CommitFileAsync(repo, "wip.txt", "unpushed work");
        await RunGitAsync(repo, "checkout", "main");
        await RunGitAsync(repo, "config", $"branch.{branchName}.remote", ".");
        await RunGitAsync(repo, "config", $"branch.{branchName}.merge", $"refs/heads/{branchName}");
        return tip;
    }

    private static async Task<string> CommitFileAsync(string repo, string fileName, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, fileName), message);
        await RunGitAsync(repo, "add", ".");
        await RunGitAsync(repo, "commit", "-m", message, "--no-gpg-sign");
        return (await RunGitAsync(repo, "rev-parse", "HEAD")).Trim();
    }

    private static Task<string> RunGitAsync(string workingDirectory, params string[] arguments) =>
        RunGitWithInputAsync(workingDirectory, null, arguments);

    private static async Task<string> RunGitWithInputAsync(string workingDirectory, string input, params string[] arguments)
    {
        var result = await RunRawGitAsync(workingDirectory, input, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}{result.Output}");
        return result.Output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunRawGitAsync(
        string workingDirectory, string input, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutableLocator.GetRequiredPath(),
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
