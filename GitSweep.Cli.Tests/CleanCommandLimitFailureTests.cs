using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

/// <summary>
/// SEC-002/SEC-003 fail-closed wiring: when a git invocation hits its
/// deadline or output cap, <see cref="GitRepositoryService"/> throws
/// <see cref="GitCommandLimitException"/> instead of reporting a default.
/// <c>clean</c> must not swallow it into a permissive path: nothing more is
/// deleted, and a delete whose outcome is unknown keeps its trash entry.
/// In production the exception reaches the CLI's global exception handler
/// (Program.cs), which prints it escaped and exits 1.
/// </summary>
public sealed class CleanCommandLimitFailureTests
{
    private const string Repo = "/some/repo";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly IBranchAnalyzer _analyzer = Substitute.For<IBranchAnalyzer>();
    private readonly ITrashService _trashService = Substitute.For<ITrashService>();
    private readonly IGitSweepConfigService _configService = Substitute.For<IGitSweepConfigService>();

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    private static CleanCommand.Settings DeleteAll() =>
        new() { Path = Repo, SelectAll = true, ConfirmDeletion = true, MergedOnly = true };

    private CleanCommand CreateCommand(TestConsole console) =>
        new(_gitService, _analyzer, _trashService, new BranchRiskClassifier(),
            _configService, new BranchProtectionService(), console);

    private void SetupMergedBranch()
    {
        var branches = new List<BranchInfo> { new("feature/merged", DateTime.UtcNow, true) };
        _gitService.IsGitRepositoryAsync(Repo, Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync(Repo, Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync(Repo, "main", Arg.Any<CancellationToken>()).Returns(branches);
        _gitService.GetCurrentBranchAsync(Repo, Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetWorktreeBranchesAsync(Repo, Arg.Any<CancellationToken>()).Returns(["main"]);
        _configService.LoadAsync(Repo, Arg.Any<CancellationToken>()).Returns(GitSweepConfig.Default);
        _analyzer.IdentifyStaleBranches(Arg.Any<IReadOnlyList<BranchInfo>>(), Arg.Any<int>()).Returns([]);
        _analyzer.IdentifyMergedBranches(Arg.Any<IReadOnlyList<BranchInfo>>())
            .Returns(ci => ((IReadOnlyList<BranchInfo>)ci[0]).Where(b => b.IsEffectivelyMerged).ToList());
        _analyzer.IdentifySafeGoneBranches(Arg.Any<IReadOnlyList<BranchInfo>>()).Returns([]);
        _gitService.GetBranchTipShaAsync(Repo, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Sha);
        _gitService.GetBranchRiskSignalsAsync(Repo, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, true, true));
        _trashService.SaveAsync(Repo, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TrashEntry(Guid.NewGuid().ToString("N"), (string)ci[1], Sha, DateTimeOffset.UtcNow, Guid.NewGuid()));
    }

    [Fact]
    public async Task Unknown_current_branch_aborts_clean_before_anything_is_deleted()
    {
        SetupMergedBranch();
        _gitService.GetCurrentBranchAsync(Repo, Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitCommandLimitException("git branch timed out after 120 seconds."));
        var command = CreateCommand(new TestConsole());

        var ex = await Assert.ThrowsAsync<GitCommandLimitException>(
            () => command.ExecuteForTestAsync(CreateContext(), DeleteAll(), CancellationToken.None));

        Assert.Equal("git branch timed out after 120 seconds.", ex.Message);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _trashService.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_protection_config_root_is_not_swallowed_as_a_config_error()
    {
        SetupMergedBranch();
        _configService.LoadAsync(Repo, Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitCommandLimitException("git rev-parse timed out after 120 seconds."));
        var command = CreateCommand(new TestConsole());

        await Assert.ThrowsAsync<GitCommandLimitException>(
            () => command.ExecuteForTestAsync(CreateContext(), DeleteAll(), CancellationToken.None));

        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_with_unknown_outcome_keeps_the_trash_entry_and_stops()
    {
        // git may have deleted the branch before the limit was hit: rolling
        // the trash entry back would lose the only record of its SHA.
        SetupMergedBranch();
        _gitService.DeleteBranchAsync(Repo, "feature/merged", Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitCommandLimitException("git branch output exceeded the 1,048,576-character limit."));
        var command = CreateCommand(new TestConsole());

        await Assert.ThrowsAsync<GitCommandLimitException>(
            () => command.ExecuteForTestAsync(CreateContext(), DeleteAll(), CancellationToken.None));

        await _trashService.Received(1).SaveAsync(
            Repo, "feature/merged", Sha, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _trashService.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
