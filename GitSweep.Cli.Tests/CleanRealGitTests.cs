using System.Text.RegularExpressions;
using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

/// <summary>
/// clean over the real <see cref="GitRepositoryService"/>, <see cref="TrashService"/>,
/// <see cref="GitSweepConfigService"/> and <see cref="BranchProtectionService"/>
/// (final-coverage-review.md):
/// <list type="bullet">
/// <item>P2-T1: whatever the mode, with or without <c>--fetch</c>, no tip a
/// clean run touched is left unreferenced after the remote branch disappears,
/// a later <c>fetch --prune</c> and an aggressive <c>gc</c>.</item>
/// <item>P5-T1: a <c>.gitsweep.json</c> that turns the current-branch and
/// worktree protection flags off still cannot get those branches deleted.</item>
/// <item>P5-T2: a glob that makes the regex engine time out stops clean before
/// it deletes anything.</item>
/// </list>
/// </summary>
public sealed class CleanRealGitTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _git = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // ---- P2-T1 ----

    [Fact]
    public async Task Positive_control_plain_git_loses_the_pushed_only_commit_in_the_same_sequence()
    {
        var (origin, repo, _, pushed) = await BuildRepositoryWithUniqueWorkAsync("control");

        // `git branch -d` accepts the branch: it is merged into its upstream.
        await RealGit.RunAsync(repo, "branch", "-d", "feature/pushed");
        await DeleteRemoteBranchFetchAndCollectGarbageAsync(origin, repo);

        Assert.False(await RealGit.ObjectExistsAsync(repo, pushed), "positive control: the pushed-only commit survived without GitSweep");
    }

    [Theory]
    [InlineData("default", false)]
    [InlineData("default", true)]
    [InlineData("merged-only", false)]
    [InlineData("merged-only", true)]
    [InlineData("stale-only", false)]
    [InlineData("stale-only", true)]
    [InlineData("gone-only", false)]
    [InlineData("gone-only", true)]
    public async Task No_mode_leaves_a_tip_unreferenced_after_a_later_prune_and_gc(string mode, bool fetch)
    {
        var (origin, repo, unpushed, pushed) = await BuildRepositoryWithUniqueWorkAsync(mode + (fetch ? "-fetch" : ""));
        var settings = new CleanCommand.Settings
        {
            Path = repo,
            SelectAll = true,
            ConfirmDeletion = true,
            StaleAgeInMonths = 0,
            Fetch = fetch,
            MergedOnly = mode == "merged-only",
            StaleOnly = mode == "stale-only",
            GoneOnly = mode == "gone-only"
        };

        var (exitCode, output) = await RunCleanAsync(settings);

        Assert.True(exitCode is 0 or 1, output);
        // The unpushed branch is offered in the default and stale modes, but
        // no other ref contains its tip, so it is never deleted.
        Assert.Equal(unpushed, await RealGit.TipAsync(repo, "feature/unpushed"));
        var deletesStale = mode is "default" or "stale-only";
        Assert.Equal(deletesStale, await RealGit.TipAsync(repo, "feature/pushed") is null);

        await DeleteRemoteBranchFetchAndCollectGarbageAsync(origin, repo);

        foreach (var tip in new[] { unpushed, pushed })
        {
            Assert.True(await RealGit.ObjectExistsAsync(repo, tip), $"{mode}{(fetch ? " --fetch" : "")}: commit {tip} was lost.\n{output}");
            var containing = await RealGit.RunAsync(repo, "for-each-ref", "--contains", tip, "--format=%(refname)");
            Assert.False(string.IsNullOrWhiteSpace(containing), $"{mode}: commit {tip} is no longer referenced");
        }
    }

    // ---- P5-T1 ----

    [Fact]
    public async Task Config_that_turns_the_current_and_worktree_flags_off_still_cannot_get_those_branches_deleted()
    {
        var origin = Path.Combine(_root, "protect-origin.git");
        await RealGit.RunAsync(_root, "init", "--bare", "-b", "main", origin);
        var seed = await RealGit.InitRepoAsync(Path.Combine(_root, "protect-seed"));
        await RealGit.CommitFileAsync(seed, "README.md", "initial", "initial commit");
        await RealGit.RunAsync(seed, "push", origin, "main");
        var repo = Path.Combine(_root, "protect-repo");
        await RealGit.RunAsync(_root, "clone", origin, repo);
        await RealGit.RunAsync(repo, "config", "user.email", "tests@example.com");
        await RealGit.RunAsync(repo, "config", "user.name", "GitSweep Tests");
        // All three are merged into main (they point at it).
        await RealGit.RunAsync(repo, "branch", "feature/unprotected");
        await RealGit.RunAsync(repo, "branch", "feature/worktree");
        await RealGit.RunAsync(repo, "worktree", "add", Path.Combine(_root, "protect-worktree"), "feature/worktree");
        await RealGit.RunAsync(repo, "checkout", "-b", "feature/current");
        await File.WriteAllTextAsync(Path.Combine(repo, GitSweepConfig.FileName),
            """{"excludeCurrentBranch":false,"excludeWorktreeBranches":false}""");
        Assert.Equal("main", await _git.GetDefaultBranchNameAsync(repo));

        var (exitCode, output) = await RunCleanAsync(new CleanCommand.Settings
        {
            Path = repo, SelectAll = true, ConfirmDeletion = true, MergedOnly = true
        });

        Assert.True(exitCode == 0, output);
        // Positive control: an unprotected merged branch in the same run is deleted.
        Assert.Null(await RealGit.TipAsync(repo, "feature/unprotected"));
        Assert.NotNull(await RealGit.TipAsync(repo, "feature/current"));
        Assert.NotNull(await RealGit.TipAsync(repo, "feature/worktree"));
        Assert.NotNull(await RealGit.TipAsync(repo, "main"));
    }

    // ---- P5-T2 ----

    [Fact]
    public async Task Glob_that_times_out_stops_clean_with_exit_1_before_any_delete()
    {
        // A valid pattern whose regex backtracks for far longer than the
        // matcher's 250 ms timeout against a long name of 'a's.
        var pattern = string.Concat(Enumerable.Repeat("*a", 14)) + "*b";
        var branch = new string('a', 100);
        var repo = await RealGit.InitRepoAsync(Path.Combine(_root, "redos"));
        await RealGit.CommitFileAsync(repo, "README.md", "initial", "initial commit");
        await RealGit.RunAsync(repo, "branch", branch);
        await File.WriteAllTextAsync(Path.Combine(repo, GitSweepConfig.FileName), $$"""{"protectedBranches":["{{pattern}}"]}""");

        // Through the real CLI process: the timeout reaches the global
        // handler, which prints it and exits 1.
        var (exitCode, output) = await CliProcess.RunAsync(["clean", "-p", repo, "--merged-only", "--all", "--yes"]);

        Assert.True(exitCode == 1, output);
        Assert.Contains("Error:", output);
        Assert.Contains("timed out", Regex.Replace(output, "\u001b\\[[0-9;]*m|\r|\n", string.Empty));
        Assert.NotNull(await RealGit.TipAsync(repo, branch));
        Assert.Empty(RealGit.TrashFiles(repo));

        // Positive control: without the pattern the same run deletes the
        // (merged) branch, so it was the protection check that stopped it.
        File.Delete(Path.Combine(repo, GitSweepConfig.FileName));
        var (controlExit, controlOutput) = await CliProcess.RunAsync(["clean", "-p", repo, "--merged-only", "--all", "--yes"]);
        Assert.True(controlExit == 0, controlOutput);
        Assert.Null(await RealGit.TipAsync(repo, branch));
    }

    // ---- helpers ----

    // origin (bare, HEAD -> main) and a clone with:
    //  - feature/unpushed: a unique commit that exists only locally;
    //  - feature/pushed: a unique commit that only its remote branch also holds.
    private async Task<(string Origin, string Repo, string Unpushed, string Pushed)> BuildRepositoryWithUniqueWorkAsync(string name)
    {
        var origin = Path.Combine(_root, name + "-origin.git");
        await RealGit.RunAsync(_root, "init", "--bare", "-b", "main", origin);
        var seed = await RealGit.InitRepoAsync(Path.Combine(_root, name + "-seed"));
        await RealGit.CommitFileAsync(seed, "README.md", "initial", "initial commit");
        await RealGit.RunAsync(seed, "push", origin, "main");

        var repo = Path.Combine(_root, name + "-repo");
        await RealGit.RunAsync(_root, "clone", origin, repo);
        await RealGit.RunAsync(repo, "config", "user.email", "tests@example.com");
        await RealGit.RunAsync(repo, "config", "user.name", "GitSweep Tests");

        await RealGit.RunAsync(repo, "checkout", "-b", "feature/unpushed");
        var unpushed = await RealGit.CommitFileAsync(repo, "unpushed.txt", "unpushed", "unpushed work");
        await RealGit.RunAsync(repo, "checkout", "main");
        await RealGit.RunAsync(repo, "checkout", "-b", "feature/pushed");
        var pushed = await RealGit.CommitFileAsync(repo, "pushed.txt", "pushed", "pushed work");
        await RealGit.RunAsync(repo, "push", "-u", "origin", "feature/pushed");
        await RealGit.RunAsync(repo, "checkout", "main");
        return (origin, repo, unpushed, pushed);
    }

    // The ordinary aftermath: the remote branch is deleted (merged or
    // abandoned upstream), the user's next `fetch --prune` drops the
    // remote-tracking ref, and gc prunes whatever nothing references.
    private static async Task DeleteRemoteBranchFetchAndCollectGarbageAsync(string origin, string repo)
    {
        await RealGit.RunAsync(origin, "branch", "-D", "feature/pushed");
        await RealGit.RunAsync(repo, "fetch", "--prune", "origin");
        await RealGit.ExpireReflogsAndPruneAsync(repo);
    }

    private async Task<(int ExitCode, string Output)> RunCleanAsync(CleanCommand.Settings settings)
    {
        var console = new TestConsole().Width(400);
        var command = new CleanCommand(_git, new BranchAnalyzer(), new TrashService(_git), new BranchRiskClassifier(),
            new GitSweepConfigService(_git), new BranchProtectionService(), console);
        var result = await command.ExecuteForTestAsync(
            new CommandContext([], Substitute.For<IRemainingArguments>(), "clean", null), settings, CancellationToken.None);
        return (result, console.Output);
    }
}
