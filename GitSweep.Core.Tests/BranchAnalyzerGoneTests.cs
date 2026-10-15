using System;
using System.Collections.Generic;
using System.Linq;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Core.Tests;

public class BranchAnalyzerGoneTests
{
    private readonly BranchAnalyzer _sut = new();

    private static List<BranchInfo> Branches() =>
    [
        new("gone-merged", DateTime.UtcNow, true, BranchMergeKind.Ancestor, "origin/gone-merged", true),
        new("gone-squash", DateTime.UtcNow, false, BranchMergeKind.SquashOrRebase, "origin/gone-squash", true),
        new("gone-unmerged", DateTime.UtcNow, false, BranchMergeKind.NotMerged, "origin/gone-unmerged", true),
        new("merged-not-gone", DateTime.UtcNow, true, BranchMergeKind.Ancestor, "origin/merged-not-gone", false),
        new("no-upstream", DateTime.UtcNow, false)
    ];

    [Fact]
    public void IdentifyGoneBranches_returns_all_gone_regardless_of_merge()
    {
        var result = _sut.IdentifyGoneBranches(Branches()).Select(b => b.Name).ToList();

        Assert.Equal(["gone-merged", "gone-squash", "gone-unmerged"], result);
    }

    [Fact]
    public void IdentifySafeGoneBranches_returns_only_gone_and_effectively_merged()
    {
        var result = _sut.IdentifySafeGoneBranches(Branches()).Select(b => b.Name).ToList();

        Assert.Equal(["gone-merged", "gone-squash"], result);
        Assert.DoesNotContain("gone-unmerged", result);
    }

    [Fact]
    public void IdentifyGoneBranches_throws_when_null()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.IdentifyGoneBranches(null!).ToList());
        Assert.Throws<ArgumentNullException>(() => _sut.IdentifySafeGoneBranches(null!).ToList());
    }

    [Fact]
    public void Gone_classifier_safe_only_when_merged_and_no_unique_commits()
    {
        var classifier = new BranchRiskClassifier();
        var goneMerged = Branches()[0];
        var goneUnmerged = Branches()[2];

        var safe = classifier.Classify(goneMerged, new BranchRiskSignals(0, 0, false, true, true));
        var dangerous = classifier.Classify(goneUnmerged, new BranchRiskSignals(2, 2, false, false, true));
        var unknown = classifier.Classify(goneMerged, new BranchRiskSignals(null, null, false, true, true));

        Assert.Equal(BranchRiskLevel.Safe, safe.Level);
        Assert.Contains("Upstream is gone", safe.Reason);
        Assert.Equal(BranchRiskLevel.Dangerous, dangerous.Level);
        Assert.Equal(BranchRiskLevel.Review, unknown.Level);
    }
}
