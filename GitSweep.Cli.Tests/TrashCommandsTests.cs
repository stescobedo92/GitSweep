using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

public sealed class TrashCommandsTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly ITrashService _trashService = Substitute.For<ITrashService>();

    private static CommandContext CreateContext(string name) =>
        new([], Substitute.For<IRemainingArguments>(), name, null);

    private static TrashEntry Entry(string branch, string sha, Guid batch, int minutesAgo = 0) =>
        new(Guid.NewGuid().ToString("N"), branch, sha, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), batch);

    private void SetupRepo(IReadOnlyList<TrashEntry> entries)
    {
        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(true);
        _trashService.ListAsync("/some/repo", Arg.Any<CancellationToken>()).Returns(entries);
        _gitService.BranchExistsAsync("/some/repo", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _gitService.CreateBranchAsync("/some/repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, string.Empty));
        _trashService.RemoveAsync("/some/repo", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    // ---- restore ----

    [Fact]
    public async Task Restore_by_branch_recreates_at_saved_sha_and_removes_entry()
    {
        var console = new TestConsole();
        var entry = Entry("feature/login", ShaA, Guid.NewGuid());
        SetupRepo([entry]);
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = "/some/repo", Branch = "feature/login", ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(0, result);
        await _gitService.Received(1).CreateBranchAsync("/some/repo", "feature/login", ShaA, Arg.Any<CancellationToken>());
        await _trashService.Received(1).RemoveAsync("/some/repo", entry.Id, Arg.Any<CancellationToken>());
        Assert.Contains("Restored", console.Output);
        Assert.Contains("feature/login", console.Output);
    }

    [Fact]
    public async Task Restore_returns_1_and_keeps_entry_when_branch_already_exists()
    {
        var console = new TestConsole();
        var entry = Entry("feature/login", ShaA, Guid.NewGuid());
        SetupRepo([entry]);
        _gitService.BranchExistsAsync("/some/repo", "feature/login", Arg.Any<CancellationToken>()).Returns(true);
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = "/some/repo", Branch = "feature/login", ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        await _gitService.DidNotReceive().CreateBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _trashService.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("already exists", console.Output);
    }

    [Fact]
    public async Task Restore_returns_1_when_named_branch_is_not_in_trash()
    {
        var console = new TestConsole();
        SetupRepo([Entry("feature/other", ShaA, Guid.NewGuid())]);
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = "/some/repo", Branch = "feature/missing", ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("No deleted branch named", console.Output);
    }

    [Fact]
    public async Task Restore_returns_0_when_trash_is_empty()
    {
        var console = new TestConsole();
        SetupRepo([]);
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = "/some/repo", ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("Trash is empty", console.Output);
    }

    [Fact]
    public async Task Restore_without_selector_fails_closed_in_non_interactive_terminal()
    {
        var console = new TestConsole();
        SetupRepo([Entry("feature/login", ShaA, Guid.NewGuid())]);
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = "/some/repo", ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        await _gitService.DidNotReceive().CreateBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_all_restores_every_entry_and_reports_failures()
    {
        var console = new TestConsole();
        var good = Entry("feature/good", ShaA, Guid.NewGuid());
        var bad = Entry("feature/bad", ShaB, Guid.NewGuid());
        SetupRepo([good, bad]);
        _gitService.CreateBranchAsync("/some/repo", "feature/bad", ShaB, Arg.Any<CancellationToken>())
            .Returns((false, "fatal: not a valid object"));
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = "/some/repo", RestoreAll = true, ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        await _trashService.Received(1).RemoveAsync("/some/repo", good.Id, Arg.Any<CancellationToken>());
        await _trashService.DidNotReceive().RemoveAsync("/some/repo", bad.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_rejects_branch_and_all_together()
    {
        var console = new TestConsole();
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = "/some/repo", Branch = "feature/x", RestoreAll = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("either --branch or --all", console.Output);
    }

    [Fact]
    public async Task Restore_returns_1_for_non_repository()
    {
        var console = new TestConsole();
        _gitService.IsGitRepositoryAsync("/not/a/repo", Arg.Any<CancellationToken>()).Returns(false);
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = "/not/a/repo" };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("is not a Git repository", console.Output);
    }

    // ---- undo ----

    [Fact]
    public async Task Undo_restores_only_the_latest_batch()
    {
        var console = new TestConsole();
        var latestBatch = Guid.NewGuid();
        var olderBatch = Guid.NewGuid();
        var first = Entry("feature/one", ShaA, latestBatch, minutesAgo: 1);
        var second = Entry("feature/two", ShaB, latestBatch, minutesAgo: 1);
        var older = Entry("feature/old", ShaA, olderBatch, minutesAgo: 60);
        SetupRepo([first, second, older]);
        var command = new UndoCommand(_gitService, _trashService, console);
        var settings = new UndoCommand.Settings { Path = "/some/repo", ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("undo"), settings, CancellationToken.None);

        Assert.Equal(0, result);
        await _gitService.Received(1).CreateBranchAsync("/some/repo", "feature/one", ShaA, Arg.Any<CancellationToken>());
        await _gitService.Received(1).CreateBranchAsync("/some/repo", "feature/two", ShaB, Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().CreateBranchAsync("/some/repo", "feature/old", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _trashService.DidNotReceive().RemoveAsync("/some/repo", older.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Undo_returns_0_when_trash_is_empty()
    {
        var console = new TestConsole();
        SetupRepo([]);
        var command = new UndoCommand(_gitService, _trashService, console);
        var settings = new UndoCommand.Settings { Path = "/some/repo", ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("undo"), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("Nothing to undo", console.Output);
    }

    [Fact]
    public async Task Undo_returns_1_for_non_repository()
    {
        var console = new TestConsole();
        _gitService.IsGitRepositoryAsync("/not/a/repo", Arg.Any<CancellationToken>()).Returns(false);
        var command = new UndoCommand(_gitService, _trashService, console);
        var settings = new UndoCommand.Settings { Path = "/not/a/repo" };

        var result = await command.ExecuteForTestAsync(CreateContext("undo"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("is not a Git repository", console.Output);
    }
}
