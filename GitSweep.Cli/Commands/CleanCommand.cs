using GitSweep.Core.Models;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
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

        [Description("Run 'git fetch --prune' before scanning so branches whose upstream was deleted on the remote are detected as gone.")]
        [CommandOption("--fetch")]
        public bool Fetch { get; init; }

        [Description("Only include safe gone branches: upstream gone and really merged into the target.")]
        [CommandOption("--gone-only")]
        public bool GoneOnly { get; init; }

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

            if (settings.GoneOnly && (settings.MergedOnly || settings.StaleOnly))
            {
                return "Use only one of --merged-only, --stale-only or --gone-only.";
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
    private readonly ITrashService _trashService;
    private readonly IBranchRiskClassifier _riskClassifier;
    private readonly IGitSweepConfigService _configService;
    private readonly IBranchProtectionService _protectionService;
    private readonly IAnsiConsole _console;

    [ActivatorUtilitiesConstructor]
    public CleanCommand(IGitRepositoryService gitService, IBranchAnalyzer analyzer, ITrashService trashService, IBranchRiskClassifier riskClassifier, IGitSweepConfigService configService, IBranchProtectionService protectionService, IAnsiConsole console)
    {
        _gitService = gitService;
        _analyzer = analyzer;
        _trashService = trashService;
        _riskClassifier = riskClassifier;
        _configService = configService ?? new GitSweepConfigService(gitService);
        _protectionService = protectionService ?? new BranchProtectionService();
        _console = console;
    }

    public CleanCommand(IGitRepositoryService gitService, IBranchAnalyzer analyzer, ITrashService trashService, IBranchRiskClassifier riskClassifier, IAnsiConsole console)
        : this(gitService, analyzer, trashService, riskClassifier, new GitSweepConfigService(gitService), new BranchProtectionService(), console)
    {
    }

    public CleanCommand(IGitRepositoryService gitService, IBranchAnalyzer analyzer, ITrashService trashService, IAnsiConsole console)
        : this(gitService, analyzer, trashService, new BranchRiskClassifier(), console)
    {
    }

    public CleanCommand(IGitRepositoryService gitService, IBranchAnalyzer analyzer, IBranchRiskClassifier riskClassifier, IAnsiConsole console)
        : this(gitService, analyzer, new UnavailableTrashService(), riskClassifier, console)
    {
    }

    /// <summary>
    /// Backwards-compatible constructor kept for existing callers/tests that
    /// predate the trash. Deletes through it use a trash that only succeeds
    /// when a real <see cref="ITrashService"/> was not required by the test;
    /// production always resolves the 4-argument constructor via DI.
    /// </summary>
    public CleanCommand(IGitRepositoryService gitService, IBranchAnalyzer analyzer, IAnsiConsole console)
        : this(gitService, analyzer, new UnavailableTrashService(), console)
    {
    }

    /// <summary>
    /// Fail-closed placeholder: without a real trash a branch must never be
    /// deleted, because its SHA would be lost.
    /// </summary>
    private sealed class UnavailableTrashService : ITrashService
    {
        public Task<TrashEntry> SaveAsync(string path, string branchName, string commitSha, Guid batchId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Trash service is not configured; refusing to delete without saving the branch SHA first.");

        public Task<IReadOnlyList<TrashEntry>> ListAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrashEntry>>([]);

        public Task<TrashEntry?> GetAsync(string path, string id, CancellationToken ct = default) =>
            Task.FromResult<TrashEntry?>(null);

        public Task<bool> RemoveAsync(string path, string id, CancellationToken ct = default) =>
            Task.FromResult(false);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
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

        // Protección configurable: un .gitsweep.json inválido falla cerrado.
        GitSweepConfig protectionConfig;
        string? currentBranch;
        IReadOnlyList<string> worktreeBranches;
        try
        {
            protectionConfig = await _configService.LoadAsync(repoPath, cancellationToken)
                ?? GitSweepConfig.Default;
            var currentTask = _gitService.GetCurrentBranchAsync(repoPath, cancellationToken);
            currentBranch = currentTask is null ? null : await currentTask;
            var worktreeTask = _gitService.GetWorktreeBranchesAsync(repoPath, cancellationToken);
            worktreeBranches = worktreeTask is null ? [] : await worktreeTask ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            WriteError($"Invalid .gitsweep.json protection configuration; refusing to clean: {ex.Message}");
            return 1;
        }

        if (settings.Fetch)
        {
            WriteProgressStep("Fetching remotes (prune)", 1, 1);
            var (fetchSuccess, fetchError) = await _gitService.FetchPruneAsync(repoPath, cancellationToken);
            if (!fetchSuccess)
            {
                // Redacted at the display point as well as in the service:
                // a fetch error quotes the remote URL, which may embed
                // credentials that must never reach the terminal.
                // The text is git's stderr, including any `remote:` sideband
                // lines the remote sent: control characters are made visible
                // (WriteError), only its line breaks are kept.
                var reason = string.IsNullOrWhiteSpace(fetchError) ? "unknown error" : GitOutputRedactor.Redact(fetchError.Trim());
                WriteError($"Fetch with prune failed; refusing to detect gone branches from stale remote state: {reason}", allowNewlines: true);
                return 1;
            }
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

        // Excluir protegidas (glob .gitsweep.json, rama actual, worktrees)
        // antes de cualquier análisis/borrado.
        var protectedBranches = _protectionService.FilterProtected(
            allBranches, protectionConfig, currentBranch, worktreeBranches, out var unprotectedBranches);
        if (protectedBranches.Count == 0)
            unprotectedBranches = allBranches;
        WriteProtectionSummary(protectedBranches, protectionConfig);

        var staleBranches = _analyzer.IdentifyStaleBranches(unprotectedBranches, staleAge).ToList();
        var mergedBranches = _analyzer.IdentifyMergedBranches(unprotectedBranches).ToList();
        var safeGoneBranches = (_analyzer.IdentifySafeGoneBranches(unprotectedBranches) ?? []).ToList();
        var candidates = BuildCandidates(staleBranches, mergedBranches, safeGoneBranches, settings).ToList();

        if (!candidates.Any())
        {
            WriteAnsiLine("No stale or merged branches found. Your repository is clean!", BrightGreen);
            return 0;
        }

        WriteCandidateTable(candidates);
        var riskAssessments = await AssessRiskAsync(repoPath, defaultBranch, candidates, cancellationToken);
        WriteRiskTable(riskAssessments);

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
        WriteAnsiLine($"You are about to delete {selectedCandidates.Count} branch(es). Each SHA is saved to .git/gitsweep/trash first, so 'gitsweep undo' can restore them.", Red);
        if (!settings.ConfirmDeletion && !_console.Confirm("Are you sure you want to continue?"))
        {
            WriteAnsiLine("Operation cancelled.", Yellow);
            return 0;
        }

        var deletedCount = 0;
        var failures = new List<(string BranchName, string ErrorMessage)>();
        var batchId = Guid.NewGuid();

        // In --gone-only mode the deletion decision was made from a scan
        // taken before the user confirmed; re-scan now, immediately before
        // deleting, so a branch that stopped being a safe gone candidate in
        // the meantime (upstream restored, new unique commits, merge state
        // changed) is skipped instead of deleted on stale evidence.
        HashSet<string>? freshSafeGoneNames = null;
        if (settings.GoneOnly)
        {
            var freshBranches = await _gitService.GetLocalBranchesAsync(repoPath, defaultBranch, cancellationToken);
            freshSafeGoneNames = (_analyzer.IdentifySafeGoneBranches(freshBranches) ?? [])
                .Select(branch => branch.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        for (var index = 0; index < selectedCandidates.Count; index++)
        {
            var candidate = selectedCandidates[index];
            cancellationToken.ThrowIfCancellationRequested();
            WriteProgressStep($"Deleting {TerminalText.Sanitize(candidate.Branch.Name)}", index + 1, selectedCandidates.Count);

            // Revalidar protección justo antes de borrar: la rama pudo
            // pasar a ser la actual o entrar en un worktree tras el escaneo.
            var freshCurrentTask = _gitService.GetCurrentBranchAsync(repoPath, cancellationToken);
            var freshCurrent = freshCurrentTask is null ? currentBranch : await freshCurrentTask ?? currentBranch;
            var freshWorktreeTask = _gitService.GetWorktreeBranchesAsync(repoPath, cancellationToken);
            var freshWorktrees = freshWorktreeTask is null ? worktreeBranches : await freshWorktreeTask ?? worktreeBranches;
            var protection = _protectionService.Evaluate(candidate.Branch.Name, protectionConfig, freshCurrent, freshWorktrees);
            if (protection.IsProtected)
            {
                failures.Add((candidate.Branch.Name, $"Branch is protected ({protection.Reason}); it was not deleted."));
                continue;
            }

            if (freshSafeGoneNames is not null && !freshSafeGoneNames.Contains(candidate.Branch.Name))
            {
                failures.Add((candidate.Branch.Name, "Branch is no longer a safe gone candidate (upstream restored or merge state changed); it was not deleted."));
                continue;
            }

            if (freshSafeGoneNames is not null)
            {
                // Re-validate this branch on its own fresh scan immediately
                // before it is touched: the batch re-scan above can itself
                // go stale while earlier branches are being processed, so a
                // branch whose upstream was restored or that gained unique
                // commits in the meantime is skipped for this branch too.
                var currentBranches = await _gitService.GetLocalBranchesAsync(repoPath, defaultBranch, cancellationToken);
                var stillSafe = (_analyzer.IdentifySafeGoneBranches(currentBranches) ?? [])
                    .Any(branch => branch.Name.Equals(candidate.Branch.Name, StringComparison.OrdinalIgnoreCase));
                if (!stillSafe)
                {
                    failures.Add((candidate.Branch.Name, "Branch is no longer a safe gone candidate (upstream restored or merge state changed); it was not deleted."));
                    continue;
                }
            }

            // Fail closed: capture the SHA and persist it to the trash before
            // any deletion. If either step fails the branch is left intact.
            TrashEntry trashEntry;
            try
            {
                var tipSha = await _gitService.GetBranchTipShaAsync(repoPath, candidate.Branch.Name, cancellationToken);
                if (string.IsNullOrWhiteSpace(tipSha))
                {
                    failures.Add((candidate.Branch.Name, "Could not resolve the branch tip SHA; branch was not deleted."));
                    continue;
                }

                trashEntry = await _trashService.SaveAsync(repoPath, candidate.Branch.Name, tipSha, batchId, cancellationToken);

                // Re-resolve the tip immediately before deleting: if the
                // branch moved after its SHA was saved, the trash would
                // restore a different commit than the one being deleted.
                // Fail closed and roll the entry back instead.
                var currentTip = await _gitService.GetBranchTipShaAsync(repoPath, candidate.Branch.Name, cancellationToken);
                if (currentTip is null ||
                    !string.Equals(currentTip, tipSha, StringComparison.OrdinalIgnoreCase))
                {
                    await RollbackTrashEntryAsync(repoPath, trashEntry, cancellationToken);
                    failures.Add((candidate.Branch.Name, "Branch tip changed after its SHA was saved to trash; branch was not deleted."));
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                failures.Add((candidate.Branch.Name, $"Could not save branch to trash: {ex.Message} Branch was not deleted."));
                continue;
            }

            var (success, errorMessage) = await _gitService.DeleteBranchAsync(repoPath, candidate.Branch.Name, cancellationToken);
            if (success)
            {
                deletedCount++;
            }
            else
            {
                // Roll back the trash entry: trash only holds branches that
                // were actually deleted.
                await RollbackTrashEntryAsync(repoPath, trashEntry, cancellationToken);

                failures.Add((candidate.Branch.Name, errorMessage));
            }
        }

        foreach (var failure in failures)
        {
            var reason = string.IsNullOrWhiteSpace(failure.ErrorMessage)
                ? "unknown error"
                : failure.ErrorMessage.Trim();
            _console.MarkupLine($"[red]Failed to delete branch '[blue]{TerminalText.Markup(failure.BranchName)}[/]':[/] {TerminalText.Markup(reason, allowNewlines: true)}");
            if (reason.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
            {
                _console.MarkupLine($"  [grey]Tip: Use [blue]git branch -D {TerminalText.Markup(failure.BranchName)}[/] to force-delete this branch if you no longer need its changes.[/]");
            }
        }

        WriteProgressComplete("Delete progress");
        var summaryColor = failures.Count == 0 ? "green" : "yellow";
        _console.MarkupLine($"\n[{summaryColor}]Operation completed. {deletedCount} branch(es) deleted, {failures.Count} failed.[/]");
        if (deletedCount > 0)
        {
            _console.Write(new Panel(
                "[grey]Deleted branch SHAs were saved to[/] [blue].git/gitsweep/trash[/][grey]. " +
                "Run[/] [green]gitsweep undo[/] [grey]to restore the last batch or[/] [green]gitsweep restore[/] [grey]to pick a branch.[/]")
            {
                Header = new PanelHeader("Trash / Undo"),
                Border = BoxBorder.Rounded
            });
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Best-effort removal of a trash entry whose branch was not deleted:
    /// the trash only holds branches that were actually deleted. A leftover
    /// entry is harmless; a later restore reports AlreadyExists.
    /// </summary>
    private async Task RollbackTrashEntryAsync(string repoPath, TrashEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _trashService.RemoveAsync(repoPath, entry.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort only.
        }
    }

    private async Task<List<BranchRiskAssessment>> AssessRiskAsync(
        string repoPath, string targetBranch, List<BranchCleanupCandidate> candidates, CancellationToken cancellationToken)
    {
        var assessments = new List<BranchRiskAssessment>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BranchRiskSignals? signals;
            try
            {
                signals = await _gitService.GetBranchRiskSignalsAsync(repoPath, targetBranch, candidate.Branch.Name, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // Fail-closed: sin señales la rama nunca se marca segura.
                signals = null;
            }

            var assessment = _riskClassifier.Classify(candidate.Branch, signals);
            assessments.Add(assessment ?? new BranchRiskAssessment(
                candidate.Branch, BranchRiskLevel.Review, BranchRiskSignals.Unknown,
                "Risk data is unknown; review before deleting."));
        }

        return assessments;
    }

    private void WriteProtectionSummary(IReadOnlyList<BranchProtectionResult> protectedBranches, GitSweepConfig config)
    {
        if (protectedBranches.Count == 0)
        {
            _console.MarkupLine("[grey]Protection: no branches excluded by .gitsweep.json, current-branch or worktree rules.[/]");
            return;
        }

        var table = new Table()
            .Title("[yellow]Protected branches (excluded)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Branch")
            .AddColumn("Reason")
            .AddColumn("Pattern");
        foreach (var item in protectedBranches.OrderBy(p => p.BranchName, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                $"[blue]{TerminalText.Markup(item.BranchName)}[/]",
                TerminalText.Markup(item.Reason.ToString()),
                TerminalText.Markup(item.MatchedPattern ?? "-"));
        }

        _console.Write(new Panel(table)
        {
            Header = new PanelHeader("Protection (.gitsweep.json)"),
            Border = BoxBorder.Rounded
        });
        _console.WriteLine();
    }

    internal static Table BuildProtectionTable(IReadOnlyList<BranchProtectionResult> protectedBranches)
    {
        var table = new Table().Title("[yellow]Protected branches (excluded)[/]").Border(TableBorder.Rounded)
            .AddColumn("Branch").AddColumn("Reason").AddColumn("Pattern");
        foreach (var item in protectedBranches)
            table.AddRow($"[blue]{TerminalText.Markup(item.BranchName)}[/]", TerminalText.Markup(item.Reason.ToString()), TerminalText.Markup(item.MatchedPattern ?? "-"));
        return table;
    }

    private void WriteRiskTable(IReadOnlyList<BranchRiskAssessment> assessments)
    {
        _console.Write(BuildRiskTable(assessments));
        _console.WriteLine();
        var safe = assessments.Count(a => a.Level == BranchRiskLevel.Safe);
        var review = assessments.Count(a => a.Level == BranchRiskLevel.Review);
        var dangerous = assessments.Count(a => a.Level == BranchRiskLevel.Dangerous);
        _console.MarkupLine(
            $"Risk summary: [green]{safe} segura[/], [yellow]{review} revisar[/], [red]{dangerous} peligrosa[/].");
        _console.WriteLine();
    }

    internal static Table BuildRiskTable(IReadOnlyList<BranchRiskAssessment> assessments)
    {
        var table = new Table()
            .Title("[green]Branch risk (semáforo)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Branch")
            .AddColumn("Risk")
            .AddColumn("Unique")
            .AddColumn("Unpushed")
            .AddColumn("Upstream")
            .AddColumn("Merged");

        foreach (var assessment in assessments)
        {
            var signals = assessment.Signals;
            table.AddRow(
                $"[blue]{TerminalText.Markup(assessment.Branch.Name)}[/]",
                RiskMarkup(assessment.Level),
                TerminalText.Markup(FormatCount(signals.UniqueCommitCount)),
                TerminalText.Markup(FormatCount(signals.UnpushedCommitCount)),
                signals.HasUpstream ? "yes" : assessment.Branch.IsUpstreamGone || signals.IsUpstreamGone ? "gone" : "no",
                assessment.Branch.IsEffectivelyMerged || signals.IsReallyMerged ? "yes" : "no");
        }

        return table;
    }

    private static string RiskMarkup(BranchRiskLevel level) => level switch
    {
        BranchRiskLevel.Safe => "[green]● segura[/]",
        BranchRiskLevel.Dangerous => "[red]● peligrosa[/]",
        _ => "[yellow]● revisar[/]"
    };

    private static string FormatCount(int? count) => count?.ToString(CultureInfo.InvariantCulture) ?? "?";

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
                .UseConverter(candidate => $"{TerminalText.Markup(candidate.Branch.Name)} [grey]({TerminalText.Markup(candidate.Reason)})[/]")
                .AddChoices(candidates)
        ).ToList();
    }

    private static IEnumerable<BranchCleanupCandidate> BuildCandidates(
        IReadOnlyCollection<BranchInfo> staleBranches,
        IReadOnlyCollection<BranchInfo> mergedBranches,
        Settings settings) =>
        BuildCandidates(staleBranches, mergedBranches, [], settings);

    private static IEnumerable<BranchCleanupCandidate> BuildCandidates(
        IReadOnlyCollection<BranchInfo> staleBranches,
        IReadOnlyCollection<BranchInfo> mergedBranches,
        IReadOnlyCollection<BranchInfo> safeGoneBranches,
        Settings settings)
    {
        var staleNames = staleBranches.Select(branch => branch.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var goneNames = safeGoneBranches.Select(branch => branch.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = settings.MergedOnly
            ? mergedBranches
            : settings.StaleOnly
                ? staleBranches
                : settings.GoneOnly
                    ? safeGoneBranches
                    : staleBranches.Concat(mergedBranches).Concat(safeGoneBranches);

        return candidates
            .DistinctBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(branch => branch.IsEffectivelyMerged)
            .ThenBy(branch => branch.LastCommitDate ?? DateTime.MaxValue)
            .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .Select(branch => new BranchCleanupCandidate(
                branch,
                goneNames.Contains(branch.Name) && settings.GoneOnly ? "gone" :
                branch.IsSquashOrRebaseMerged && goneNames.Contains(branch.Name) ? "gone (squash-merged)" :
                branch.IsSquashOrRebaseMerged ? "squash-merged" : branch.IsMerged && goneNames.Contains(branch.Name) ? "gone (merged)" :
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
        // Sanitized before the panel measures its column widths.
        var rows = new[]
        {
            ("Repository", TerminalText.Sanitize(repoPath), Blue),
            ("Target branch", TerminalText.Sanitize(defaultBranch), Green),
            ("Stale age", $"{staleAge} month(s)", Yellow),
            ("Mode", GetModeText(settings), Cyan),
        };

        WriteAsciiPanel("Scan settings", rows);
        _console.WriteLine();
    }

    private void WriteCandidateTable(IReadOnlyCollection<BranchCleanupCandidate> candidates)
    {
        // Branch names are sanitized before the column width is measured, so
        // a name with an escaped character (U+202E shown as \u202E) keeps the
        // table aligned.
        var rows = candidates
            .Select(candidate => (Candidate: candidate, Name: TerminalText.Sanitize(candidate.Branch.Name)))
            .ToList();
        var branchWidth = Math.Max("Branch".Length, rows.Max(row => row.Name.Length));
        var reasonWidth = Math.Max("Reason".Length, candidates.Max(candidate => candidate.Reason.Length));
        var dateWidth = Math.Max("Last commit".Length, candidates.Max(candidate => FormatDate(candidate.Branch.LastCommitDate).Length));
        var border = $"+-{new string('-', branchWidth)}-+-{new string('-', reasonWidth)}-+-{new string('-', dateWidth)}-+";

        WriteAnsiLine(border, Green);
        WriteTableRow("Branch", "Reason", "Last commit", branchWidth, reasonWidth, dateWidth, Bold + BrightGreen, Bold + BrightGreen, Bold + BrightGreen);
        WriteAnsiLine(border.Replace('-', '='), Green);

        foreach (var (candidate, name) in rows)
        {
            var reasonColor = candidate.Reason is "merged" or "squash-merged" || candidate.Reason.StartsWith("gone", StringComparison.Ordinal) ? BrightGreen : Yellow;
            WriteTableRow(
                name,
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

    // Raw writer: error messages quote external text (paths, exception and
    // config messages, git stderr), so the whole message is sanitized here.
    private void WriteError(string message, bool allowNewlines = false)
    {
        WriteAnsi("Error: ", Bold + Red);
        _console.Profile.Out.Writer.WriteLine(TerminalText.Sanitize(message, allowNewlines));
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

        if (settings.GoneOnly)
        {
            return "gone only";
        }

        return settings.Fetch ? "merged, stale and gone (after fetch --prune)" : "merged, stale and gone";
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

    // Raw writer: only the color is a GitSweep-authored escape sequence; the
    // text is always sanitized (a no-op for GitSweep's own labels), so no
    // repository-derived text can reach the terminal unsanitized this way.
    private void WriteAnsi(string text, string color)
    {
        _console.Profile.Out.Writer.Write(color);
        _console.Profile.Out.Writer.Write(TerminalText.Sanitize(text));
        _console.Profile.Out.Writer.Write(Reset);
    }

    private sealed record BranchCleanupCandidate(BranchInfo Branch, string Reason);
}
