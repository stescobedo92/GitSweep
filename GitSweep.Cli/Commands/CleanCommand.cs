using GitSweep.Core.Models;
using GitSweep.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;

namespace GitSweep.Cli.Commands;

public sealed class CleanCommand : AsyncCommand<CleanCommand.Settings>
{
    private const string Reset = "\u001b[0m";
    private const string Green = "\u001b[32m";
    private const string BrightGreen = "\u001b[92m";
    private const string Blue = "\u001b[34m";
    private const string Cyan = "\u001b[36m";
    private const string Yellow = "\u001b[33m";
    private const string Red = "\u001b[31m";
    private const string Grey = "\u001b[90m";
    private const string Bold = "\u001b[1m";

    public sealed class Settings : CommandSettings
    {
        [Description("Path to the git repository to clean (defaults to current directory).")]
        [CommandOption("-p|--path")]
        public string? Path { get; init; }

        [Description("Number of months after which a branch is considered stale (default: 6).")]
        [CommandOption("-a|--age")]
        [DefaultValue(6)]
        public int StaleAgeInMonths { get; init; } = 6;

        [Description("Select every candidate branch without showing the multi-select prompt.")]
        [CommandOption("--all")]
        public bool SelectAll { get; init; }

        [Description("Skip the final confirmation prompt. Intended for automation with --all.")]
        [CommandOption("-y|--yes")]
        public bool ConfirmDeletion { get; init; }

        [Description("Preview the branches that would be deleted without deleting anything.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [Description("Only include branches already merged into the target branch.")]
        [CommandOption("--merged-only")]
        public bool MergedOnly { get; init; }

        [Description("Only include stale branches older than the configured age.")]
        [CommandOption("--stale-only")]
        public bool StaleOnly { get; init; }

        public override ValidationResult Validate()
        {
            var error = ValidateValues(this);
            return error is null ? ValidationResult.Success() : ValidationResult.Error(error);
        }

        internal static string? ValidateValues(Settings settings)
        {
            if (settings.StaleAgeInMonths < 0)
            {
                return "The stale age (--age) must be a non-negative number.";
            }

            if (settings.MergedOnly && settings.StaleOnly)
            {
                return "Use either --merged-only or --stale-only, not both.";
            }

            if (settings.ConfirmDeletion && !settings.SelectAll)
            {
                return "The --yes option must be used with --all so the selected branches are explicit.";
            }

            return null;
        }
    }

    private readonly IGitRepositoryService _gitService;
    private readonly IBranchAnalyzer _analyzer;
    private readonly IAnsiConsole _console;

    public CleanCommand(IGitRepositoryService gitService, IBranchAnalyzer analyzer, IAnsiConsole console)
    {
        _gitService = gitService;
        _analyzer = analyzer;
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
            WriteError(validationError);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    private async Task<int> ExecuteCoreAsync(Settings settings, CancellationToken cancellationToken)
    {
        var repoPath = settings.Path ?? Environment.CurrentDirectory;
        var displayPath = Path.GetFullPath(repoPath);
        var staleAge = settings.StaleAgeInMonths;

        WriteHeader();

        var isGitRepository = await _gitService.IsGitRepositoryAsync(repoPath, cancellationToken);
        if (!isGitRepository)
        {
            WriteError($"'{displayPath}' is not a Git repository. Ensure the path is correct and contains a .git directory.");
            return 1;
        }

        string defaultBranch = string.Empty;
        List<BranchInfo> allBranches = [];

        WriteProgressStep("Resolving target branch", 1, 3);
        defaultBranch = await _gitService.GetDefaultBranchNameAsync(repoPath, cancellationToken);

        WriteProgressStep("Reading local branches", 2, 3);
        allBranches = await _gitService.GetLocalBranchesAsync(repoPath, defaultBranch, cancellationToken);

        WriteProgressStep("Preparing branch analysis", 3, 3);

        WriteProgressComplete("Scan progress");
        _console.WriteLine();
        WriteRepositorySummary(displayPath, defaultBranch, staleAge, settings);

        if (!allBranches.Any())
        {
            WriteAnsiLine("No local branches found to analyze.", Yellow);
            return 0;
        }

        var staleBranches = _analyzer.IdentifyStaleBranches(allBranches, staleAge).ToList();
        var mergedBranches = _analyzer.IdentifyMergedBranches(allBranches).ToList();
        var candidates = BuildCandidates(staleBranches, mergedBranches, settings).ToList();

        if (!candidates.Any())
        {
            WriteAnsiLine("No stale or merged branches found. Your repository is clean!", BrightGreen);
            return 0;
        }

        WriteCandidateTable(candidates);

        if (settings.DryRun)
        {
            WriteAnsiLine("Dry run enabled. No branches will be deleted.", Yellow);

            if (_console.Profile.Capabilities.Interactive)
            {
                var previewSelection = SelectCandidates(candidates, settings);
                var selectedCount = previewSelection.Count;
                var message = selectedCount == 0
                    ? "Preview selection empty. Nothing would be deleted."
                    : $"Preview selection complete. {selectedCount} branch(es) would be deleted in a real run.";
                WriteAnsiLine(message, selectedCount == 0 ? Yellow : BrightGreen);
            }

            return 0;
        }

        var selectedCandidates = SelectCandidates(candidates, settings);
        if (!selectedCandidates.Any())
        {
            WriteAnsiLine("No branches selected for deletion.", Yellow);
            return 0;
        }

        _console.WriteLine();
        WriteAnsiLine($"You are about to delete {selectedCandidates.Count} branch(es). This action is irreversible.", Red);
        if (!settings.ConfirmDeletion && !_console.Confirm("Are you sure you want to continue?"))
        {
            WriteAnsiLine("Operation cancelled.", Yellow);
            return 0;
        }

        var deletedCount = 0;
        var failures = new List<(string BranchName, string ErrorMessage)>();

        for (var index = 0; index < selectedCandidates.Count; index++)
        {
            var candidate = selectedCandidates[index];
            cancellationToken.ThrowIfCancellationRequested();
            WriteProgressStep($"Deleting {candidate.Branch.Name}", index + 1, selectedCandidates.Count);

            var (success, errorMessage) = await _gitService.DeleteBranchAsync(repoPath, candidate.Branch.Name, cancellationToken);
            if (success)
            {
                deletedCount++;
            }
            else
            {
                failures.Add((candidate.Branch.Name, errorMessage));
            }
        }

        foreach (var failure in failures)
        {
            var reason = string.IsNullOrWhiteSpace(failure.ErrorMessage)
                ? "unknown error"
                : failure.ErrorMessage.Trim();
            _console.MarkupLine($"[red]Failed to delete branch '[blue]{Markup.Escape(failure.BranchName)}[/]':[/] {Markup.Escape(reason)}");
            if (reason.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
            {
                _console.MarkupLine($"  [grey]Tip: Use [blue]git branch -D {Markup.Escape(failure.BranchName)}[/] to force-delete this branch if you no longer need its changes.[/]");
            }
        }

        WriteProgressComplete("Delete progress");
        var summaryColor = failures.Count == 0 ? "green" : "yellow";
        _console.MarkupLine($"\n[{summaryColor}]Operation completed. {deletedCount} branch(es) deleted, {failures.Count} failed.[/]");
        return failures.Count == 0 ? 0 : 1;
    }

    private List<BranchCleanupCandidate> SelectCandidates(List<BranchCleanupCandidate> candidates, Settings settings)
    {
        if (settings.SelectAll)
        {
            return candidates;
        }

        if (!_console.Profile.Capabilities.Interactive)
        {
            WriteError("Interactive selection is unavailable in this terminal. Re-run with --all to select every listed candidate.");
            return [];
        }

        return _console.Prompt(
            new MultiSelectionPrompt<BranchCleanupCandidate>()
                .Title("Select branches to delete")
                .NotRequired()
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to reveal more branches.)[/]")
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle a branch, [green]<enter>[/] to continue.)[/]")
                .UseConverter(candidate => $"{Markup.Escape(candidate.Branch.Name)} [grey]({Markup.Escape(candidate.Reason)})[/]")
                .AddChoices(candidates)
        ).ToList();
    }

    private static IEnumerable<BranchCleanupCandidate> BuildCandidates(
        IReadOnlyCollection<BranchInfo> staleBranches,
        IReadOnlyCollection<BranchInfo> mergedBranches,
        Settings settings)
    {
        var staleNames = staleBranches.Select(branch => branch.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = settings.MergedOnly
            ? mergedBranches
            : settings.StaleOnly
                ? staleBranches
                : staleBranches.Concat(mergedBranches);

        return candidates
            .DistinctBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(branch => branch.IsMerged)
            .ThenBy(branch => branch.LastCommitDate ?? DateTime.MaxValue)
            .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .Select(branch => new BranchCleanupCandidate(
                branch,
                branch.IsMerged ? "merged" : staleNames.Contains(branch.Name) ? "stale" : "candidate"));
    }

    private void WriteHeader()
    {
        var banner = new[]
        {
            "   ____   _   _     ____                                   ",
            "  / ___| (_) | |_  / ___|  __      __   ___    ___   _ __  ",
            " | |  _  | | | __| \\___ \\  \\ \\ /\\ / /  / _ \\  / _ \\ | '_ \\ ",
            " | |_| | | | | |_   ___) |  \\ V  V /  |  __/ |  __/ | |_) |",
            "  \\____| |_|  \\__| |____/    \\_/\\_/    \\___|  \\___| | .__/ ",
            "                                                    |_|    ",
        };

        foreach (var line in banner)
        {
            WriteAnsiLine(line, BrightGreen);
        }

        WriteAnsiLine("Interactive local branch cleanup for Git repositories.", Green);
        _console.WriteLine();
    }

    private void WriteRepositorySummary(string repoPath, string defaultBranch, int staleAge, Settings settings)
    {
        var rows = new[]
        {
            ("Repository", repoPath, Blue),
            ("Target branch", defaultBranch, Green),
            ("Stale age", $"{staleAge} month(s)", Yellow),
            ("Mode", GetModeText(settings), Cyan),
        };

        WriteAsciiPanel("Scan settings", rows);
        _console.WriteLine();
    }

    private void WriteCandidateTable(IReadOnlyCollection<BranchCleanupCandidate> candidates)
    {
        var branchWidth = Math.Max("Branch".Length, candidates.Max(candidate => candidate.Branch.Name.Length));
        var reasonWidth = Math.Max("Reason".Length, candidates.Max(candidate => candidate.Reason.Length));
        var dateWidth = Math.Max("Last commit".Length, candidates.Max(candidate => FormatDate(candidate.Branch.LastCommitDate).Length));
        var border = $"+-{new string('-', branchWidth)}-+-{new string('-', reasonWidth)}-+-{new string('-', dateWidth)}-+";

        WriteAnsiLine(border, Green);
        WriteTableRow("Branch", "Reason", "Last commit", branchWidth, reasonWidth, dateWidth, Bold + BrightGreen, Bold + BrightGreen, Bold + BrightGreen);
        WriteAnsiLine(border.Replace('-', '='), Green);

        foreach (var candidate in candidates)
        {
            var reasonColor = candidate.Reason == "merged" ? BrightGreen : Yellow;
            WriteTableRow(
                candidate.Branch.Name,
                candidate.Reason,
                FormatDate(candidate.Branch.LastCommitDate),
                branchWidth,
                reasonWidth,
                dateWidth,
                Blue,
                reasonColor,
                Grey);
        }

        WriteAnsiLine(border, Green);
        _console.WriteLine();
    }

    private void WriteProgressComplete(string label)
    {
        WriteAnsi(label, Green);
        _console.Profile.Out.Writer.Write(" ");
        WriteAnsi("[####################]", BrightGreen);
        _console.Profile.Out.Writer.Write(" ");
        WriteAnsiLine("100%", Bold + BrightGreen);
    }

    private void WriteError(string message)
    {
        WriteAnsi("Error: ", Bold + Red);
        _console.Profile.Out.Writer.WriteLine(message);
    }

    private static string GetModeText(Settings settings)
    {
        if (settings.MergedOnly)
        {
            return "merged only";
        }

        if (settings.StaleOnly)
        {
            return "stale only";
        }

        return "merged and stale";
    }

    private static string FormatDate(DateTime? date)
    {
        return date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown";
    }

    private void WriteAsciiPanel(string title, IReadOnlyList<(string Label, string Value, string Color)> rows)
    {
        var labelWidth = rows.Max(row => row.Label.Length);
        var valueWidth = rows.Max(row => row.Value.Length);
        var contentWidth = labelWidth + 2 + valueWidth;
        var titleText = $"-{title}";
        var top = $"+{titleText}{new string('-', Math.Max(0, contentWidth + 2 - titleText.Length))}+";
        var bottom = $"+{new string('-', contentWidth + 2)}+";

        WriteAnsiLine(top, Green);
        foreach (var row in rows)
        {
            WriteAnsi("| ", Green);
            WriteAnsi(row.Label.PadRight(labelWidth), Bold);
            _console.Profile.Out.Writer.Write("  ");
            WriteAnsi(row.Value.PadRight(valueWidth), row.Color);
            WriteAnsiLine(" |", Green);
        }

        WriteAnsiLine(bottom, Green);
    }

    private void WriteProgressStep(string label, int completed, int total)
    {
        const int width = 24;
        var safeTotal = Math.Max(1, total);
        var ratio = Math.Clamp(completed / (double)safeTotal, 0, 1);
        var filled = (int)Math.Round(ratio * width);
        var empty = width - filled;
        var percent = (int)Math.Round(ratio * 100);

        WriteAnsi(label.PadRight(28), Green);
        _console.Profile.Out.Writer.Write(" ");
        WriteAnsi("[", Grey);
        WriteAnsi(new string('#', filled), BrightGreen);
        WriteAnsi(new string('-', empty), Grey);
        WriteAnsi("]", Grey);
        _console.Profile.Out.Writer.Write(" ");
        WriteAnsiLine($"{percent,3}%", Bold + BrightGreen);
    }

    private void WriteTableRow(
        string branch,
        string reason,
        string date,
        int branchWidth,
        int reasonWidth,
        int dateWidth,
        string branchColor,
        string reasonColor,
        string dateColor)
    {
        WriteAnsi("| ", Green);
        WriteAnsi(branch.PadRight(branchWidth), branchColor);
        WriteAnsi(" | ", Green);
        WriteAnsi(reason.PadRight(reasonWidth), reasonColor);
        WriteAnsi(" | ", Green);
        WriteAnsi(date.PadRight(dateWidth), dateColor);
        WriteAnsiLine(" |", Green);
    }

    private void WriteAnsiLine(string text, string color)
    {
        WriteAnsi(text, color);
        _console.Profile.Out.Writer.WriteLine();
    }

    private void WriteAnsi(string text, string color)
    {
        _console.Profile.Out.Writer.Write(color);
        _console.Profile.Out.Writer.Write(text);
        _console.Profile.Out.Writer.Write(Reset);
    }

    private sealed record BranchCleanupCandidate(BranchInfo Branch, string Reason);
}
