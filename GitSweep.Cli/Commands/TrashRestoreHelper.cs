using GitSweep.Core.Models;
using GitSweep.Core.Services;
using Spectre.Console;

namespace GitSweep.Cli.Commands;

/// <summary>
/// Shared restore core for <c>restore</c> and <c>undo</c>: recreates one
/// trashed branch at its saved SHA. Never overwrites an existing branch and
/// only removes the trash entry after the branch really exists again at the
/// saved SHA.
/// </summary>
internal static class TrashRestoreHelper
{
    internal enum RestoreOutcome
    {
        Restored,
        AlreadyRestored,
        AlreadyExists,
        Failed
    }

    internal sealed record RestoreResult(RestoreOutcome Outcome, string? Message);

    internal static async Task<RestoreResult> RestoreEntryAsync(
        IGitRepositoryService gitService,
        ITrashService trashService,
        string repoPath,
        TrashEntry entry,
        CancellationToken cancellationToken)
    {
        if (await gitService.BranchExistsAsync(repoPath, entry.BranchName, cancellationToken))
        {
            // I2: the branch can already be back at the saved commit (clean's
            // post-delete check recreated it, a limit failure struck after
            // `git branch -d`, or `git checkout <name>` recreated it from its
            // remote-tracking branch). The entry has then served its purpose:
            // drop it (and its keep-ref, the branch now holds the commit)
            // instead of reporting a failure on every run.
            var existingTip = await gitService.GetBranchTipShaAsync(repoPath, entry.BranchName, cancellationToken);
            if (string.Equals(existingTip, entry.CommitSha, StringComparison.OrdinalIgnoreCase))
            {
                // RR-3: only say it was removed when RemoveAsync confirms it;
                // the branch already holds the commit either way.
                var removed = await trashService.RemoveAsync(repoPath, entry.Id, cancellationToken);
                return new RestoreResult(
                    RestoreOutcome.AlreadyRestored,
                    $"Branch '{entry.BranchName}' already exists at the saved commit {ShortSha(entry.CommitSha)}; " +
                    (removed ? "its trash entry was removed." : "its trash entry is no longer needed."));
            }

            // RR-4/O-1: the recovery command uses `<sha>^{commit}` (never the
            // bare SHA, which git resolves through a ref of the same name,
            // V-01), quoted so it is safe to paste in cmd, PowerShell, bash and
            // zsh. Only the saved SHA (validated hex) is shown, never the
            // branch name, which comes from the trash file.
            return new RestoreResult(
                RestoreOutcome.AlreadyExists,
                $"Branch '{entry.BranchName}' already exists. It does not point at the saved commit {ShortSha(entry.CommitSha)}, so nothing was changed and the trash entry was kept. " +
                $"To get the saved commit back, rename the existing branch and run restore again, or create it under another name with: git branch <new-name> \"{entry.CommitSha}^{{commit}}\"");
        }

        // CreateBranchAsync reports success only when the new branch points
        // at exactly the saved SHA (V-01), so the entry and its keep-ref are
        // never dropped for a commit that was not restored.
        var (success, errorMessage) = await gitService.CreateBranchAsync(
            repoPath, entry.BranchName, entry.CommitSha, cancellationToken);
        if (!success)
        {
            return new RestoreResult(
                RestoreOutcome.Failed,
                string.IsNullOrWhiteSpace(errorMessage) ? "git branch failed." : errorMessage.Trim());
        }

        // The branch exists again, so the trash entry has served its purpose.
        // A removal failure is not fatal: the entry stays listed and a later
        // restore finds the branch at the saved commit and drops it then.
        await trashService.RemoveAsync(repoPath, entry.Id, cancellationToken);
        return new RestoreResult(RestoreOutcome.Restored, null);
    }

    internal static Table BuildTrashTable(IReadOnlyList<TrashEntry> entries)
    {
        var table = new Table()
            .Title("[green]Deleted branches (trash)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Branch")
            .AddColumn("Commit")
            .AddColumn("Deleted (UTC)")
            .AddColumn("Id");

        foreach (var entry in entries)
        {
            table.AddRow(
                $"[blue]{TerminalText.Markup(entry.BranchName)}[/]",
                TerminalText.Markup(ShortSha(entry.CommitSha)),
                TerminalText.Markup(entry.DeletedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm")),
                $"[grey]{TerminalText.Markup(entry.Id[..8])}…[/]");
        }

        return table;
    }

    internal static string ShortSha(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>
    /// The batch <c>undo</c> restores: every entry sharing the newest
    /// entry's <see cref="TrashEntry.BatchId"/>. Entries arrive newest-first;
    /// a legacy empty batch degrades to just the newest entry.
    /// </summary>
    internal static IReadOnlyList<TrashEntry> LatestBatch(IReadOnlyList<TrashEntry> entriesNewestFirst)
    {
        if (entriesNewestFirst.Count == 0)
        {
            return [];
        }

        var latest = entriesNewestFirst[0];
        if (latest.BatchId == Guid.Empty)
        {
            return [latest];
        }

        return entriesNewestFirst.Where(entry => entry.BatchId == latest.BatchId).ToList();
    }
}
