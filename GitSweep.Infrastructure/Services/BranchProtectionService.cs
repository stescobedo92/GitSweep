using GitSweep.Core.Models;
using GitSweep.Core.Services;

namespace GitSweep.Infrastructure.Services;

public sealed class BranchProtectionService : IBranchProtectionService
{
    public BranchProtectionResult Evaluate(
        string branchName, GitSweepConfig config, string? currentBranch,
        IReadOnlyCollection<string>? worktreeBranches)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(branchName) || !GitRepositoryService.IsValidBranchName(branchName))
            return new BranchProtectionResult(branchName ?? string.Empty, BranchProtectionReason.GlobPattern, "<invalid>");

        if (config.ExcludeCurrentBranch && !string.IsNullOrWhiteSpace(currentBranch) &&
            string.Equals(branchName, currentBranch, StringComparison.OrdinalIgnoreCase))
            return new BranchProtectionResult(branchName, BranchProtectionReason.CurrentBranch);

        if (config.ExcludeWorktreeBranches && worktreeBranches is not null &&
            worktreeBranches.Any(w => string.Equals(w, branchName, StringComparison.OrdinalIgnoreCase)))
            return new BranchProtectionResult(branchName, BranchProtectionReason.WorktreeBranch);

        var pattern = BranchGlobMatcher.FirstMatchingPattern(branchName, config.ProtectedBranches ?? []);
        return pattern is not null
            ? new BranchProtectionResult(branchName, BranchProtectionReason.GlobPattern, pattern)
            : BranchProtectionResult.NotProtected(branchName);
    }

    public IReadOnlyList<BranchProtectionResult> FilterProtected(
        IEnumerable<BranchInfo> branches, GitSweepConfig config, string? currentBranch,
        IReadOnlyCollection<string>? worktreeBranches, out List<BranchInfo> unprotected)
    {
        ArgumentNullException.ThrowIfNull(branches);
        var protectedResults = new List<BranchProtectionResult>();
        unprotected = [];
        foreach (var branch in branches)
        {
            var result = Evaluate(branch.Name, config, currentBranch, worktreeBranches);
            if (result.IsProtected) protectedResults.Add(result);
            else unprotected.Add(branch);
        }
        return protectedResults;
    }
}
