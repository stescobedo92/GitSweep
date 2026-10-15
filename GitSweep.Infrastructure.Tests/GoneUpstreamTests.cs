using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

public sealed class GoneUpstreamTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private string _repoPath = "";
    private string _remotePath = "";
    private readonly GitRepositoryService _sut = new();
    private readonly BranchAnalyzer _analyzer = new();
    private readonly BranchRiskClassifier _classifier = new();

    public async Task InitializeAsync()
    {
        _repoPath = Path.Combine(_root, "repo");
        _remotePath = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(_repoPath);
        await RunGitAsync(_root, "init", "--bare", _remotePath);
        try { await RunGitAsync(_repoPath, "init", "-b", "main"); }
        catch (InvalidOperationException) { await RunGitAsync(_repoPath, "init"); await RunGitAsync(_repoPath, "checkout", "-b", "main"); }
        await RunGitAsync(_repoPath, "config", "user.email", "tests@example.com");
        await RunGitAsync(_repoPath, "config", "user.name", "GitSweep Tests");
        await CommitFileAsync("README.md", "initial", "initial commit");
        await RunGitAsync(_repoPath, "remote", "add", "origin", _remotePath);
        await RunGitAsync(_repoPath, "push", "-u", "origin", "main");
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FetchPruneAsync_detects_merged_branch_as_safe_gone_candidate()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/gone-merged");
        await CommitFileAsync("gone.txt", "gone", "gone work");
        await RunGitAsync(_repoPath, "push", "-u", "origin", "feature/gone-merged");
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "merge", "feature/gone-merged", "--no-ff", "-m", "merge gone", "--no-gpg-sign");
        await RunGitAsync(_repoPath, "push", "origin", "main");
        // The branch is deleted on the remote itself (as after a merged PR);
        // the local remote-tracking ref still exists until fetch --prune.
        await RunGitAsync(_remotePath, "branch", "-D", "feature/gone-merged");

        var before = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        Assert.False(Assert.Single(before, b => b.Name == "feature/gone-merged").IsUpstreamGone);

        var fetch = await _sut.FetchPruneAsync(_repoPath);
        Assert.True(fetch.Success, fetch.ErrorMessage);

        var after = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(after, b => b.Name == "feature/gone-merged");
        Assert.True(branch.IsUpstreamGone);
        Assert.Equal("origin/feature/gone-merged", branch.UpstreamName);
        Assert.True(branch.IsSafeGoneCandidate);
        Assert.Contains(branch, _analyzer.IdentifySafeGoneBranches(after));

        var signals = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "feature/gone-merged");
        Assert.True(signals.IsUpstreamGone);
        Assert.Equal(0, signals.UniqueCommitCount);
        Assert.Equal(BranchRiskLevel.Safe, _classifier.Classify(branch, signals).Level);
    }

    [Fact]
    public async Task Gone_unmerged_branch_is_detected_but_never_safe()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/gone-unmerged");
        await CommitFileAsync("unmerged.txt", "unmerged", "unmerged work");
        await RunGitAsync(_repoPath, "push", "-u", "origin", "feature/gone-unmerged");
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_remotePath, "branch", "-D", "feature/gone-unmerged");

        var fetch = await _sut.FetchPruneAsync(_repoPath);
        Assert.True(fetch.Success, fetch.ErrorMessage);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/gone-unmerged");
        Assert.True(branch.IsUpstreamGone);
        Assert.False(branch.IsSafeGoneCandidate);
        Assert.Contains(branch, _analyzer.IdentifyGoneBranches(branches));
        Assert.DoesNotContain(branch, _analyzer.IdentifySafeGoneBranches(branches));

        var signals = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "feature/gone-unmerged");
        Assert.True(signals.IsUpstreamGone);
        Assert.Equal(1, signals.UniqueCommitCount);
        Assert.Equal(BranchRiskLevel.Dangerous, _classifier.Classify(branch, signals).Level);

        // Safe delete must still refuse this branch (no -D anywhere).
        var delete = await _sut.DeleteBranchAsync(_repoPath, "feature/gone-unmerged");
        Assert.False(delete.Success);
        Assert.True(await _sut.BranchExistsAsync(_repoPath, "feature/gone-unmerged"));
    }

    [Fact]
    public async Task FetchPruneAsync_fails_for_missing_path_and_unreachable_remote()
    {
        var missing = await _sut.FetchPruneAsync(Path.Combine(_root, "nope"));
        Assert.False(missing.Success);

        // A repository whose only remote points at a missing local path must
        // fail (fail-closed) instead of reporting a successful prune.
        var broken = Path.Combine(_root, "broken");
        Directory.CreateDirectory(broken);
        try { await RunGitAsync(broken, "init", "-b", "main"); }
        catch (InvalidOperationException) { await RunGitAsync(broken, "init"); }
        await RunGitAsync(broken, "remote", "add", "origin", Path.Combine(_root, "missing-remote.git"));
        var unreachable = await _sut.FetchPruneAsync(broken);
        Assert.False(unreachable.Success);
    }

    [Fact]
    public async Task Branch_without_upstream_is_not_gone()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/local-only");
        await CommitFileAsync("local.txt", "local", "local work");
        await RunGitAsync(_repoPath, "checkout", "main");

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/local-only");
        Assert.Null(branch.UpstreamName);
        Assert.False(branch.IsUpstreamGone);
    }

    private async Task CommitFileAsync(string fileName, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, fileName), content);
        await RunGitAsync(_repoPath, "add", ".");
        await RunGitAsync(_repoPath, "commit", "-m", message, "--no-gpg-sign");
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
        return stdout;
    }
}
