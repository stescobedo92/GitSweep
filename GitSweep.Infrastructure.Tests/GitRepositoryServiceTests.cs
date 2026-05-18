using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

public sealed class GitRepositoryServiceTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _sut = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoPath);

        await InitRepositoryAsync();
        await ConfigureUserAsync();

        // initial commit on main
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "README.md"), "initial");
        await RunGitAsync("add .");
        await RunGitAsync("commit -m \"initial commit\" --no-gpg-sign");

        // branch merged into main
        await RunGitAsync("checkout -b feature/merged");
        await File.AppendAllTextAsync(Path.Combine(_repoPath, "README.md"), " merged");
        await RunGitAsync("add .");
        await RunGitAsync("commit -m \"merged branch\" --no-gpg-sign");
        await RunGitAsync("checkout main");
        await RunGitAsync("merge feature/merged --no-ff -m \"merge feature/merged\" --no-gpg-sign");

        // active branch not merged
        await RunGitAsync("checkout -b feature/active");
        await File.AppendAllTextAsync(Path.Combine(_repoPath, "README.md"), " active");
        await RunGitAsync("add .");
        await RunGitAsync("commit -m \"active branch\" --no-gpg-sign");
        await RunGitAsync("checkout main");
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_repoPath))
            {
                Directory.Delete(_repoPath, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup errors in tests
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task IsGitRepositoryAsync_returns_true_for_git_repo()
    {
        var result = await _sut.IsGitRepositoryAsync(_repoPath);
        Assert.True(result);
    }

    [Fact]
    public async Task IsGitRepositoryAsync_returns_false_for_non_repo()
    {
        var path = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        var result = await _sut.IsGitRepositoryAsync(path);
        Assert.False(result);
    }

    [Fact]
    public async Task IsGitRepositoryAsync_returns_false_for_missing_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));

        var result = await _sut.IsGitRepositoryAsync(path);

        Assert.False(result);
    }

    [Fact]
    public async Task GetDefaultBranchNameAsync_returns_main()
    {
        var result = await _sut.GetDefaultBranchNameAsync(_repoPath);
        Assert.Equal("main", result);
    }

    [Fact]
    public async Task GetLocalBranchesAsync_marks_merged_branches()
    {
        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branchLookup = branches.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);

        Assert.True(branchLookup.ContainsKey("feature/merged"));
        Assert.True(branchLookup["feature/merged"].IsMerged);

        Assert.True(branchLookup.ContainsKey("feature/active"));
        Assert.False(branchLookup["feature/active"].IsMerged);

        Assert.DoesNotContain(branches, b => string.Equals(b.Name, "main", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteBranchAsync_deletes_merged_branch()
    {
        var result = await _sut.DeleteBranchAsync(_repoPath, "feature/merged");

        Assert.True(result.Success, result.ErrorMessage);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        Assert.DoesNotContain(branches, b => string.Equals(b.Name, "feature/merged", StringComparison.OrdinalIgnoreCase));
    }

    private async Task InitRepositoryAsync()
    {
        try
        {
            await RunGitAsync("init -b main");
        }
        catch (InvalidOperationException)
        {
            await RunGitAsync("init");
            await RunGitAsync("checkout -b main");
        }
    }

    private async Task ConfigureUserAsync()
    {
        await RunGitAsync("config user.email tests@example.com");
        await RunGitAsync("config user.name GitSweep Tests");
    }

    private async Task RunGitAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {stderr}{stdout}");
        }
    }
}
