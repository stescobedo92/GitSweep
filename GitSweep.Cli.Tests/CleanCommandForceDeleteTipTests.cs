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
/// I1 (final-review.md): when git refuses a branch as "not fully merged",
/// clean prints a <c>git branch -D</c> tip that users paste into a shell. A
/// repository-controlled name such as <c>x;touch${IFS}pwned</c> is a valid
/// branch name, so it must never be spelled out there; only a name made of
/// shell-inert characters is, after <c>--</c>.
/// </summary>
public sealed class CleanCommandForceDeleteTipTests
{
    private const string Repo = "/some/repo";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly ITrashService _trashService = Substitute.For<ITrashService>();
    private readonly IGitSweepConfigService _configService = Substitute.For<IGitSweepConfigService>();

    [Theory]
    [InlineData("x;touch${IFS}pwned")]
    [InlineData("feature/$(id)")]
    [InlineData("a&b")]
    [InlineData("it's")]
    [InlineData("x|y")]
    [InlineData("`id`")]
    [InlineData("a\"b")]
    [InlineData("a!b")]
    [InlineData("100%")]
    public async Task Shell_unsafe_branch_name_is_never_spelled_out_in_the_force_delete_tip(string branch)
    {
        var output = await RunCleanWhereGitRefusesAsync(branch);

        var tip = Assert.Single(TipLines(output));
        Assert.Contains("git branch -D -- <name>", tip, StringComparison.Ordinal);
        Assert.DoesNotContain(branch, tip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("feature/login_v1.2-fix")]
    [InlineData("HOTFIX-42")]
    public async Task Shell_safe_branch_name_is_spelled_out_after_a_double_dash(string branch)
    {
        var output = await RunCleanWhereGitRefusesAsync(branch);

        var tip = Assert.Single(TipLines(output));
        Assert.Contains($"git branch -D -- {branch} ", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_ascii_letters_digits_dot_underscore_slash_and_hyphen_are_shell_safe()
    {
        Assert.True(CleanCommand.IsShellSafeBranchName("release/1.0_rc-2"));
        foreach (var unsafeName in new[] { "", "a b", "a;b", "a$b", "a`b", "a'b", "a\"b", "a&b", "a|b", "a<b", "a>b", "a(b", "a)b", "a{b", "a!b", "a%b", "a@b", "a#b", "a=b", "a,b", "café", "a‮b" })
        {
            Assert.False(CleanCommand.IsShellSafeBranchName(unsafeName), unsafeName);
        }
    }

    private static IEnumerable<string> TipLines(string output) =>
        output.Split('\n').Where(line => line.Contains("Tip:", StringComparison.Ordinal));

    private async Task<string> RunCleanWhereGitRefusesAsync(string branch)
    {
        var console = new TestConsole().Width(400);
        _gitService.IsGitRepositoryAsync(Repo, Arg.Any<CancellationToken>()).Returns(true);
        _configService.LoadAsync(Repo, Arg.Any<CancellationToken>()).Returns(GitSweepConfig.Default);
        _gitService.GetCurrentBranchAsync(Repo, Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetWorktreeBranchesAsync(Repo, Arg.Any<CancellationToken>()).Returns(["main"]);
        _gitService.GetDefaultBranchNameAsync(Repo, Arg.Any<CancellationToken>()).Returns("main");
        _gitService.GetLocalBranchesAsync(Repo, "main", Arg.Any<CancellationToken>())
            .Returns([new BranchInfo(branch, DateTime.UtcNow, true)]);
        _gitService.GetBranchTipShaAsync(Repo, branch, Arg.Any<CancellationToken>()).Returns(Sha);
        _gitService.GetBranchRiskSignalsAsync(Repo, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, true, true));
        _trashService.SaveAsync(Repo, branch, Sha, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new TrashEntry(Guid.NewGuid().ToString("N"), branch, Sha, DateTimeOffset.UtcNow, Guid.NewGuid()));
        _gitService.DeleteBranchAsync(Repo, branch, Arg.Any<CancellationToken>())
            .Returns((false, $"error: the branch '{branch}' is not fully merged"));
        var command = new CleanCommand(_gitService, new BranchAnalyzer(), _trashService, new BranchRiskClassifier(),
            _configService, new BranchProtectionService(), console);
        var settings = new CleanCommand.Settings { Path = Repo, SelectAll = true, ConfirmDeletion = true };

        var result = await command.ExecuteForTestAsync(
            new CommandContext([], Substitute.For<IRemainingArguments>(), "clean", null), settings, CancellationToken.None);

        Assert.Equal(1, result);
        return console.Output;
    }
}
