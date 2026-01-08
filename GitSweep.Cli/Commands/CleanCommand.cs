using GitSweep.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace GitSweep.Cli.Commands;

public sealed class CleanCommand : AsyncCommand<CleanCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the git repository to clean (defaults to current directory).")]
        [CommandOption("-p|--path")]
        public string? Path { get; init; }

        [Description("Number of months after which a branch is considered stale (default: 6).")]
        [CommandOption("-a|--age")]
        [DefaultValue(6)]
        public int StaleAgeInMonths { get; init; } = 6;
    }

    private readonly IGitRepositoryService _gitService;
    private readonly IBranchAnalyzer _analyzer;

    public CleanCommand(IGitRepositoryService gitService, IBranchAnalyzer analyzer)
    {
        _gitService = gitService;
        _analyzer = analyzer;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var repoPath = settings.Path ?? Environment.CurrentDirectory;
        var staleAge = settings.StaleAgeInMonths;

        // Validation
        if (!await _gitService.IsGitRepositoryAsync(repoPath, cancellationToken))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] The specified path is not a Git repository.");
            return 1;
        }

        var defaultBranch = await _gitService.GetDefaultBranchNameAsync(repoPath, cancellationToken);
        AnsiConsole.MarkupLine($"[green]Analyzing repository:[/] [blue]{repoPath}[/]");
        AnsiConsole.MarkupLine($"[green]Target branch:[/] [blue]{defaultBranch}[/]\n");

        var allBranches = await _gitService.GetLocalBranchesAsync(repoPath, defaultBranch, cancellationToken);
        if (!allBranches.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No local branches found to analyze.[/]");
            return 0;
        }

        // Analysis
        var staleBranches = _analyzer.IdentifyStaleBranches(allBranches, staleAge).ToList();
        var mergedBranches = _analyzer.IdentifyMergedBranches(allBranches).ToList();

        var allCandidateBranches = staleBranches.Union(mergedBranches).DistinctBy(b => b.Name).ToList();

        if (!allCandidateBranches.Any())
        {
            AnsiConsole.MarkupLine("[green]No stale or merged branches found. Your repository is clean![/]");
            return 0;
        }

        // Interactive Selection
        var choices = allCandidateBranches
            .Select(b => new
            {
                Branch = b,
                Description = b.IsMerged
                    ? $"{b.Name} (merged)"
                    : $"{b.Name} (last commit: {b.LastCommitDate?.ToString("yyyy-MM-dd") ?? "unknown"})"
            })
            .ToList();

        var selectedBranches = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Select branches to delete")
                .NotRequired()
                .AddChoices(choices.Select(c => c.Branch.Name).ToArray())
        ).ToList();

        if (!selectedBranches.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No branches selected for deletion.[/]");
            return 0;
        }

        // Confirmation and Deletion
        AnsiConsole.MarkupLine($"\n[red]You are about to delete {selectedBranches.Count} branch(es). This action is irreversible.[/]");
        if (!AnsiConsole.Confirm("Are you sure you want to continue?"))
        {
            AnsiConsole.MarkupLine("[yellow]Operation cancelled.[/]");
            return 0;
        }

        var deletedCount = 0;
        foreach (var branchName in selectedBranches)
        {
            var deleteResult = await DeleteBranchAsync(repoPath, branchName, cancellationToken);
            if (deleteResult)
            {
                deletedCount++;
                AnsiConsole.MarkupLine($"[green]Deleted branch:[/] [blue]{branchName}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to delete branch:[/] [blue]{branchName}[/]");
            }
        }

        AnsiConsole.MarkupLine($"\n[green]Operation completed. {deletedCount} branch(es) deleted.[/]");
        return 0;
    }

    private static async Task<bool> DeleteBranchAsync(string repoPath, string branchName, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"branch -d {branchName}",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0;
    }
}