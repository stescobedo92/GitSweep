using System;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Core.Tests;

public class BranchRiskClassifierTests
{
    private readonly BranchRiskClassifier _sut = new();

    [Fact]
    public void Ancestor_merged_with_no_unique_commits_is_safe()
    {
        var branch = new BranchInfo("merged", DateTime.UtcNow, true, BranchMergeKind.Ancestor);
        var result = _sut.Classify(branch, new BranchRiskSignals(0, 0, true, true));
        Assert.Equal(BranchRiskLevel.Safe, result.Level);
    }

    [Fact]
    public void Squash_merged_with_no_unique_commits_is_safe_even_without_upstream()
    {
        var branch = new BranchInfo("squash", DateTime.UtcNow, false, BranchMergeKind.SquashOrRebase);
        var result = _sut.Classify(branch, new BranchRiskSignals(0, 2, false, true));
        Assert.Equal(BranchRiskLevel.Safe, result.Level);
    }

    [Fact]
    public void Unique_unpushed_commits_without_upstream_are_dangerous()
    {
        var branch = new BranchInfo("wip", DateTime.UtcNow, false);
        var result = _sut.Classify(branch, new BranchRiskSignals(3, 3, false, false));
        Assert.Equal(BranchRiskLevel.Dangerous, result.Level);
    }

    [Fact]
    public void Unique_partially_pushed_commits_are_dangerous()
    {
        var branch = new BranchInfo("wip", DateTime.UtcNow, false);
        var result = _sut.Classify(branch, new BranchRiskSignals(3, 1, true, false));
        Assert.Equal(BranchRiskLevel.Dangerous, result.Level);
    }

    [Fact]
    public void Unique_fully_pushed_but_unmerged_commits_need_review()
    {
        var branch = new BranchInfo("pushed", DateTime.UtcNow, false);
        var result = _sut.Classify(branch, new BranchRiskSignals(2, 0, true, false));
        Assert.Equal(BranchRiskLevel.Review, result.Level);
    }

    [Fact]
    public void No_unique_commits_without_real_merge_needs_review()
    {
        var branch = new BranchInfo("empty", DateTime.UtcNow, false);
        var result = _sut.Classify(branch, new BranchRiskSignals(0, 0, true, false));
        Assert.Equal(BranchRiskLevel.Review, result.Level);
    }

    [Fact]
    public void Merged_flag_with_unique_commits_needs_review()
    {
        var branch = new BranchInfo("partial", DateTime.UtcNow, false, BranchMergeKind.SquashOrRebase);
        var result = _sut.Classify(branch, new BranchRiskSignals(1, 0, true, true));
        Assert.Equal(BranchRiskLevel.Review, result.Level);
    }

    [Fact]
    public void Unknown_signals_never_are_safe()
    {
        var merged = new BranchInfo("merged", DateTime.UtcNow, true, BranchMergeKind.Ancestor);
        Assert.Equal(BranchRiskLevel.Review, _sut.Classify(merged, BranchRiskSignals.Unknown).Level);
        Assert.Equal(BranchRiskLevel.Review, _sut.Classify(merged, null).Level);
        Assert.Equal(BranchRiskLevel.Review,
            _sut.Classify(merged, new BranchRiskSignals(null, 0, true, true)).Level);
        Assert.Equal(BranchRiskLevel.Review,
            _sut.Classify(merged, new BranchRiskSignals(0, null, true, true)).Level);
    }

    [Fact]
    public void Classify_throws_when_branch_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Classify(null!, BranchRiskSignals.Unknown));
    }
}
