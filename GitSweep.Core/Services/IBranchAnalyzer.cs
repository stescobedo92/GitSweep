using GitSweep.Core.Models;

namespace GitSweep.Core.Services;

public interface IBranchAnalyzer
{
    IEnumerable<BranchInfo> IdentifyStaleBranches(IReadOnlyList<BranchInfo> branches, int staleAgeInMonths);
    IEnumerable<BranchInfo> IdentifyMergedBranches(IReadOnlyList<BranchInfo> branches);
}
