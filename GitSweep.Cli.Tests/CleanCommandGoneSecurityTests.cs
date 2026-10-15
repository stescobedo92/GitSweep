using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

/// <summary>
/// Security tests for <c>clean --fetch/--gone-only</c>: fetch errors shown
/// to the user must not carry credentials, and a gone-only deletion must
/// re-validate the safe-gone decision on a fresh scan instead of deleting
/// from the stale pre-confirmation scan.
/// </summary>
public sealed class CleanCommandGoneSecurityTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly ITrashService _trashService = Substitute.For<ITrashService>();

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    [Fact]
    public async Task Fetch_failure_output_does_not_leak_credentials()
    {
        var console = new TestConsole().Width(200);
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), new BranchRiskClassifier(), console);
        var settings = new CleanCommand.Settings { Path = "/some/repo", Fetch = true, GoneOnly = true, DryRun = true };
        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.FetchPruneAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns((false, "fatal: unable to access 'https://user:TopSecretPass@host/repo.git/?token=TopSecretToken': failed"));

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.DoesNotContain("TopSecretPass", console.Output);
        Assert.DoesNotContain("TopSecretToken", console.Output);
        Assert.Contains("***", console.Output);
    }

    [Fact]
    public async Task Gone_only_deletion_skips_branch_no_longer_safe_on_fresh_scan()
    {
        var console = new TestConsole().Width(200);
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), _trashService, console);
        var settings = new CleanCommand.Settings
        {
            Path = "/some/repo", GoneOnly = true, SelectAll = true, ConfirmDeletion = true
        };

        var safeGone = new BranchInfo("feature/gone", DateTime.UtcNow, true, BranchMergeKind.Ancestor, "origin/feature/gone", true);
        // Fresh scan right before deletion: upstream restored and the
        // branch is no longer merged, so it is not a safe gone candidate.
        var noLongerSafe = new BranchInfo("feature/gone", DateTime.UtcNow, false, BranchMergeKind.NotMerged, "origin/feature/gone", false);

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns([safeGone], [noLongerSafe]);
        _gitService.GetBranchRiskSignalsAsync("/some/repo", "main", "feature/gone", Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, false, true, true));
        _gitService.GetBranchTipShaAsync("/some/repo", "feature/gone", Arg.Any<CancellationToken>()).Returns(Sha);

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("no longer a safe gone candidate", console.Output);
        await _trashService.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gone_only_deletion_skips_branch_that_turns_unsafe_after_batch_rescan()
    {
        var console = new TestConsole().Width(200);
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), _trashService, console);
        var settings = new CleanCommand.Settings
        {
            Path = "/some/repo", GoneOnly = true, SelectAll = true, ConfirmDeletion = true
        };

        var safeGone = new BranchInfo("feature/gone", DateTime.UtcNow, true, BranchMergeKind.Ancestor, "origin/feature/gone", true);
        // Batch re-scan still says safe, but the per-branch scan taken
        // immediately before deletion no longer does.
        var noLongerSafe = new BranchInfo("feature/gone", DateTime.UtcNow, false, BranchMergeKind.NotMerged, "origin/feature/gone", false);

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns([safeGone], [safeGone], [noLongerSafe]);
        _gitService.GetBranchRiskSignalsAsync("/some/repo", "main", "feature/gone", Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, false, true, true));

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("no longer a safe gone candidate", console.Output);
        await _trashService.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gone_only_deletion_proceeds_when_fresh_scan_still_safe()
    {
        var console = new TestConsole().Width(200);
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), _trashService, console);
        var settings = new CleanCommand.Settings
        {
            Path = "/some/repo", GoneOnly = true, SelectAll = true, ConfirmDeletion = true
        };

        var safeGone = new BranchInfo("feature/gone", DateTime.UtcNow, true, BranchMergeKind.Ancestor, "origin/feature/gone", true);

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns([safeGone], [safeGone]);
        _gitService.GetBranchRiskSignalsAsync("/some/repo", "main", "feature/gone", Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, false, true, true));
        _gitService.GetBranchTipShaAsync("/some/repo", "feature/gone", Arg.Any<CancellationToken>()).Returns(Sha);
        _trashService.SaveAsync("/some/repo", "feature/gone", Sha, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new TrashEntry(Guid.NewGuid().ToString("N"), "feature/gone", Sha, DateTimeOffset.UtcNow, Guid.NewGuid()));
        _gitService.DeleteBranchAsync("/some/repo", "feature/gone", Arg.Any<CancellationToken>())
            .Returns((true, string.Empty));

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        await _gitService.Received(1).DeleteBranchAsync("/some/repo", "feature/gone", Arg.Any<CancellationToken>());
    }
}
