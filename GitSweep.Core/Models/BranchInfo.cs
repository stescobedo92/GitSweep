namespace GitSweep.Core.Models;

/// <summary>
/// Describes a local branch and how it relates to the target branch.
/// </summary>
/// <param name="Name">Short branch name (for example <c>feature/login</c>).</param>
/// <param name="LastCommitDate">Date of the branch tip commit, or <see langword="null"/> when unknown.</param>
/// <param name="IsMerged">
/// <see langword="true"/> only when the branch tip is an ancestor of the target branch
/// (what <c>git branch --merged</c> reports). Squash/rebase-merged branches keep this
/// <see langword="false"/> and are flagged through <paramref name="MergeKind"/> instead.
/// </param>
/// <param name="MergeKind">How the branch was merged, or <see cref="BranchMergeKind.NotMerged"/>.</param>
/// <param name="UpstreamName">
/// Short name of the configured upstream (for example <c>origin/feature/login</c>),
/// or <see langword="null"/> when the branch has no upstream.
/// </param>
/// <param name="IsUpstreamGone">
/// <see langword="true"/> when an upstream is configured but its remote-tracking
/// ref no longer exists (what <c>git branch -vv</c> reports as <c>[gone]</c>,
/// typically after <c>git fetch --prune</c> removed it because the branch was
/// deleted on the remote). Gone alone never makes a branch safe to delete.
/// </param>
public record BranchInfo(
    string Name,
    DateTime? LastCommitDate,
    bool IsMerged,
    BranchMergeKind MergeKind = BranchMergeKind.NotMerged,
    string? UpstreamName = null,
    bool IsUpstreamGone = false)
{
    /// <summary>
    /// <see langword="true"/> when this branch is a safe gone candidate: its
    /// upstream is gone and its changes are effectively merged into the target
    /// (ancestor or squash/rebase), so deleting it locally loses no unique work.
    /// </summary>
    public bool IsSafeGoneCandidate => IsUpstreamGone && IsEffectivelyMerged;
    /// <summary>
    /// <see langword="true"/> when the branch was squash- or rebase-merged into the
    /// target branch even though its tip is not an ancestor of the target.
    /// </summary>
    public bool IsSquashOrRebaseMerged => MergeKind == BranchMergeKind.SquashOrRebase;

    /// <summary>
    /// <see langword="true"/> when no unique changes would be lost by deleting the
    /// branch: either an ancestor merge or a squash/rebase merge.
    /// </summary>
    public bool IsEffectivelyMerged => IsMerged || IsSquashOrRebaseMerged;
}
