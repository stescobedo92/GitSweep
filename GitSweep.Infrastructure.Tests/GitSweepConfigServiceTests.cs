using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

public sealed class GitSweepConfigServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GitSweepConfigTests", Guid.NewGuid().ToString("N"));
    private readonly GitSweepConfigService _sut = new();

    public GitSweepConfigServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<string> WriteConfigAsync(string content)
    {
        var path = Path.Combine(_dir, GitSweepConfig.FileName);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task LoadAsync_missing_file_returns_defaults_protecting_main_master()
    {
        var config = await _sut.LoadAsync(_dir);
        Assert.Contains("main", config.ProtectedBranches);
        Assert.Contains("master", config.ProtectedBranches);
        Assert.True(config.ExcludeCurrentBranch);
        Assert.True(config.ExcludeWorktreeBranches);
    }

    [Fact]
    public async Task LoadAsync_reads_globs_and_flags()
    {
        await WriteConfigAsync("""{"protectedBranches":["release/*","hotfix/*","main"],"excludeCurrentBranch":true,"excludeWorktreeBranches":false}""");
        var config = await _sut.LoadAsync(_dir);
        Assert.Equal(["release/*", "hotfix/*", "main"], config.ProtectedBranches);
        Assert.True(config.ExcludeCurrentBranch);
        Assert.False(config.ExcludeWorktreeBranches);
    }

    [Fact]
    public async Task LoadAsync_case_insensitive_property_names()
    {
        await WriteConfigAsync("""{"ProtectedBranches":["keep/*"]}""");
        var config = await _sut.LoadAsync(_dir);
        Assert.Equal(["keep/*"], config.ProtectedBranches);
    }

    [Fact]
    public async Task LoadAsync_empty_object_uses_flag_defaults_and_no_globs()
    {
        await WriteConfigAsync("""{}""");
        var config = await _sut.LoadAsync(_dir);
        Assert.Empty(config.ProtectedBranches);
        Assert.True(config.ExcludeCurrentBranch);
    }

    [Fact]
    public async Task LoadAsync_invalid_json_throws_fail_closed()
    {
        await WriteConfigAsync("{ not json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
    }

    [Fact]
    public async Task LoadAsync_yaml_content_is_rejected_not_parsed()
    {
        await WriteConfigAsync("protectedBranches:\n  - release/*\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
    }

    [Fact]
    public async Task LoadAsync_invalid_glob_throws()
    {
        await WriteConfigAsync("""{"protectedBranches":["bad pattern"]}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
    }

    [Fact]
    public async Task LoadAsync_too_many_patterns_throws()
    {
        var patterns = string.Join(",", Enumerable.Range(0, 101).Select(i => $"\"b{i}/*\""));
        await WriteConfigAsync($"{{\"protectedBranches\":[{patterns}]}}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
    }

    [Fact]
    public async Task LoadAsync_oversized_file_throws()
    {
        await WriteConfigAsync(new string(' ', 64 * 1024 + 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
    }

    [Fact]
    public async Task GetConfigFilePathAsync_points_to_gitsweep_json()
    {
        var path = await _sut.GetConfigFilePathAsync(_dir);
        Assert.EndsWith(GitSweepConfig.FileName, path);
    }
}
