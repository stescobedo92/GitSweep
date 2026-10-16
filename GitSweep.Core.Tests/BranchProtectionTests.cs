using System;
using System.Collections.Generic;
using System.Linq;
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
    public void BuiltInProtectedBranches_are_exactly_main_and_master() =>
        Assert.Equal(["main", "master"], GitSweepConfig.BuiltInProtectedBranches);

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("MAIN")]
    [InlineData("Master")]
    public void Evaluate_empty_config_still_protects_builtin_main_and_master(string branch)
    {
        var result = _sut.Evaluate(branch, GitSweepConfig.Empty, null, []);
        Assert.True(result.IsProtected);
        Assert.Equal(BranchProtectionReason.GlobPattern, result.Reason);
    }

    [Fact]
    public void Evaluate_test_constructed_config_without_patterns_still_protects_builtins()
    {
        var config = new GitSweepConfig([], ExcludeCurrentBranch: false, ExcludeWorktreeBranches: false);
        Assert.True(_sut.Evaluate("main", config, "feature/x", ["feature/x"]).IsProtected);
        Assert.True(_sut.Evaluate("master", config, "feature/x", ["feature/x"]).IsProtected);
    }

    [Fact]
    public void Evaluate_null_pattern_list_still_protects_builtins_fail_closed()
    {
        var config = new GitSweepConfig(null!);
        Assert.True(_sut.Evaluate("main", config, null, []).IsProtected);
        Assert.True(_sut.Evaluate("master", config, null, []).IsProtected);
        Assert.False(_sut.Evaluate("feature/x", config, null, []).IsProtected);
    }

    [Fact]
    public void Evaluate_configured_patterns_are_unioned_with_builtins()
    {
        var config = new GitSweepConfig(["release/*"]);
        Assert.True(_sut.Evaluate("main", config, null, []).IsProtected);
        Assert.True(_sut.Evaluate("master", config, null, []).IsProtected);
        var release = _sut.Evaluate("release/x", config, null, []);
        Assert.True(release.IsProtected);
        Assert.Equal("release/*", release.MatchedPattern);
        Assert.False(_sut.Evaluate("feature/x", config, null, []).IsProtected);
    }

    [Theory]
    [InlineData("main-extra")]
    [InlineData("domain")]
    [InlineData("mainline")]
    [InlineData("feature/main")]
    [InlineData("master2")]
    public void Evaluate_builtins_match_whole_name_only(string branch) =>
        Assert.False(_sut.Evaluate(branch, GitSweepConfig.Empty, null, []).IsProtected);

    [Fact]
    public void FilterProtected_with_empty_config_never_leaves_main_or_master_unprotected()
    {
        var branches = new List<BranchInfo>
        {
            new("main", DateTime.UtcNow, true),
            new("master", DateTime.UtcNow, true),
            new("feature/old", DateTime.UtcNow.AddMonths(-9), false),
        };
        var protectedResults = _sut.FilterProtected(branches, GitSweepConfig.Empty, null, [], out var unprotected);
        Assert.Equal(["main", "master"], protectedResults.Select(r => r.BranchName).ToList());
        Assert.Equal(["feature/old"], unprotected.Select(b => b.Name).ToList());
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

    // P5-T2 (final-coverage-review.md): a valid pattern can still backtrack for
    // far longer than the matcher's 250 ms timeout. The matcher must throw
    // (no filtered catch in clean takes it, so clean stops before any delete)
    // instead of answering "not protected".
    [Fact]
    public void GlobMatcher_backtracking_pattern_hits_the_timeout_and_throws_instead_of_answering()
    {
        var pattern = string.Concat(Enumerable.Repeat("*a", 14)) + "*b";
        Assert.True(BranchGlobMatcher.IsValidPattern(pattern));
        var branch = new string('a', 100);

        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => BranchGlobMatcher.IsMatch(branch, pattern));
        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(
            () => _sut.Evaluate(branch, new GitSweepConfig([pattern]), null, []));
    }
}
