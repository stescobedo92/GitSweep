using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

public sealed class CleanCommandTrashTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly IBranchAnalyzer _analyzer = Substitute.For<IBranchAnalyzer>();
    private readonly ITrashService _trashService = Substitute.For<ITrashService>();

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    private CleanCommand CreateCommand(TestConsole console) =>
        new(_gitService, _analyzer, _trashService, console);

    private void SetupRepoWithMergedCandidate()
    {
        var merged = new BranchInfo("feature/merged", DateTime.UtcNow, true);
        var branches = new List<BranchInfo> { merged };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>()).Returns(branches);
        _analyzer.IdentifyStaleBranches(branches, 6).Returns([]);
        _analyzer.IdentifyMergedBranches(branches).Returns([merged]);
        _gitService.GetBranchTipShaAsync("/some/repo", "feature/merged", Arg.Any<CancellationToken>()).Returns(Sha);
    }

    private static CleanCommand.Settings AllYesSettings() =>
        new() { Path = "/some/repo", SelectAll = true, ConfirmDeletion = true };

    [Fact]
    public async Task ExecuteAsync_saves_sha_to_trash_before_deleting()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        SetupRepoWithMergedCandidate();
        var entry = new TrashEntry(Guid.NewGuid().ToString("N"), "feature/merged", Sha, DateTimeOffset.UtcNow, Guid.NewGuid());
        _trashService.SaveAsync("/some/repo", "feature/merged", Sha, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(entry);
        _gitService.DeleteBranchAsync("/some/repo", "feature/merged", Arg.Any<CancellationToken>())
            .Returns((true, string.Empty));

        var result = await command.ExecuteForTestAsync(CreateContext(), AllYesSettings(), CancellationToken.None);

        Assert.Equal(0, result);
        Received.InOrder(async () =>
        {
            await _trashService.Received(1).SaveAsync("/some/repo", "feature/merged", Sha, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
            await _gitService.Received(1).DeleteBranchAsync("/some/repo", "feature/merged", Arg.Any<CancellationToken>());
        });
        Assert.Contains("gitsweep undo", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_delete_when_trash_save_fails()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        SetupRepoWithMergedCandidate();
        _trashService.SaveAsync("/some/repo", "feature/merged", Sha, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<TrashEntry>(_ => throw new IOException("disk full"));

        var result = await command.ExecuteForTestAsync(CreateContext(), AllYesSettings(), CancellationToken.None);

        Assert.Equal(1, result);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("Could not save branch to trash", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_delete_when_tip_sha_cannot_be_resolved()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        SetupRepoWithMergedCandidate();
        _gitService.GetBranchTipShaAsync("/some/repo", "feature/merged", Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await command.ExecuteForTestAsync(CreateContext(), AllYesSettings(), CancellationToken.None);

        Assert.Equal(1, result);
        await _trashService.DidNotReceive()
            .SaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_removes_trash_entry_when_delete_fails()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        SetupRepoWithMergedCandidate();
        var entry = new TrashEntry(Guid.NewGuid().ToString("N"), "feature/merged", Sha, DateTimeOffset.UtcNow, Guid.NewGuid());
        _trashService.SaveAsync("/some/repo", "feature/merged", Sha, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(entry);
        _gitService.DeleteBranchAsync("/some/repo", "feature/merged", Arg.Any<CancellationToken>())
            .Returns((false, "error: The branch 'feature/merged' is not fully merged."));

        var result = await command.ExecuteForTestAsync(CreateContext(), AllYesSettings(), CancellationToken.None);

        Assert.Equal(1, result);
        await _trashService.Received(1).RemoveAsync("/some/repo", entry.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_does_not_delete_when_branch_tip_changes_after_trash_save()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        SetupRepoWithMergedCandidate();
        const string movedSha = "fedcba9876543210fedcba9876543210fedcba98";
        _gitService.GetBranchTipShaAsync("/some/repo", "feature/merged", Arg.Any<CancellationToken>())
            .Returns(Sha, movedSha);
        var entry = new TrashEntry(Guid.NewGuid().ToString("N"), "feature/merged", Sha, DateTimeOffset.UtcNow, Guid.NewGuid());
        _trashService.SaveAsync("/some/repo", "feature/merged", Sha, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(entry);
        _trashService.RemoveAsync("/some/repo", entry.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await command.ExecuteForTestAsync(CreateContext(), AllYesSettings(), CancellationToken.None);

        // The saved SHA no longer matches the branch tip, so deleting would
        // trash a different commit than the one removed: fail closed.
        Assert.Equal(1, result);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _trashService.Received(1).RemoveAsync("/some/repo", entry.Id, Arg.Any<CancellationToken>());
        Assert.Contains("tip changed", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_dry_run_never_saves_or_deletes()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        SetupRepoWithMergedCandidate();
        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true };

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        await _trashService.DidNotReceive()
            .SaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
