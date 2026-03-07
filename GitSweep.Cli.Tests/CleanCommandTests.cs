using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using NSubstitute;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

public sealed class CleanCommandTests
{
    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly IBranchAnalyzer _analyzer = Substitute.For<IBranchAnalyzer>();

    private CleanCommand CreateCommand(TestConsole console) =>
        new(_gitService, _analyzer, console);

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    [Fact]
    public void Settings_has_default_stale_age_of_6_months()
    {
        var settings = new CleanCommand.Settings();

        Assert.Equal(6, settings.StaleAgeInMonths);
    }

    [Fact]
    public void Settings_path_defaults_to_null()
    {
        var settings = new CleanCommand.Settings();

        Assert.Null(settings.Path);
    }

    [Fact]
    public async Task ExecuteAsync_returns_1_when_stale_age_is_negative()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var settings = new CleanCommand.Settings { StaleAgeInMonths = -1 };

        var result = await command.ExecuteAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("must be a non-negative number", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_returns_1_when_path_is_not_a_git_repository()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var settings = new CleanCommand.Settings { Path = "/not/a/repo" };

        _gitService.IsGitRepositoryAsync("/not/a/repo", Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await command.ExecuteAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("is not a Git repository", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_returns_0_when_no_local_branches_found()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var settings = new CleanCommand.Settings { Path = "/some/repo" };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await command.ExecuteAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("No local branches found to analyze", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_returns_0_when_no_stale_or_merged_branches_found()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var settings = new CleanCommand.Settings { Path = "/some/repo" };
        var branches = new List<BranchInfo> { new("feature/active", DateTime.UtcNow, false) };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns(branches);
        _analyzer.IdentifyStaleBranches(branches, 6).Returns([]);
        _analyzer.IdentifyMergedBranches(branches).Returns([]);

        var result = await command.ExecuteAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("No stale or merged branches found", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_returns_0_when_no_branches_selected_in_prompt()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        // Press Enter immediately to confirm empty selection (NotRequired prompt)
        console.Input.PushKey(ConsoleKey.Enter);

        var command = CreateCommand(console);
        var settings = new CleanCommand.Settings { Path = "/some/repo" };
        var branches = new List<BranchInfo> { new("feature/stale", DateTime.UtcNow.AddMonths(-7), false) };
        var staleBranches = new List<BranchInfo> { branches[0] };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns(branches);
        _analyzer.IdentifyStaleBranches(branches, 6).Returns(staleBranches);
        _analyzer.IdentifyMergedBranches(branches).Returns([]);

        var result = await command.ExecuteAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("No branches selected for deletion", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_returns_0_when_user_cancels_confirmation()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        // Press Space to select the first branch, then Enter to confirm the selection
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);
        // Then decline the deletion confirmation
        console.Input.PushTextWithEnter("n");

        var command = CreateCommand(console);
        var settings = new CleanCommand.Settings { Path = "/some/repo" };
        var branches = new List<BranchInfo> { new("feature/stale", DateTime.UtcNow.AddMonths(-7), false) };
        var staleBranches = new List<BranchInfo> { branches[0] };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns(branches);
        _analyzer.IdentifyStaleBranches(branches, 6).Returns(staleBranches);
        _analyzer.IdentifyMergedBranches(branches).Returns([]);

        var result = await command.ExecuteAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("Operation cancelled", console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_uses_current_directory_when_path_not_specified()
    {
        var console = new TestConsole();
        var command = CreateCommand(console);
        var settings = new CleanCommand.Settings();

        _gitService.IsGitRepositoryAsync(Environment.CurrentDirectory, Arg.Any<CancellationToken>())
            .Returns(false);

        await command.ExecuteAsync(CreateContext(), settings, CancellationToken.None);

        await _gitService.Received(1)
            .IsGitRepositoryAsync(Environment.CurrentDirectory, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_includes_both_stale_and_merged_candidates()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        // Press Enter immediately to skip selection
        console.Input.PushKey(ConsoleKey.Enter);

        var command = CreateCommand(console);
        var settings = new CleanCommand.Settings { Path = "/some/repo" };

        var stale = new BranchInfo("feature/stale", DateTime.UtcNow.AddMonths(-7), false);
        var merged = new BranchInfo("feature/merged", DateTime.UtcNow, true);
        var branches = new List<BranchInfo> { stale, merged };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns(branches);
        _analyzer.IdentifyStaleBranches(branches, 6).Returns([stale]);
        _analyzer.IdentifyMergedBranches(branches).Returns([merged]);

        var result = await command.ExecuteAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        // Both branches appear as choices, user picks none → "No branches selected"
        Assert.Contains("No branches selected for deletion", console.Output);
    }
}
