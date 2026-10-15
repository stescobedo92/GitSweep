using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

public sealed class GitRepositoryServiceProtectionTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly string _worktreePath = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N") + "-wt");
    private readonly GitRepositoryService _sut = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoPath);
        await RunGitAsync(_repoPath, "init -b main");
        await RunGitAsync(_repoPath, "config user.email tests@example.com");
        await RunGitAsync(_repoPath, "config user.name GitSweep Tests");
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "README.md"), "initial");
        await RunGitAsync(_repoPath, "add .");
        await RunGitAsync(_repoPath, "commit -m \"initial\" --no-gpg-sign");
        await RunGitAsync(_repoPath, "checkout -b feature/merged");
        await File.AppendAllTextAsync(Path.Combine(_repoPath, "README.md"), " merged");
        await RunGitAsync(_repoPath, "add .");
        await RunGitAsync(_repoPath, "commit -m \"merged\" --no-gpg-sign");
        await RunGitAsync(_repoPath, "checkout main");
        await RunGitAsync(_repoPath, "merge feature/merged --no-ff -m \"merge\" --no-gpg-sign");
        await RunGitAsync(_repoPath, "checkout -b feature/worktree");
        await RunGitAsync(_repoPath, "checkout main");
        await RunGitAsync(_repoPath, $"worktree add \"{_worktreePath}\" feature/worktree");
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_worktreePath)) Directory.Delete(_worktreePath, true); } catch { }
        try { if (Directory.Exists(_repoPath)) Directory.Delete(_repoPath, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetCurrentBranchAsync_returns_checked_out_branch()
    {
        Assert.Equal("main", await _sut.GetCurrentBranchAsync(_repoPath));
        Assert.Equal("feature/worktree", await _sut.GetCurrentBranchAsync(_worktreePath));
    }

    [Fact]
    public async Task GetCurrentBranchAsync_returns_null_for_non_repo()
    {
        // Ruta inexistente: sin directorio no se invoca git (un directorio
        // vacío dentro de otro repo devolvería la rama de ese repo padre).
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Null(await _sut.GetCurrentBranchAsync(dir));
    }

    [Fact]
    public async Task GetWorktreeBranchesAsync_includes_main_and_worktree_branches()
    {
        var branches = await _sut.GetWorktreeBranchesAsync(_repoPath);
        Assert.Contains("main", branches, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("feature/worktree", branches, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRepositoryRootAsync_returns_repo_root()
    {
        var root = await _sut.GetRepositoryRootAsync(_repoPath);
        Assert.NotNull(root);
        Assert.Equal(Path.GetFullPath(_repoPath), Path.GetFullPath(root!), ignoreCase: true);
    }

    [Fact]
    public async Task GetLocalBranchesAsync_excludes_current_and_worktree_branches()
    {
        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        Assert.DoesNotContain(branches, b => string.Equals(b.Name, "feature/worktree", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(branches, b => string.Equals(b.Name, "feature/merged", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteBranchAsync_refuses_current_branch()
    {
        var result = await _sut.DeleteBranchAsync(_repoPath, "main");
        Assert.False(result.Success);
        Assert.Contains("current branch", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteBranchAsync_refuses_worktree_branch()
    {
        var result = await _sut.DeleteBranchAsync(_repoPath, "feature/worktree");
        Assert.False(result.Success);
        Assert.Contains("worktree", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(await _sut.BranchExistsAsync(_repoPath, "feature/worktree"));
    }

    [Fact]
    public async Task DeleteBranchAsync_still_deletes_merged_unprotected_branch()
    {
        var result = await _sut.DeleteBranchAsync(_repoPath, "feature/merged");
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task DeleteBranchAsync_never_force_deletes_unmerged_branch()
    {
        await RunGitAsync(_repoPath, "checkout -b feature/unmerged");
        await File.AppendAllTextAsync(Path.Combine(_repoPath, "README.md"), " unique");
        await RunGitAsync(_repoPath, "add .");
        await RunGitAsync(_repoPath, "commit -m \"unique\" --no-gpg-sign");
        await RunGitAsync(_repoPath, "checkout main");
        var result = await _sut.DeleteBranchAsync(_repoPath, "feature/unmerged");
        Assert.False(result.Success);
        Assert.True(await _sut.BranchExistsAsync(_repoPath, "feature/unmerged"));
    }

    private static async Task RunGitAsync(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git", Arguments = arguments, WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {stderr}{stdout}");
    }
}
