using GitSweep.Cli.Commands;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

/// <summary>
/// restore / undo over the real <see cref="GitRepositoryService"/> and
/// <see cref="TrashService"/>:
/// <list type="bullet">
/// <item>V-01 (final-security-audit.md): the branch is recreated at exactly the
/// saved SHA even when a ref is named like it, and a trash entry and its
/// keep-ref are only dropped once the branch really points at that SHA.</item>
/// <item>I2 (final-review.md): an entry whose branch is already back at the
/// saved commit is finished instead of failing on every run; one whose branch
/// points elsewhere is kept, with what to do next.</item>
/// </list>
/// </summary>
public sealed class TrashRestoreRealGitTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _git = new();
    private string _repo = "";

    public async Task InitializeAsync()
    {
        _repo = await RealGit.InitRepoAsync(Path.Combine(_root, "repo"));
        await RealGit.CommitFileAsync(_repo, "README.md", "initial", "initial commit");
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // ---- V-01 ----

    [Fact]
    public async Task Undo_restores_the_saved_commit_even_after_a_fetch_brought_a_tag_named_like_it()
    {
        // The audit's scenario: feature/pushed is deleted by clean while its
        // remote branch still holds the commit S; the remote then deletes the
        // branch and publishes a tag NAMED S at another commit, which the
        // user's next plain fetch auto-follows.
        var origin = Path.Combine(_root, "origin.git");
        await RealGit.RunAsync(_root, "init", "--bare", "-b", "main", origin);
        await RealGit.RunAsync(_repo, "remote", "add", "origin", origin);
        await RealGit.RunAsync(_repo, "push", "-u", "origin", "main");
        await RealGit.RunAsync(_repo, "checkout", "-b", "feature/pushed");
        var saved = await RealGit.CommitFileAsync(_repo, "pushed.txt", "pushed work", "pushed work");
        await RealGit.RunAsync(_repo, "push", "-u", "origin", "feature/pushed");
        await RealGit.RunAsync(_repo, "checkout", "main");

        var clean = await RunCleanAsync(new CleanCommand.Settings
        {
            Path = _repo, SelectAll = true, ConfirmDeletion = true, StaleOnly = true, StaleAgeInMonths = 0
        });
        Assert.True(clean.ExitCode == 0, clean.Output);
        Assert.Null(await RealGit.TipAsync(_repo, "feature/pushed"));

        var attacker = Path.Combine(_root, "attacker");
        await RealGit.RunAsync(_root, "clone", origin, attacker);
        await RealGit.RunAsync(attacker, "config", "user.email", "attacker@example.com");
        await RealGit.RunAsync(attacker, "config", "user.name", "Attacker");
        var planted = await RealGit.CommitFileAsync(attacker, "attacker.txt", "attacker work", "attacker work");
        await RealGit.RunAsync(attacker, "tag", saved, planted);
        await RealGit.RunAsync(attacker, "push", origin, "main", $"refs/tags/{saved}", ":refs/heads/feature/pushed");
        await RealGit.RunAsync(_repo, "fetch", "--prune", "origin");
        Assert.Equal(planted, (await RealGit.RunAsync(_repo, "rev-parse", $"refs/tags/{saved}")).Trim());

        var (exitCode, output) = await RunUndoAsync();

        Assert.True(exitCode == 0, output);
        Assert.Equal(saved, await RealGit.TipAsync(_repo, "feature/pushed"));
        Assert.Empty(RealGit.TrashFiles(_repo));
        await RealGit.ExpireReflogsAndPruneAsync(_repo);
        Assert.True(await RealGit.ObjectExistsAsync(_repo, saved), "the saved commit was lost");
    }

    [Fact]
    public async Task Restore_keeps_the_entry_and_its_keep_ref_when_the_new_branch_is_not_at_the_saved_object()
    {
        // A trash file is repository content and can name an annotated tag
        // object: git peels it and creates the branch at the tagged commit,
        // which is not what the entry saved. The entry and its keep-ref stay.
        await RealGit.RunAsync(_repo, "tag", "-a", "v1", "-m", "release", "HEAD");
        var tagObject = (await RealGit.RunAsync(_repo, "rev-parse", "refs/tags/v1")).Trim();
        var entry = await new TrashService(_git).SaveAsync(_repo, "feature/tagged", tagObject, Guid.NewGuid());

        var console = new TestConsole().Width(400);
        var result = await new RestoreCommand(_git, new TrashService(_git), console).ExecuteForTestAsync(
            Context("restore"), new RestoreCommand.Settings { Path = _repo, RestoreAll = true, ConfirmRestore = true }, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("Failed to restore 'feature/tagged'", console.Output);
        Assert.Contains("does not point at commit " + tagObject, console.Output);
        Assert.Single(RealGit.TrashFiles(_repo));
        Assert.Equal(new[] { $"refs/gitsweep/trash/{entry.Id}" }, await RealGit.KeepRefsAsync(_repo));
    }

    // ---- I2 ----

    [Fact]
    public async Task Undo_finishes_an_entry_whose_branch_is_already_back_at_the_saved_commit()
    {
        // e.g. `git checkout feature/work` recreated it from its remote branch.
        var tip = await CreateBranchAsync("feature/work");
        await new TrashService(_git).SaveAsync(_repo, "feature/work", tip, Guid.NewGuid());
        Assert.Single(await RealGit.KeepRefsAsync(_repo));

        var (exitCode, output) = await RunUndoAsync();

        Assert.True(exitCode == 0, output);
        Assert.Contains($"Branch 'feature/work' already exists at the saved commit {tip[..8]}; its trash entry was removed.", output);
        Assert.Contains("Undo completed. 1 branch(es) restored, 0 not restored.", output);
        Assert.Equal(tip, await RealGit.TipAsync(_repo, "feature/work"));
        Assert.Empty(RealGit.TrashFiles(_repo));
        Assert.Empty(await RealGit.KeepRefsAsync(_repo));

        // A second run has nothing left to do.
        var (secondExit, secondOutput) = await RunUndoAsync();
        Assert.True(secondExit == 0, secondOutput);
        Assert.Contains("Nothing to undo", secondOutput);
    }

    [Fact]
    public async Task Undo_after_clean_stopped_on_a_limit_failure_finishes_the_entry()
    {
        // clean saved the entry and keep-ref, then `git branch -d` hit a
        // deadline: the entry is kept on purpose (git's answer is unknown),
        // but the branch was never deleted. undo used to report "already
        // exists" on every run, keeping the keep-ref forever.
        var tip = await CreateBranchAsync("feature/merged", mergeIntoMain: true);
        var git = new DeleteHitsALimit(_git);
        var console = new TestConsole().Width(400);
        var command = new CleanCommand(git, new BranchAnalyzer(), new TrashService(git), new BranchRiskClassifier(),
            new GitSweepConfigService(git), new BranchProtectionService(), console);

        await Assert.ThrowsAsync<GitCommandLimitException>(() => command.ExecuteForTestAsync(
            Context("clean"), new CleanCommand.Settings { Path = _repo, SelectAll = true, ConfirmDeletion = true, MergedOnly = true }, CancellationToken.None));
        Assert.Single(RealGit.TrashFiles(_repo));
        Assert.Single(await RealGit.KeepRefsAsync(_repo));
        Assert.Equal(tip, await RealGit.TipAsync(_repo, "feature/merged"));

        var (exitCode, output) = await RunUndoAsync();

        Assert.True(exitCode == 0, output);
        Assert.Contains("already exists at the saved commit", output);
        Assert.Empty(RealGit.TrashFiles(_repo));
        Assert.Empty(await RealGit.KeepRefsAsync(_repo));
        Assert.Equal(tip, await RealGit.TipAsync(_repo, "feature/merged"));
    }

    [Fact]
    public async Task Undo_keeps_an_entry_whose_branch_now_points_elsewhere_and_says_how_to_get_the_commit_back()
    {
        var tip = await CreateBranchAsync("feature/work");
        var entry = await new TrashService(_git).SaveAsync(_repo, "feature/work", tip, Guid.NewGuid());
        await RealGit.RunAsync(_repo, "branch", "-f", "feature/work", "main");

        var (exitCode, output) = await RunUndoAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("Branch 'feature/work' already exists.", output);
        Assert.Contains($"It does not point at the saved commit {tip[..8]}", output);
        Assert.Contains($"git branch <new-name> \"{tip}^{{commit}}\"", output);
        Assert.Single(RealGit.TrashFiles(_repo));
        Assert.Equal(new[] { $"refs/gitsweep/trash/{entry.Id}" }, await RealGit.KeepRefsAsync(_repo));
        Assert.NotEqual(tip, await RealGit.TipAsync(_repo, "feature/work"));
    }

    // ---- helpers ----

    private sealed class DeleteHitsALimit(IGitRepositoryService inner) : DelegatingGitRepositoryService(inner)
    {
        public override Task<(bool Success, string ErrorMessage)> DeleteBranchAsync(string path, string branchName, CancellationToken ct = default) =>
            throw new GitCommandLimitException("git branch timed out after 120 seconds.");
    }

    private async Task<string> CreateBranchAsync(string branch, bool mergeIntoMain = false)
    {
        await RealGit.RunAsync(_repo, "checkout", "-b", branch);
        var tip = await RealGit.CommitFileAsync(_repo, branch.Replace('/', '-') + ".txt", branch, branch + " work");
        await RealGit.RunAsync(_repo, "checkout", "main");
        if (mergeIntoMain)
        {
            await RealGit.RunAsync(_repo, "merge", "--ff-only", branch);
        }

        return tip;
    }

    private async Task<(int ExitCode, string Output)> RunCleanAsync(CleanCommand.Settings settings)
    {
        var console = new TestConsole().Width(400);
        var trash = new TrashService(_git);
        var command = new CleanCommand(_git, new BranchAnalyzer(), trash, new BranchRiskClassifier(),
            new GitSweepConfigService(_git), new BranchProtectionService(), console);
        var result = await command.ExecuteForTestAsync(Context("clean"), settings, CancellationToken.None);
        return (result, console.Output);
    }

    private async Task<(int ExitCode, string Output)> RunUndoAsync()
    {
        var console = new TestConsole().Width(400);
        var result = await new UndoCommand(_git, new TrashService(_git), console).ExecuteForTestAsync(
            Context("undo"), new UndoCommand.Settings { Path = _repo, ConfirmRestore = true }, CancellationToken.None);
        return (result, console.Output);
    }

    private static CommandContext Context(string name) =>
        new([], Substitute.For<IRemainingArguments>(), name, null);
}
