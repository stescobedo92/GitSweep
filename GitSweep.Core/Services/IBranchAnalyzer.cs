using GitSweep.Core.Models;

namespace GitSweep.Core.Services;

public interface IBranchAnalyzer
{
    IEnumerable<BranchInfo> IdentifyStaleBranches(IReadOnlyList<BranchInfo> branches, int staleAgeInMonths);
    IEnumerable<BranchInfo> IdentifyMergedBranches(IReadOnlyList<BranchInfo> branches);

    /// <summary>Every branch whose upstream is gone ([gone]), merged or not.</summary>
    IEnumerable<BranchInfo> IdentifyGoneBranches(IReadOnlyList<BranchInfo> branches);

    /// <summary>Safe gone candidates only: upstream gone and effectively merged (ancestor or squash/rebase). A gone branch that is not merged is never a safe candidate.</summary>
    IEnumerable<BranchInfo> IdentifySafeGoneBranches(IReadOnlyList<BranchInfo> branches);
}
