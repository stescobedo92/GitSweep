using System;
using System.Collections.Generic;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Core.Tests;

public class BranchProtectionTests
{
    private readonly BranchProtectionService _sut = new();

    [Theory]
    [InlineData("release/1.0", "release/*", true)]
    [InlineData("RELEASE/1.0", "release/*", true)]
    [InlineData("main", "main", true)]
    [InlineData("main", "m?in", true)]
    [InlineData("feature/x", "feature/*", true)]
    [InlineData("feature/x", "*", true)]
    [InlineData("feature/x", "release/*", false)]
    [InlineData("domain", "main", false)]
    [InlineData("main-extra", "main", false)]
    public void GlobMatcher_matches_expected(string branch, string pattern, bool expected) =>
        Assert.Equal(expected, BranchGlobMatcher.IsMatch(branch, pattern));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a b")]
    [InlineData("a[bc]")]
    [InlineData("a\\b")]
    [InlineData("release/../x")]
    public void GlobMatcher_rejects_invalid_patterns(string pattern) =>
        Assert.False(BranchGlobMatcher.IsValidPattern(pattern));

    [Fact]
    public void Evaluate_protects_glob_match()
    {
        var config = new GitSweepConfig(["release/*", "hotfix/*"]);
        var result = _sut.Evaluate("release/2.0", config, "main", []);
        Assert.True(result.IsProtected);
        Assert.Equal(BranchProtectionReason.GlobPattern, result.Reason);
        Assert.Equal("release/*", result.MatchedPattern);
    }

    [Fact]
    public void Evaluate_protects_current_branch_even_without_glob()
    {
        var result = _sut.Evaluate("feature/wip", GitSweepConfig.Empty, "feature/wip", []);
        Assert.Equal(BranchProtectionReason.CurrentBranch, result.Reason);
    }

    [Fact]
    public void Evaluate_protects_worktree_branch()
    {
        var result = _sut.Evaluate("feature/wt", GitSweepConfig.Empty, "main", ["feature/wt"]);
        Assert.Equal(BranchProtectionReason.WorktreeBranch, result.Reason);
    }

    [Fact]
    public void Evaluate_respects_disabled_current_and_worktree_flags()
    {
        var config = new GitSweepConfig([], ExcludeCurrentBranch: false, ExcludeWorktreeBranches: false);
        Assert.False(_sut.Evaluate("feature/wip", config, "feature/wip", ["feature/wip"]).IsProtected);
    }

    [Fact]
    public void Evaluate_default_config_protects_main_and_master()
    {
        Assert.True(_sut.Evaluate("main", GitSweepConfig.Default, null, []).IsProtected);
        Assert.True(_sut.Evaluate("master", GitSweepConfig.Default, null, []).IsProtected);
        Assert.False(_sut.Evaluate("feature/x", GitSweepConfig.Default, null, []).IsProtected);
    }

    [Fact]
    public void FilterProtected_splits_branches()
    {
        var branches = new List<BranchInfo>
        {
            new("release/1", DateTime.UtcNow, true),
            new("feature/old", DateTime.UtcNow.AddMonths(-9), false),
            new("feature/wt", DateTime.UtcNow, true),
        };
        var config = new GitSweepConfig(["release/*"]);
        var protectedResults = _sut.FilterProtected(branches, config, "main", ["feature/wt"], out var unprotected);
        Assert.Equal(2, protectedResults.Count);
        Assert.Single(unprotected);
        Assert.Equal("feature/old", unprotected[0].Name);
    }

    [Fact]
    public void Evaluate_invalid_branch_name_is_protected_fail_closed()
    {
        Assert.True(_sut.Evaluate("-bad", GitSweepConfig.Empty, null, []).IsProtected);
        Assert.True(_sut.Evaluate("", GitSweepConfig.Empty, null, []).IsProtected);
    }
}
