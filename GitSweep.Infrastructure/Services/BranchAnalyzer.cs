using GitSweep.Core.Models;
using GitSweep.Core.Services;

namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Provides logic to analyze a collection of branches and identify stale or merged ones.
/// </summary>
public class BranchAnalyzer : IBranchAnalyzer
{
    /// <summary>
    /// Identifies branches that have been merged.
    /// </summary>
    /// <param name="branches">The collection of branches to analyze.</param>
    /// <returns>An enumerable of merged branches.</returns>
    public IEnumerable<BranchInfo> IdentifyMergedBranches(IReadOnlyList<BranchInfo> branches)
    {
        if (branches is null)
            throw new ArgumentNullException(nameof(branches));

        return branches.Where(b => b.IsMerged);
    }

    /// <summary>
    /// Identifies branches that are considered stale based on their last commit date.
    /// A branch is stale if its last commit date is before the cutoff date.
    /// Branches with a null last commit date are not considered stale.
    /// </summary>
    /// <param name="branches">The collection of branches to analyze.</param>
    /// <param name="staleAgeInMonths">The number of months to use as the cutoff for staleness.</param>
    /// <returns>An enumerable of stale branches.</returns>
    public IEnumerable<BranchInfo> IdentifyStaleBranches(IReadOnlyList<BranchInfo> branches, int staleAgeInMonths)
    {
        if (branches is null)
            throw new ArgumentNullException(nameof(branches));

        if (staleAgeInMonths < 0)
            throw new ArgumentOutOfRangeException(nameof(staleAgeInMonths), "Stale age in months cannot be negative.");

        var cutoffDate = DateTime.UtcNow.AddMonths(-staleAgeInMonths);

        return branches.Where(b =>
            b.LastCommitDate.HasValue &&
            b.LastCommitDate.Value < cutoffDate &&
            !b.IsMerged
        );
    }
}