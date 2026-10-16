using GitSweep.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace GitSweep.Cli.Commands;

/// <summary>
/// <c>gitsweep undo</c>: restores every branch deleted by the most recent
/// <c>clean</c> run (the newest trash batch). Older trash entries are left
/// untouched; use <c>restore</c> for individual branches.
/// </summary>
public sealed class UndoCommand : AsyncCommand<UndoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the git repository (defaults to current directory).")]
        [CommandOption("-p|--path")]
        public string? Path { get; init; }

        [Description("Skip the confirmation prompt.")]
        [CommandOption("-y|--yes")]
        public bool ConfirmRestore { get; init; }
    }

    private readonly IGitRepositoryService _gitService;
    private readonly ITrashService _trashService;
    private readonly IAnsiConsole _console;

    [ActivatorUtilitiesConstructor]
    public UndoCommand(IGitRepositoryService gitService, ITrashService trashService, IAnsiConsole console)
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
            _console.MarkupLine($"[red]Error:[/] {TerminalText.Markup(ex.Message)}");
            return 1;
        }
    }

    private async Task<int> ExecuteCoreAsync(Settings settings, CancellationToken cancellationToken)
    {
        var repoPath = settings.Path ?? Environment.CurrentDirectory;
        var displayPath = System.IO.Path.GetFullPath(repoPath);

        if (!await _gitService.IsGitRepositoryAsync(repoPath, cancellationToken))
        {
            _console.MarkupLine($"[red]Error:[/] '[blue]{TerminalText.Markup(displayPath)}[/]' is not a Git repository.");
            return 1;
        }

        var entries = await _trashService.ListAsync(repoPath, cancellationToken);
        if (entries.Count == 0)
        {
            _console.MarkupLine("[yellow]Nothing to undo. Trash is empty.[/]");
            return 0;
        }

        var batch = TrashRestoreHelper.LatestBatch(entries);
        _console.Write(new Panel(TrashRestoreHelper.BuildTrashTable(batch))
        {
            Header = new PanelHeader("Last deletion batch"),
            Border = BoxBorder.Rounded
        });
        _console.WriteLine();

        if (!settings.ConfirmRestore &&
            _console.Profile.Capabilities.Interactive &&
            !_console.Confirm($"Restore {batch.Count} branch(es) from the last deletion?"))
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
                var task = progress.AddTask("Undoing last deletion", maxValue: batch.Count);
                foreach (var entry in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Spectre parses a task description as markup (SEC-B01: a branch
                    // named "fix]" crashed the live display mid-batch).
                    task.Description = $"Restoring {TerminalText.Markup(entry.BranchName)}";
                    var result = await TrashRestoreHelper.RestoreEntryAsync(
                        _gitService, _trashService, repoPath, entry, cancellationToken);
                    switch (result.Outcome)
                    {
                        case TrashRestoreHelper.RestoreOutcome.Restored:
                            restored++;
                            _console.MarkupLine(
                                $"[green]Restored[/] '[blue]{TerminalText.Markup(entry.BranchName)}[/]' at [grey]{TerminalText.Markup(TrashRestoreHelper.ShortSha(entry.CommitSha))}[/].");
                            break;
                        case TrashRestoreHelper.RestoreOutcome.AlreadyRestored:
                            restored++;
                            _console.MarkupLine($"[green]{TerminalText.Markup(result.Message)}[/]");
                            break;
                        case TrashRestoreHelper.RestoreOutcome.AlreadyExists:
                            failures.Add(result.Message!);
                            _console.MarkupLine($"[yellow]{TerminalText.Markup(result.Message)}[/]");
                            break;
                        default:
                            failures.Add($"'{entry.BranchName}': {result.Message}");
                            _console.MarkupLine(
                                $"[red]Failed to restore '[blue]{TerminalText.Markup(entry.BranchName)}[/]':[/] {TerminalText.Markup(result.Message ?? "unknown error", allowNewlines: true)}");
                            break;
                    }

                    task.Increment(1);
                }
            });

        var summaryColor = failures.Count == 0 ? "green" : "yellow";
        _console.MarkupLine($"[{summaryColor}]Undo completed. {restored} branch(es) restored, {failures.Count} not restored.[/]");
        return failures.Count == 0 ? 0 : 1;
    }
}
