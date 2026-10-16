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
/// SEC-B01 / SEC-B02: every piece of external text (branch names, trash
/// fields, paths, git stderr, exception messages) reaches the terminal only
/// through <see cref="TerminalText"/>: it can neither break Spectre markup
/// (a branch named <c>fix]</c> crashed undo/restore mid-batch) nor smuggle
/// control sequences (CSI/OSC/BEL, bidi overrides) to the terminal.
/// </summary>
public sealed class TerminalOutputSanitizationTests
{
    private const string Repo = "/some/repo";
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    // OSC 0 (set window title, terminated by BEL), CSI 2J (erase screen) and
    // OSC 52 (write the clipboard): what a hostile remote or repo would send.
    private const string OscTitle = "\u001b]0;PWNED\u0007";
    private const string EraseScreen = "\u001b[2J";
    private const string OscClipboard = "\u001b]52;c;cHduZWQ=\u0007";

    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly ITrashService _trashService = Substitute.For<ITrashService>();
    private readonly IGitSweepConfigService _configService = Substitute.For<IGitSweepConfigService>();

    private static CommandContext CreateContext(string name) =>
        new([], Substitute.For<IRemainingArguments>(), name, null);

    private static TrashEntry Entry(string branch, string sha, Guid batch) =>
        new(Guid.NewGuid().ToString("N"), branch, sha, DateTimeOffset.UtcNow, batch);

    // Interactive + ANSI, like Program.cs forces in production: Progress then
    // uses the live renderer, whose TaskDescriptionColumn parses the task
    // description as markup.
    private static TestConsole InteractiveConsole() => new TestConsole().Interactive().Width(400);

    private void SetupTrash(IReadOnlyList<TrashEntry> entries)
    {
        _gitService.IsGitRepositoryAsync(Repo, Arg.Any<CancellationToken>()).Returns(true);
        _trashService.ListAsync(Repo, Arg.Any<CancellationToken>()).Returns(entries);
        _gitService.BranchExistsAsync(Repo, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _gitService.CreateBranchAsync(Repo, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, string.Empty));
        _trashService.RemoveAsync(Repo, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static void AssertNoRawControlSequences(string output)
    {
        Assert.DoesNotContain(OscTitle, output, StringComparison.Ordinal);
        Assert.DoesNotContain(EraseScreen, output, StringComparison.Ordinal);
        Assert.DoesNotContain(OscClipboard, output, StringComparison.Ordinal);
        // GitSweep never writes BEL or a bidi override itself.
        Assert.DoesNotContain('\u0007', output);
        Assert.DoesNotContain('\u202e', output);
    }

    // ---- SEC-B01: ']' in a branch name must not break the progress display ----

    [Fact]
    public async Task Restore_by_branch_named_with_a_closing_bracket_completes_and_shows_the_name()
    {
        var console = InteractiveConsole();
        var entry = Entry("fix]", ShaA, Guid.NewGuid());
        SetupTrash([entry]);
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = Repo, Branch = "fix]", ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.True(result == 0, console.Output);
        await _gitService.Received(1).CreateBranchAsync(Repo, "fix]", ShaA, Arg.Any<CancellationToken>());
        await _trashService.Received(1).RemoveAsync(Repo, entry.Id, Arg.Any<CancellationToken>());
        Assert.Contains("Restored 'fix]' at aaaaaaaa.", console.Output, StringComparison.Ordinal);
        Assert.Contains("Restoring fix]", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Error", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_all_with_a_bracket_branch_restores_every_entry_and_shows_the_name()
    {
        var console = InteractiveConsole();
        var first = Entry("feature/first", ShaA, Guid.NewGuid());
        var bracket = Entry("fix]", ShaB, Guid.NewGuid());
        SetupTrash([first, bracket]);
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = Repo, RestoreAll = true, ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.True(result == 0, console.Output);
        await _gitService.Received(1).CreateBranchAsync(Repo, "feature/first", ShaA, Arg.Any<CancellationToken>());
        await _gitService.Received(1).CreateBranchAsync(Repo, "fix]", ShaB, Arg.Any<CancellationToken>());
        Assert.Contains("Restored 'fix]' at bbbbbbbb.", console.Output, StringComparison.Ordinal);
        Assert.Contains("Restore completed. 2 branch(es) restored, 0 not restored.", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Error", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_with_a_bracket_branch_restores_the_whole_batch_and_shows_the_name()
    {
        var console = InteractiveConsole();
        var batch = Guid.NewGuid();
        var first = Entry("feature/first", ShaA, batch);
        var bracket = Entry("fix]", ShaB, batch);
        SetupTrash([first, bracket]);
        var command = new UndoCommand(_gitService, _trashService, console);
        var settings = new UndoCommand.Settings { Path = Repo, ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("undo"), settings, CancellationToken.None);

        Assert.True(result == 0, console.Output);
        await _gitService.Received(1).CreateBranchAsync(Repo, "fix]", ShaB, Arg.Any<CancellationToken>());
        Assert.Contains("Restored 'fix]' at bbbbbbbb.", console.Output, StringComparison.Ordinal);
        Assert.Contains("Restoring fix]", console.Output, StringComparison.Ordinal);
        Assert.Contains("Undo completed. 2 branch(es) restored, 0 not restored.", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Error", console.Output, StringComparison.Ordinal);
    }

    // ---- SEC-B02: clean ----

    [Fact]
    public async Task Fetch_failure_with_escape_sequences_prints_them_as_visible_escapes()
    {
        var console = new TestConsole().Width(400);
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), new BranchRiskClassifier(), console);
        var settings = new CleanCommand.Settings { Path = Repo, Fetch = true, GoneOnly = true, DryRun = true };
        _gitService.IsGitRepositoryAsync(Repo, Arg.Any<CancellationToken>()).Returns(true);
        _gitService.FetchPruneAsync(Repo, Arg.Any<CancellationToken>())
            .Returns((false, $"remote: pre{OscTitle}post{EraseScreen}\nfatal: could not read from remote repository"));

        var result = await command.ExecuteForTestAsync(CreateContext("clean"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        AssertNoRawControlSequences(console.Output);
        Assert.Contains(@"remote: pre\x1B]0;PWNED\x07post\x1B[2J", console.Output, StringComparison.Ordinal);
        // The multi-line git error block keeps its line break.
        Assert.Contains("\nfatal: could not read from remote repository", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_failure_from_git_is_printed_with_visible_escapes()
    {
        var console = new TestConsole().Width(400);
        const string branch = "feature/\u202eexe.txt";
        SetupCleanRepo([new BranchInfo(branch, DateTime.UtcNow, true)]);
        _trashService.SaveAsync(Repo, branch, ShaA, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new TrashEntry(Guid.NewGuid().ToString("N"), branch, ShaA, DateTimeOffset.UtcNow, Guid.NewGuid()));
        _gitService.DeleteBranchAsync(Repo, branch, Arg.Any<CancellationToken>())
            .Returns((false, $"error: the branch is not fully merged{OscClipboard}{EraseScreen}"));
        var command = CreateCleanCommand(console);
        var settings = new CleanCommand.Settings { Path = Repo, SelectAll = true, ConfirmDeletion = true };

        var result = await command.ExecuteForTestAsync(CreateContext("clean"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        AssertNoRawControlSequences(console.Output);
        Assert.Contains(@"Failed to delete branch 'feature/\u202Eexe.txt': error: the branch is not fully merged\x1B]52;c;cHduZWQ=\x07\x1B[2J", console.Output, StringComparison.Ordinal);
        Assert.Contains(@"git branch -D feature/\u202Eexe.txt", console.Output, StringComparison.Ordinal);
        Assert.Contains(@"Deleting feature/\u202Eexe.txt", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_derived_names_in_every_clean_table_and_panel_are_sanitized()
    {
        var console = new TestConsole().Interactive().Width(400);
        console.Input.PushKey(ConsoleKey.Enter);
        const string candidate = "feature/\u202eexe.txt";
        const string protectedBranch = "release/\u202e1";
        const string target = "ma\u200bin";
        SetupCleanRepo(
            [new BranchInfo(candidate, DateTime.UtcNow, true), new BranchInfo(protectedBranch, DateTime.UtcNow, true)],
            target,
            new GitSweepConfig(["release/\u202e*"]));
        var command = CreateCleanCommand(console);
        var settings = new CleanCommand.Settings { Path = Repo, DryRun = true };

        var result = await command.ExecuteForTestAsync(CreateContext("clean"), settings, CancellationToken.None);

        Assert.True(result == 0, console.Output);
        AssertNoRawControlSequences(console.Output);
        Assert.DoesNotContain('\u200b', console.Output);
        // Scan settings panel (raw writer): target branch.
        Assert.Contains(@"ma\u200Bin", console.Output, StringComparison.Ordinal);
        // Protection table (markup): branch and the .gitsweep.json pattern.
        Assert.Contains(@"release/\u202E1", console.Output, StringComparison.Ordinal);
        Assert.Contains(@"release/\u202E*", console.Output, StringComparison.Ordinal);
        // Candidate table (raw writer), risk table and selection prompt (markup).
        Assert.True(CountOccurrences(console.Output, @"feature/\u202Eexe.txt") >= 3, console.Output);
    }

    [Fact]
    public async Task Exception_message_reaching_clean_is_printed_with_visible_escapes()
    {
        var console = new TestConsole().Width(400);
        SetupCleanRepo([]);
        _gitService.GetLocalBranchesAsync(Repo, "main", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException($"git said {EraseScreen}{OscTitle}"));
        var command = CreateCleanCommand(console);
        var settings = new CleanCommand.Settings { Path = Repo, DryRun = true };

        var result = await command.ExecuteForTestAsync(CreateContext("clean"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        AssertNoRawControlSequences(console.Output);
        Assert.Contains(@"git said \x1B[2J\x1B]0;PWNED\x07", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_error_message_is_printed_with_visible_escapes()
    {
        var console = new TestConsole().Width(400);
        SetupCleanRepo([]);
        _configService.LoadAsync(Repo, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException($".gitsweep.json at '/r{OscTitle}' is a symbolic link"));
        var command = CreateCleanCommand(console);
        var settings = new CleanCommand.Settings { Path = Repo, DryRun = true };

        var result = await command.ExecuteForTestAsync(CreateContext("clean"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        AssertNoRawControlSequences(console.Output);
        Assert.Contains(@".gitsweep.json at '/r\x1B]0;PWNED\x07' is a symbolic link", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_repository_path_is_printed_with_visible_escapes_by_every_command()
    {
        var path = $"/no/such{OscTitle}repo";
        var expected = Path.GetFullPath(path).Replace("\u001b", @"\x1B").Replace("\u0007", @"\x07");

        var cleanConsole = new TestConsole().Width(400);
        var clean = CreateCleanCommand(cleanConsole);
        Assert.Equal(1, await clean.ExecuteForTestAsync(CreateContext("clean"), new CleanCommand.Settings { Path = path }, CancellationToken.None));

        var restoreConsole = new TestConsole().Width(400);
        var restore = new RestoreCommand(_gitService, _trashService, restoreConsole);
        Assert.Equal(1, await restore.ExecuteForTestAsync(CreateContext("restore"), new RestoreCommand.Settings { Path = path }, CancellationToken.None));

        var undoConsole = new TestConsole().Width(400);
        var undo = new UndoCommand(_gitService, _trashService, undoConsole);
        Assert.Equal(1, await undo.ExecuteForTestAsync(CreateContext("undo"), new UndoCommand.Settings { Path = path }, CancellationToken.None));

        foreach (var output in new[] { cleanConsole.Output, restoreConsole.Output, undoConsole.Output })
        {
            AssertNoRawControlSequences(output);
            Assert.Contains(expected, output, StringComparison.Ordinal);
            Assert.Contains("is not a Git repository", output, StringComparison.Ordinal);
        }
    }

    // ---- SEC-B02: restore / undo ----

    [Fact]
    public async Task Restore_failure_from_git_and_trash_fields_are_printed_with_visible_escapes()
    {
        var console = InteractiveConsole();
        var hostileSha = $"{EraseScreen}aaaa" + new string('a', 36);
        var hostileId = $"{OscTitle}0123456789abcdef0123456789";
        var entry = new TrashEntry(hostileId, "feature/\u202eexe.txt", hostileSha, DateTimeOffset.UtcNow, Guid.NewGuid());
        SetupTrash([entry]);
        _gitService.CreateBranchAsync(Repo, entry.BranchName, hostileSha, Arg.Any<CancellationToken>())
            .Returns((false, $"fatal: not a valid object name{OscClipboard}"));
        var command = new RestoreCommand(_gitService, _trashService, console);
        var settings = new RestoreCommand.Settings { Path = Repo, RestoreAll = true, ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("restore"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        AssertNoRawControlSequences(console.Output);
        Assert.Contains(@"Failed to restore 'feature/\u202Eexe.txt': fatal: not a valid object name\x1B]52;c;cHduZWQ=\x07", console.Output, StringComparison.Ordinal);
        // Trash table: short SHA and id prefix as read from the trash file.
        Assert.Contains(@"\x1B[2Jaaaa", console.Output, StringComparison.Ordinal);
        Assert.Contains(@"\x1B]0;PWNE", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_already_exists_message_and_progress_description_are_sanitized()
    {
        var console = InteractiveConsole();
        var entry = Entry("feature/\u202eexe.txt", ShaA, Guid.NewGuid());
        SetupTrash([entry]);
        _gitService.BranchExistsAsync(Repo, entry.BranchName, Arg.Any<CancellationToken>()).Returns(true);
        var command = new UndoCommand(_gitService, _trashService, console);
        var settings = new UndoCommand.Settings { Path = Repo, ConfirmRestore = true };

        var result = await command.ExecuteForTestAsync(CreateContext("undo"), settings, CancellationToken.None);

        Assert.Equal(1, result);
        AssertNoRawControlSequences(console.Output);
        Assert.Contains(@"Branch 'feature/\u202Eexe.txt' already exists.", console.Output, StringComparison.Ordinal);
        Assert.Contains(@"Restoring feature/\u202Eexe.txt", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_selection_prompt_and_missing_branch_message_are_sanitized()
    {
        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Enter);
        var entry = Entry("feature/\u202eexe.txt", ShaA, Guid.NewGuid());
        SetupTrash([entry]);
        var command = new RestoreCommand(_gitService, _trashService, console);

        var picked = await command.ExecuteForTestAsync(
            CreateContext("restore"), new RestoreCommand.Settings { Path = Repo, ConfirmRestore = true }, CancellationToken.None);

        Assert.True(picked == 0, console.Output);
        await _gitService.Received(1).CreateBranchAsync(Repo, entry.BranchName, ShaA, Arg.Any<CancellationToken>());
        Assert.True(CountOccurrences(console.Output, @"feature/\u202Eexe.txt") >= 3, console.Output);

        var missingConsole = new TestConsole().Width(400);
        var missing = await new RestoreCommand(_gitService, _trashService, missingConsole).ExecuteForTestAsync(
            CreateContext("restore"), new RestoreCommand.Settings { Path = Repo, Branch = $"x{EraseScreen}", ConfirmRestore = true }, CancellationToken.None);

        Assert.Equal(1, missing);
        AssertNoRawControlSequences(console.Output);
        AssertNoRawControlSequences(missingConsole.Output);
        Assert.Contains(@"No deleted branch named 'x\x1B[2J' found in trash.", missingConsole.Output, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private CleanCommand CreateCleanCommand(TestConsole console) =>
        new(_gitService, new BranchAnalyzer(), _trashService, new BranchRiskClassifier(),
            _configService, new BranchProtectionService(), console);

    private void SetupCleanRepo(List<BranchInfo> branches, string target = "main", GitSweepConfig? config = null)
    {
        _gitService.IsGitRepositoryAsync(Repo, Arg.Any<CancellationToken>()).Returns(true);
        _configService.LoadAsync(Repo, Arg.Any<CancellationToken>()).Returns(config ?? GitSweepConfig.Default);
        _gitService.GetCurrentBranchAsync(Repo, Arg.Any<CancellationToken>()).Returns(target);
        _gitService.GetWorktreeBranchesAsync(Repo, Arg.Any<CancellationToken>()).Returns([target]);
        _gitService.GetDefaultBranchNameAsync(Repo, Arg.Any<CancellationToken>()).Returns(target);
        _gitService.GetLocalBranchesAsync(Repo, target, Arg.Any<CancellationToken>()).Returns(branches);
        _gitService.GetBranchTipShaAsync(Repo, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ShaA);
        _gitService.GetBranchRiskSignalsAsync(Repo, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, true, true));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
