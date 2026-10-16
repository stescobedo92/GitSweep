using GitSweep.Core.Models;
using GitSweep.Core.Services;
using Spectre.Console;

namespace GitSweep.Cli.Commands;

/// <summary>
/// Shared restore core for <c>restore</c> and <c>undo</c>: recreates one
/// trashed branch at its saved SHA. Never overwrites an existing branch and
/// only removes the trash entry after the branch really exists again.
/// </summary>
internal static class TrashRestoreHelper
{
    internal enum RestoreOutcome
    {
        Restored,
        AlreadyExists,
        Failed
    }

    internal sealed record RestoreResult(RestoreOutcome Outcome, string? ErrorMessage);

    internal static async Task<RestoreResult> RestoreEntryAsync(
        IGitRepositoryService gitService,
        ITrashService trashService,
        string repoPath,
        TrashEntry entry,
        CancellationToken cancellationToken)
    {
        if (await gitService.BranchExistsAsync(repoPath, entry.BranchName, cancellationToken))
        {
            return new RestoreResult(
                RestoreOutcome.AlreadyExists,
                $"Branch '{entry.BranchName}' already exists. Nothing was changed; the trash entry was kept.");
        }

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
        // restore reports AlreadyExists instead of recreating anything.
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
