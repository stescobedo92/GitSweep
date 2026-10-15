namespace GitSweep.Core.Models;

/// <summary>
/// Describes how a local branch relates to the target branch.
/// </summary>
public enum BranchMergeKind
{
    /// <summary>The branch tip is not merged into the target branch.</summary>
    NotMerged = 0,

    /// <summary>
    /// The branch tip is an ancestor of the target branch, as reported by
    /// <c>git branch --merged</c> (regular merge or fast-forward).
    /// </summary>
    Ancestor = 1,

    /// <summary>
    /// The branch tip is not an ancestor of the target branch, but its changes
    /// are already present there, as produced by a squash merge, a rebase merge
    /// or cherry-picked commits. Detected locally with
    /// <c>git merge-base</c> + <c>git commit-tree</c> / <c>git cherry</c>,
    /// without any hosting-provider API.
    /// </summary>
    SquashOrRebase = 2
}
