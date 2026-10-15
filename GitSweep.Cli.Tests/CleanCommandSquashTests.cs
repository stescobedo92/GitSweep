using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

public sealed class CleanCommandSquashTests
{
    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly IBranchAnalyzer _analyzer = Substitute.For<IBranchAnalyzer>();

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    [Fact]
    public async Task ExecuteAsync_dry_run_labels_squash_merged_candidate()
    {
        var console = new TestConsole();
        var command = new CleanCommand(_gitService, _analyzer, console);
        var settings = new CleanCommand.Settings { Path = "/some/repo", DryRun = true };

        var squash = new BranchInfo("feature/squash", DateTime.UtcNow, false, BranchMergeKind.SquashOrRebase);
        var branches = new List<BranchInfo> { squash };

        _gitService.IsGitRepositoryAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetDefaultBranchNameAsync("/some/repo", Arg.Any<CancellationToken>())
            .Returns("main");
        _gitService.GetLocalBranchesAsync("/some/repo", "main", Arg.Any<CancellationToken>())
            .Returns(branches);
        _analyzer.IdentifyStaleBranches(branches, 6).Returns([]);
        _analyzer.IdentifyMergedBranches(branches).Returns([squash]);

        var result = await command.ExecuteForTestAsync(CreateContext(), settings, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("feature/squash", console.Output);
        Assert.Contains("squash-merged", console.Output);
        Assert.Contains("Dry run enabled", console.Output);
        await _gitService.DidNotReceive()
            .DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
