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
/// Extremo a extremo con el <see cref="GitSweepConfigService"/> real: un
/// <c>.gitsweep.json</c> solo puede añadir protección (main/master siempre
/// quedan protegidas) y uno con propiedades desconocidas falla cerrado.
/// </summary>
public sealed class CleanCommandConfigProtectionTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "GitSweepCliConfigTests", Guid.NewGuid().ToString("N"));
    private readonly IGitRepositoryService _gitService = Substitute.For<IGitRepositoryService>();
    private readonly IBranchAnalyzer _analyzer = Substitute.For<IBranchAnalyzer>();
    private readonly ITrashService _trashService = Substitute.For<ITrashService>();

    public CleanCommandConfigProtectionTests() => Directory.CreateDirectory(_repo);
    public void Dispose() { try { Directory.Delete(_repo, true); } catch { } }

    private static CommandContext CreateContext() =>
        new([], Substitute.For<IRemainingArguments>(), "clean", null);

    private CleanCommand CreateCommand(TestConsole console) =>
        new(_gitService, _analyzer, _trashService, new BranchRiskClassifier(),
            new GitSweepConfigService(), new BranchProtectionService(), console);

    private void SetupRepo(List<BranchInfo> branches)
    {
        _gitService.IsGitRepositoryAsync(_repo, Arg.Any<CancellationToken>()).Returns(true);
        // El destino es develop: main y master son ramas normales para el analizador.
        _gitService.GetDefaultBranchNameAsync(_repo, Arg.Any<CancellationToken>()).Returns("develop");
        _gitService.GetLocalBranchesAsync(_repo, "develop", Arg.Any<CancellationToken>()).Returns(branches);
        _gitService.GetCurrentBranchAsync(_repo, Arg.Any<CancellationToken>()).Returns("develop");
        _gitService.GetWorktreeBranchesAsync(_repo, Arg.Any<CancellationToken>()).Returns(["develop"]);
        _analyzer.IdentifyStaleBranches(Arg.Any<IReadOnlyList<BranchInfo>>(), Arg.Any<int>()).Returns([]);
        _analyzer.IdentifyMergedBranches(Arg.Any<IReadOnlyList<BranchInfo>>())
            .Returns(ci => ((IReadOnlyList<BranchInfo>)ci[0]).Where(b => b.IsEffectivelyMerged).ToList());
        _analyzer.IdentifySafeGoneBranches(Arg.Any<IReadOnlyList<BranchInfo>>()).Returns([]);
        _gitService.GetBranchTipShaAsync(_repo, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Sha);
        _gitService.GetBranchRiskSignalsAsync(_repo, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BranchRiskSignals(0, 0, true, true));
        _trashService.SaveAsync(_repo, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TrashEntry(Guid.NewGuid().ToString("N"), (string)ci[1], Sha, DateTimeOffset.UtcNow, Guid.NewGuid()));
        _gitService.DeleteBranchAsync(_repo, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((true, string.Empty));
    }

    private static List<BranchInfo> MergedBranches() =>
    [
        new("main", DateTime.UtcNow, true),
        new("master", DateTime.UtcNow, true),
        new("release/1.0", DateTime.UtcNow, true),
        new("feature/merged", DateTime.UtcNow, true),
    ];

    private static CleanCommand.Settings AutomatedSettings(string repo) =>
        new() { Path = repo, SelectAll = true, ConfirmDeletion = true, MergedOnly = true };

    [Theory]
    [InlineData("""{"protectedBranches":["release/*"]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"protectedBranches":[]}""")]
    public async Task Config_file_never_removes_builtin_main_master_protection(string json)
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, GitSweepConfig.FileName), json);
        SetupRepo(MergedBranches());
        var console = new TestConsole();

        var result = await CreateCommand(console).ExecuteForTestAsync(CreateContext(), AutomatedSettings(_repo), CancellationToken.None);

        Assert.Equal(0, result);
        await _gitService.DidNotReceive().DeleteBranchAsync(_repo, "main", Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().DeleteBranchAsync(_repo, "master", Arg.Any<CancellationToken>());
        await _gitService.Received(1).DeleteBranchAsync(_repo, "feature/merged", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Misspelled_property_fails_closed_and_deletes_nothing()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, GitSweepConfig.FileName), """{"protected_branches":["release/*"]}""");
        SetupRepo(MergedBranches());
        var console = new TestConsole();

        var result = await CreateCommand(console).ExecuteForTestAsync(CreateContext(), AutomatedSettings(_repo), CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("Invalid .gitsweep.json", console.Output);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().GetLocalBranchesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_property_error_names_its_line_and_says_refusing_to_clean_once()
    {
        // I4: valid JSON with an editor's $schema key used to be reported as
        // "not valid JSON" with "refusing to clean" twice and no location.
        await File.WriteAllTextAsync(Path.Combine(_repo, GitSweepConfig.FileName),
            "{\n  \"$schema\": \"https://example/schema.json\",\n  \"protectedBranches\": [\"release/*\"]\n}");
        SetupRepo(MergedBranches());
        var console = new TestConsole().Width(400);

        var result = await CreateCommand(console).ExecuteForTestAsync(CreateContext(), AutomatedSettings(_repo), CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("has an unknown property at line 2", console.Output);
        Assert.DoesNotContain("not valid JSON", console.Output);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(console.Output, "refusing to clean"));
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
