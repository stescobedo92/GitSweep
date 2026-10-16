using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

public sealed class CleanCommandRiskTests
{
    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly IBranchAnalyzer _analyzer = Substitute.For<IBranchAnalyzer>();

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    [Fact]
    public void BuildRiskTable_contains_all_three_levels()
    {
        var assessments = new List<BranchRiskAssessment>
        {
            new(new BranchInfo("feature/safe", DateTime.UtcNow, true, BranchMergeKind.Ancestor),
                BranchRiskLevel.Safe, new BranchRiskSignals(0, 0, true, true), "ok"),
            new(new BranchInfo("feature/review", DateTime.UtcNow, false),
                BranchRiskLevel.Review, new BranchRiskSignals(1, 0, true, false), "review"),
            new(new BranchInfo("feature/danger", DateTime.UtcNow, false),
                BranchRiskLevel.Dangerous, new BranchRiskSignals(2, 2, false, false), "danger"),
        };

        var table = CleanCommand.BuildRiskTable(assessments);

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(6, table.Columns.Count);
    }

    [Fact]
    public async Task ExecuteAsync_dry_run_shows_semaforo_for_candidates()
    {
        var console = new TestConsole().Width(200);
        var command = new CleanCommand(_gitService, _analyzer, new BranchRiskClassifier(), console);
        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true };

        var safe = new BranchInfo("feature/merged", DateTime.UtcNow, true, BranchMergeKind.Ancestor);
        var stale = new BranchInfo("feature/stale", DateTime.UtcNow.AddMonths(-9), false);
        var branches = new List<BranchInfo> { safe, stale };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>()).Returns(branches);
        _analyzer.IdentifyStaleBranches(AnalyzerInput.Matching(branches), 6).Returns([stale]);
        _analyzer.IdentifyMergedBranches(AnalyzerInput.Matching(branches)).Returns([safe]);
        _gitService.GetBranchRiskSignalsAsync("/some/repo", "main", "feature/merged", Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, true, true));
        _gitService.GetBranchRiskSignalsAsync("/some/repo", "main", "feature/stale", Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(2, 2, false, false));

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("Branch risk", console.Output);
        Assert.Contains("segura", console.Output);
        Assert.Contains("peligrosa", console.Output);
        Assert.Contains("Risk summary", console.Output);
        Assert.Contains("feature/merged", console.Output);
        Assert.Contains("Dry run enabled", console.Output);
        await _gitService.DidNotReceive()
            .DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_unknown_signals_show_revisar_never_segura()
    {
        var console = new TestConsole().Width(200);
        var command = new CleanCommand(_gitService, _analyzer, new BranchRiskClassifier(), console);
        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true };

        var merged = new BranchInfo("feature/merged", DateTime.UtcNow, true, BranchMergeKind.Ancestor);
        var branches = new List<BranchInfo> { merged };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>()).Returns(branches);
        _analyzer.IdentifyStaleBranches(AnalyzerInput.Matching(branches), 6).Returns([]);
        _analyzer.IdentifyMergedBranches(AnalyzerInput.Matching(branches)).Returns([merged]);
        // NSubstitute devolvería null; el comando debe degradar a desconocido/revisar.
        _gitService.GetBranchRiskSignalsAsync("/some/repo", "main", "feature/merged", Arg.Any<CancellationToken>())
            .Returns(BranchRiskSignals.Unknown);

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("revisar", console.Output);
        Assert.DoesNotContain("1 segura", console.Output);
    }
}
