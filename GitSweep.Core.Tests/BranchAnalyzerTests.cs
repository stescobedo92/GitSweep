using System;
using System.Collections.Generic;
using System.Linq;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Core.Tests;

public class BranchAnalyzerTests
{
    private readonly BranchAnalyzer _sut = new();

    [Fact]
    public void IdentifyMergedBranches_returns_only_merged()
    {
        var branches = new List<BranchInfo>
        {
            new("merged", DateTime.UtcNow, true),
            new("active", DateTime.UtcNow, false)
        };

        var result = _sut.IdentifyMergedBranches(branches).ToList();

        Assert.Single(result);
        Assert.Equal("merged", result[0].Name);
    }

    [Fact]
    public void IdentifyMergedBranches_throws_when_branches_null()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.IdentifyMergedBranches(null!));
    }

    [Fact]
    public void IdentifyStaleBranches_filters_by_age_and_merge_state()
    {
        var now = DateTime.UtcNow;
        var branches = new List<BranchInfo>
        {
            new("stale", now.AddMonths(-7), false),
            new("recent", now.AddMonths(-1), false),
            new("merged-stale", now.AddMonths(-8), true),
            new("unknown-date", null, false)
        };

        var result = _sut.IdentifyStaleBranches(branches, 6).Select(b => b.Name).ToList();

        Assert.Contains("stale", result);
        Assert.DoesNotContain("recent", result);
        Assert.DoesNotContain("merged-stale", result);
        Assert.DoesNotContain("unknown-date", result);
    }

    [Fact]
    public void IdentifyStaleBranches_throws_for_negative_age()
    {
        var branches = new List<BranchInfo>();

        Assert.Throws<ArgumentOutOfRangeException>(() => _sut.IdentifyStaleBranches(branches, -1).ToList());
    }

    [Fact]
    public void IdentifyStaleBranches_throws_when_branches_null()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.IdentifyStaleBranches(null!, 6).ToList());
    }
}
