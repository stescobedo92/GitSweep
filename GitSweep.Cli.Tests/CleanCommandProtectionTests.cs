using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

public sealed class CleanCommandProtectionTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly IBranchAnalyzer _analyzer = Substitute.For<IBranchAnalyzer>();
    private readonly ITrashService _trashService = Substitute.For<ITrashService>();
    private readonly IGitSweepConfigService _configService = Substitute.For<IGitSweepConfigService>();

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    private CleanCommand CreateCommand(TestConsole console) =>
        new(_gitService, _analyzer, _trashService, new BranchRiskClassifier(),
            _configService, new BranchProtectionService(), console);

    private void SetupBranches(List<BranchInfo> branches, GitSweepConfig config, string? current, IReadOnlyList<string> worktrees)
    {
        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>()).Returns(branches);
        _gitService.GetCurrentBranchAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(current);
        _gitService.GetWorktreeBranchesAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(worktrees);
        _configService.LoadAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(config);
        // Analyzer sees the *unprotected* subset; match any list and filter like the real analyzer would for merged.
        _analyzer.IdentifyStaleBranches(Arg.Any<IReadOnlyList<BranchInfo>>(), Arg.Any<int>())
            .Returns(ci => ((IReadOnlyList<BranchInfo>)ci[0]).Where(b => !b.IsEffectivelyMerged && b.LastCommitDate < DateTime.UtcNow.AddMonths(-6)).ToList());
        _analyzer.IdentifyMergedBranches(Arg.Any<IReadOnlyList<BranchInfo>>())
            .Returns(ci => ((IReadOnlyList<BranchInfo>)ci[0]).Where(b => b.IsEffectivelyMerged).ToList());
        _analyzer.IdentifySafeGoneBranches(Arg.Any<IReadOnlyList<BranchInfo>>()).Returns([]);
        _gitService.GetBranchTipShaAsync("/some/repo", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Sha);
        _gitService.GetBranchRiskSignalsAsync("/some/repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, true, true));
    }

    [Fact]
    public async Task Glob_protected_branch_is_never_candidate_nor_deleted()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var branches = new List<BranchInfo>
        {
            new("release/1.0", DateTime.UtcNow, true),
            new("feature/merged", DateTime.UtcNow, true),
        };
        SetupBranches(branches, new GitSweepConfig(["release/*"]), "main", ["main"]);
        _trashService.SaveAsync("/some/repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TrashEntry(Guid.NewGuid().ToString("N"), (string)ci[1], Sha, DateTimeOffset.UtcNow, Guid.NewGuid()));
        _gitService.DeleteBranchAsync("/some/repo", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((true, string.Empty));

        var settings = new CleanCommand.Settings { Path = "/some/repo", SelectAll = true, ConfirmDeletion = true };
        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        await _gitService.DidNotReceive().DeleteBranchAsync("/some/repo", "release/1.0", Arg.Any<CancellationToken>());
        await _gitService.Received(1).DeleteBranchAsync("/some/repo", "feature/merged", Arg.Any<CancellationToken>());
        Assert.Contains("release/1.0", console.Output);
        Assert.Contains("Protected", console.Output);
    }

    [Fact]
    public async Task Current_branch_is_excluded_even_if_merged()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var branches = new List<BranchInfo> { new("feature/current", DateTime.UtcNow, true) };
        SetupBranches(branches, GitSweepConfig.Empty, "feature/current", ["feature/current"]);

        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true };
        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("No stale or merged branches found", console.Output);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worktree_branch_is_excluded_even_if_merged()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var branches = new List<BranchInfo> { new("feature/wt", DateTime.UtcNow, true) };
        SetupBranches(branches, GitSweepConfig.Empty, "main", ["main", "feature/wt"]);

        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true };
        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("No stale or merged branches found", console.Output);
    }

    [Fact]
    public async Task Default_config_protects_main_when_it_is_not_target()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var branches = new List<BranchInfo> { new("main", DateTime.UtcNow, true) };
        // Target is develop in this scenario; mock still returns branches as given.
        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("develop");
        _gitService.GetLocalBranchesAsync("/some/repo", "develop", Arg.Any<CancellationToken>()).Returns(branches);
        _gitService.GetCurrentBranchAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("develop");
        _gitService.GetWorktreeBranchesAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(["develop"]);
        _configService.LoadAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(GitSweepConfig.Default);
        _analyzer.IdentifyStaleBranches(Arg.Any<IReadOnlyList<BranchInfo>>(), Arg.Any<int>()).Returns([]);
        _analyzer.IdentifyMergedBranches(Arg.Any<IReadOnlyList<BranchInfo>>()).Returns(ci => ((IReadOnlyList<BranchInfo>)ci[0]).ToList());
        _analyzer.IdentifySafeGoneBranches(Arg.Any<IReadOnlyList<BranchInfo>>()).Returns([]);

        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true };
        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("No stale or merged branches found", console.Output);
    }

    [Fact]
    public async Task Clean_uses_exactly_the_unprotected_list_even_when_nothing_is_reported_protected()
    {
        // O12: clean used to replace the unprotected list with every branch
        // whenever no branch was reported protected. FilterProtected already
        // returns every unprotected branch, so that fallback could only ever
        // widen the candidates past what the protection service allowed
        // (fail-open shape). Here the service excludes the only branch
        // without reporting it as protected: nothing may be analyzed or
        // deleted.
        var console = new TestConsole();
        var branches = new List<BranchInfo> { new("feature/merged", DateTime.UtcNow, true) };
        SetupBranches(branches, GitSweepConfig.Default, "main", ["main"]);
        _trashService.SaveAsync("/some/repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TrashEntry(Guid.NewGuid().ToString("N"), (string)ci[1], Sha, DateTimeOffset.UtcNow, Guid.NewGuid()));
        _gitService.DeleteBranchAsync("/some/repo", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((true, string.Empty));
        var protection = Substitute.For<IBranchProtectionService>();
        protection.FilterProtected(default!, default!, default, default, out _)
            .ReturnsForAnyArgs(ci =>
            {
                ci[4] = new List<BranchInfo>();
                return new List<BranchProtectionResult>();
            });
        protection.Evaluate(default!, default!, default, default)
            .ReturnsForAnyArgs(ci => BranchProtectionResult.NotProtected((string)ci[0]));
        var command = new CleanCommand(_gitService, _analyzer, _trashService, new BranchRiskClassifier(),
            _configService, protection, console);

        var settings = new CleanCommand.Settings { Path = "/some/repo", SelectAll = true, ConfirmDeletion = true };
        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("No stale or merged branches found", console.Output);
        _analyzer.Received().IdentifyMergedBranches(Arg.Is<IReadOnlyList<BranchInfo>>(list => list.Count == 0));
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_config_fails_closed_and_deletes_nothing()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _configService.LoadAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns<GitSweepConfig>(_ => throw new InvalidOperationException(".gitsweep.json is not valid JSON"));

        var settings = new CleanCommand.Settings { Path = "/some/repo", SelectAll = true, ConfirmDeletion = true };
        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("Invalid .gitsweep.json", console.Output);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().GetLocalBranchesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dry_run_shows_protected_table_and_deletes_nothing()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var branches = new List<BranchInfo>
        {
            new("release/9", DateTime.UtcNow, true),
            new("feature/merged", DateTime.UtcNow, true),
        };
        SetupBranches(branches, new GitSweepConfig(["release/*"]), "main", ["main"]);

        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true };
        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("Dry run enabled", console.Output);
        Assert.Contains("release/9", console.Output);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
