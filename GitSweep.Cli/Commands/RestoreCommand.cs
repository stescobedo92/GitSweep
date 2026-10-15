using GitSweep.Core.Models;
using GitSweep.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace GitSweep.Cli.Commands;

/// <summary>
/// <c>gitsweep restore</c>: lists branches saved in
/// <c>.git/gitsweep/trash</c> and recreates one (or all) at the exact SHA
/// captured before deletion. Existing branches are never overwritten.
/// </summary>
public sealed class RestoreCommand : AsyncCommand<RestoreCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the git repository (defaults to current directory).")]
        [CommandOption("-p|--path")]
        public string? Path { get; init; }

        [Description("Restore only the most recently deleted copy of this branch.")]
        [CommandOption("--branch")]
        public string? Branch { get; init; }

        [Description("Restore every branch in the trash.")]
        [CommandOption("--all")]
        public bool RestoreAll { get; init; }

        [Description("Skip the confirmation prompt.")]
        [CommandOption("-y|--yes")]
        public bool ConfirmRestore { get; init; }

        public override ValidationResult Validate()
        {
            var error = ValidateValues(this);
            return error is null ? ValidationResult.Success() : ValidationResult.Error(error);
        }

        internal static string? ValidateValues(Settings settings)
        {
            if (settings.RestoreAll && !string.IsNullOrWhiteSpace(settings.Branch))
            {
                return "Use either --branch or --all, not both.";
            }

            return null;
        }
    }

    private readonly IGitRepositoryService _gitService;
    private readonly ITrashService _trashService;
    private readonly IAnsiConsole _console;

    [ActivatorUtilitiesConstructor]
    public RestoreCommand(IGitRepositoryService gitService, ITrashService trashService, IAnsiConsole console)
    {
        _gitService = gitService;
        _trashService = trashService;
        _console = console;
    }

    internal Task<int> ExecuteForTestAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        return ExecuteAsync(context, settings, cancellationToken);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var validationError = Settings.ValidateValues(settings);
        if (validationError is not null)
        {
            _console.MarkupLine($"[red]Error:[/] {Markup.Escape(validationError)}");
            return 1;
        }

        try
        {
            return await ExecuteCoreAsync(settings, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _console.MarkupLine("[yellow]Operation cancelled.[/]");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private async Task<int> ExecuteCoreAsync(Settings settings, CancellationToken cancellationToken)
    {
        var repoPath = settings.Path ?? Environment.CurrentDirectory;
        var displayPath = System.IO.Path.GetFullPath(repoPath);

        if (!await _gitService.IsGitRepositoryAsync(repoPath, cancellationToken))
        {
            _console.MarkupLine($"[red]Error:[/] '[blue]{Markup.Escape(displayPath)}[/]' is not a Git repository.");
            return 1;
        }

        var entries = await _trashService.ListAsync(repoPath, cancellationToken);
        if (entries.Count == 0)
        {
            _console.MarkupLine("[yellow]Trash is empty. No deleted branches to restore.[/]");
            return 0;
        }

        _console.Write(TrashRestoreHelper.BuildTrashTable(entries));
        _console.WriteLine();

        var selected = SelectEntries(entries, settings);
        if (selected is null)
        {
            return 1;
        }

        if (selected.Count == 0)
        {
            _console.MarkupLine("[yellow]No branches selected for restore.[/]");
            return 0;
        }

        if (!settings.ConfirmRestore &&
            _console.Profile.Capabilities.Interactive &&
            !_console.Confirm($"Restore {selected.Count} branch(es)?"))
        {
            _console.MarkupLine("[yellow]Operation cancelled.[/]");
            return 0;
        }

        var restored = 0;
        var failures = new List<string>();

        await _console.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async progress =>
            {
                var task = progress.AddTask("Restoring branches", maxValue: selected.Count);
                foreach (var entry in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    task.Description = $"Restoring {entry.BranchName}";
                    var result = await TrashRestoreHelper.RestoreEntryAsync(
                        _gitService, _trashService, repoPath, entry, cancellationToken);
                    switch (result.Outcome)
                    {
                        case TrashRestoreHelper.RestoreOutcome.Restored:
                            restored++;
                            _console.MarkupLine(
                                $"[green]Restored[/] '[blue]{Markup.Escape(entry.BranchName)}[/]' at [grey]{Markup.Escape(TrashRestoreHelper.ShortSha(entry.CommitSha))}[/].");
                            break;
                        case TrashRestoreHelper.RestoreOutcome.AlreadyExists:
                            failures.Add(result.ErrorMessage!);
                            _console.MarkupLine($"[yellow]{Markup.Escape(result.ErrorMessage!)}[/]");
                            break;
                        default:
                            failures.Add($"'{entry.BranchName}': {result.ErrorMessage}");
                            _console.MarkupLine(
                                $"[red]Failed to restore '[blue]{Markup.Escape(entry.BranchName)}[/]':[/] {Markup.Escape(result.ErrorMessage ?? "unknown error")}");
                            break;
                    }

                    task.Increment(1);
                }
            });

        var summaryColor = failures.Count == 0 ? "green" : "yellow";
        _console.MarkupLine($"[{summaryColor}]Restore completed. {restored} branch(es) restored, {failures.Count} not restored.[/]");
        return failures.Count == 0 ? 0 : 1;
    }

    private List<TrashEntry>? SelectEntries(IReadOnlyList<TrashEntry> entries, Settings settings)
    {
        if (settings.RestoreAll)
        {
            return entries.ToList();
        }

        if (!string.IsNullOrWhiteSpace(settings.Branch))
        {
            // ListAsync is newest-first, so the first match is the most
            // recently deleted copy of that branch.
            var match = entries.FirstOrDefault(
                entry => string.Equals(entry.BranchName, settings.Branch, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _console.MarkupLine(
                    $"[red]Error:[/] No deleted branch named '[blue]{Markup.Escape(settings.Branch)}[/]' found in trash.");
                return null;
            }

            return [match];
        }

        if (!_console.Profile.Capabilities.Interactive)
        {
            _console.MarkupLine(
                "[red]Error:[/] Interactive selection is unavailable in this terminal. Re-run with --branch <name> or --all.");
            return null;
        }

        var chosen = _console.Prompt(
            new SelectionPrompt<TrashEntry>()
                .Title("Select a branch to restore")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to reveal more branches.)[/]")
                .UseConverter(entry =>
                    $"{Markup.Escape(entry.BranchName)} [grey]({Markup.Escape(TrashRestoreHelper.ShortSha(entry.CommitSha))}, {entry.DeletedAtUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC)[/]")
                .AddChoices(entries));
        return [chosen];
    }
}
