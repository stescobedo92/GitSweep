using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

public sealed class CleanCommandGoneTests
{
    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    private void SetupRepo(List<BranchInfo> branches)
    {
        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>()).Returns(branches);
        _gitService.FetchPruneAsync("/some/repo", Arg.Any<CancellationToken>()).Returns((true, string.Empty));
    }

    [Fact]
    public async Task Fetch_gone_only_dry_run_lists_safe_gone_candidate()
    {
        var console = new TestConsole().Width(200);
        // Real analyzer: only gone+merged is a safe gone candidate.
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), new BranchRiskClassifier(), console);
        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true, Fetch = true, GoneOnly = true };

        var safeGone = new BranchInfo("feature/gone", DateTime.UtcNow, true, BranchMergeKind.Ancestor, "origin/feature/gone", true);
        var goneUnmerged = new BranchInfo("feature/gone-unmerged", DateTime.UtcNow, false, BranchMergeKind.NotMerged, "origin/feature/gone-unmerged", true);
        var stale = new BranchInfo("feature/stale", DateTime.UtcNow.AddMonths(-9), false);
        SetupRepo([safeGone, goneUnmerged, stale]);
        _gitService.GetBranchRiskSignalsAsync("/some/repo", "main", "feature/gone", Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, false, true, true));

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        await _gitService.Received(1).FetchPruneAsync("/some/repo", Arg.Any<CancellationToken>());
        Assert.Contains("feature/gone", console.Output);
        Assert.DoesNotContain("feature/gone-unmerged", console.Output);
        Assert.DoesNotContain("feature/stale", console.Output);
        Assert.Contains("gone", console.Output);
        Assert.Contains("segura", console.Output);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fetch_failure_returns_1_and_never_scans_or_deletes()
    {
        var console = new TestConsole().Width(200);
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), new BranchRiskClassifier(), console);
        var settings = new CleanCommand.Settings { Path = "/some/repo", Fetch = true, GoneOnly = true, DryRun = true };
        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.FetchPruneAsync("/some/repo", Arg.Any<CancellationToken>()).Returns((false, "network unreachable"));

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("Fetch with prune failed", console.Output);
        await _gitService.DidNotReceive().GetLocalBranchesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_fetch_the_prune_is_not_run()
    {
        var console = new TestConsole().Width(200);
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), new BranchRiskClassifier(), console);
        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true, GoneOnly = true };
        var safeGone = new BranchInfo("feature/gone", DateTime.UtcNow, true, BranchMergeKind.Ancestor, "origin/feature/gone", true);
        SetupRepo([safeGone]);
        _gitService.GetBranchRiskSignalsAsync("/some/repo", "main", "feature/gone", Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, false, true, true));

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        await _gitService.DidNotReceive().FetchPruneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Settings_gone_only_conflicts_with_merged_or_stale_only()
    {
        Assert.NotNull(CleanCommand.Settings.ValidateValues(
            new CleanCommand.Settings { GoneOnly = true, MergedOnly = true }));
        Assert.NotNull(CleanCommand.Settings.ValidateValues(
            new CleanCommand.Settings { GoneOnly = true, StaleOnly = true }));
        Assert.Null(CleanCommand.Settings.ValidateValues(
            new CleanCommand.Settings { GoneOnly = true, Fetch = true }));
    }

    [Fact]
    public async Task ExecuteAsync_returns_1_when_gone_only_combined_with_merged_only()
    {
        var console = new TestConsole();
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), console);
        var settings = new CleanCommand.Settings { GoneOnly = true, MergedOnly = true };

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("--gone-only", console.Output);
    }
}
