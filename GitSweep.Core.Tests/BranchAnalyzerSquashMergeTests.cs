using System;
using System.Collections.Generic;
using System.Linq;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Core.Tests;

public class BranchAnalyzerSquashMergeTests
{
    private readonly BranchAnalyzer _sut = new();

    [Fact]
    public void IdentifyMergedBranches_includes_squash_or_rebase_merged()
    {
        var branches = new List<BranchInfo>
        {
            new("ancestor", DateTime.UtcNow, true, BranchMergeKind.Ancestor),
            new("squash", DateTime.UtcNow, false, BranchMergeKind.SquashOrRebase),
            new("active", DateTime.UtcNow, false, BranchMergeKind.NotMerged)
        };

        var result = _sut.IdentifyMergedBranches(branches).Select(b => b.Name).ToList();

        Assert.Contains("ancestor", result);
        Assert.Contains("squash", result);
        Assert.DoesNotContain("active", result);
    }

    [Fact]
    public void IdentifyStaleBranches_excludes_squash_or_rebase_merged()
    {
        var branches = new List<BranchInfo>
        {
            new("old-squash", DateTime.UtcNow.AddMonths(-9), false, BranchMergeKind.SquashOrRebase),
            new("old-stale", DateTime.UtcNow.AddMonths(-9), false, BranchMergeKind.NotMerged)
        };

        var result = _sut.IdentifyStaleBranches(branches, 6).Select(b => b.Name).ToList();

        Assert.Contains("old-stale", result);
        Assert.DoesNotContain("old-squash", result);
    }

    [Fact]
    public void BranchInfo_effective_merge_flags_distinguish_merge_kinds()
    {
        var ancestor = new BranchInfo("a", DateTime.UtcNow, true, BranchMergeKind.Ancestor);
        var squash = new BranchInfo("s", DateTime.UtcNow, false, BranchMergeKind.SquashOrRebase);
        var active = new BranchInfo("x", DateTime.UtcNow, false);

        Assert.True(ancestor.IsEffectivelyMerged);
        Assert.False(ancestor.IsSquashOrRebaseMerged);
        Assert.True(squash.IsEffectivelyMerged);
        Assert.True(squash.IsSquashOrRebaseMerged);
        Assert.False(active.IsEffectivelyMerged);
    }
}
