using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

public sealed class BranchRiskSignalsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private string _repoPath = "";
    private string _remotePath = "";
    private readonly GitRepositoryService _sut = new();
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
    public async Task Ancestor_merged_branch_is_safe()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/merged");
        await CommitFileAsync("merged.txt", "merged", "merged work");
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "merge", "feature/merged", "--no-ff", "-m", "merge", "--no-gpg-sign");

        var signals = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "feature/merged");
        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/merged");

        Assert.True(signals.IsReallyMerged);
        Assert.Equal(0, signals.UniqueCommitCount);
        Assert.Equal(BranchRiskLevel.Safe, _classifier.Classify(branch, signals).Level);
    }

    [Fact]
    public async Task Squash_merged_branch_is_safe()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/squash");
        await CommitFileAsync("squash.txt", "squash", "squash work");
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "merge", "--squash", "feature/squash");
        await RunGitAsync(_repoPath, "commit", "-m", "squash merge", "--no-gpg-sign");

        var signals = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "feature/squash");
        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/squash");

        Assert.True(signals.IsReallyMerged);
        Assert.Equal(0, signals.UniqueCommitCount);
        Assert.Equal(BranchRiskLevel.Safe, _classifier.Classify(branch, signals).Level);
    }

    [Fact]
    public async Task Unmerged_branch_without_upstream_is_dangerous()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/local");
        await CommitFileAsync("local.txt", "local", "local work");
        await RunGitAsync(_repoPath, "checkout", "main");

        var signals = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "feature/local");

        Assert.False(signals.IsReallyMerged);
        Assert.False(signals.HasUpstream);
        Assert.Equal(1, signals.UniqueCommitCount);
        Assert.Equal(1, signals.UnpushedCommitCount);
        var branch = new BranchInfo("feature/local", DateTime.UtcNow, false);
        Assert.Equal(BranchRiskLevel.Dangerous, _classifier.Classify(branch, signals).Level);
    }

    [Fact]
    public async Task Pushed_unmerged_branch_needs_review_until_it_gets_unpushed_commits()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/pushed");
        await CommitFileAsync("pushed.txt", "pushed", "pushed work");
        await RunGitAsync(_repoPath, "push", "-u", "origin", "feature/pushed");
        await RunGitAsync(_repoPath, "checkout", "main");
        var branch = new BranchInfo("feature/pushed", DateTime.UtcNow, false);

        var pushed = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "feature/pushed");
        Assert.True(pushed.HasUpstream);
        Assert.Equal(1, pushed.UniqueCommitCount);
        Assert.Equal(0, pushed.UnpushedCommitCount);
        Assert.Equal(BranchRiskLevel.Review, _classifier.Classify(branch, pushed).Level);

        await RunGitAsync(_repoPath, "checkout", "feature/pushed");
        await CommitFileAsync("extra.txt", "extra", "unpushed work");
        await RunGitAsync(_repoPath, "checkout", "main");

        var unpushed = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "feature/pushed");
        Assert.Equal(1, unpushed.UnpushedCommitCount);
        Assert.Equal(BranchRiskLevel.Dangerous, _classifier.Classify(branch, unpushed).Level);
    }

    [Fact]
    public async Task Invalid_inputs_return_unknown_signals()
    {
        var invalid = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "--force");
        var same = await _sut.GetBranchRiskSignalsAsync(_repoPath, "main", "main");
        var missingPath = await _sut.GetBranchRiskSignalsAsync(Path.Combine(_root, "nope"), "main", "feature/x");

        Assert.Equal(BranchRiskSignals.Unknown, invalid);
        Assert.Equal(BranchRiskSignals.Unknown, same);
        Assert.Equal(BranchRiskSignals.Unknown, missingPath);
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
