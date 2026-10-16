using System;
using System.IO;
using System.Linq;
using System.Text;
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
        Assert.Empty(config.ProtectedBranches);
        Assert.True(config.ExcludeCurrentBranch);
        Assert.True(config.ExcludeWorktreeBranches);
        AssertBuiltInsProtectedAndFeatureNot(config);
    }

    private static void AssertBuiltInsProtectedAndFeatureNot(GitSweepConfig config)
    {
        var protection = new BranchProtectionService();
        Assert.True(protection.Evaluate("main", config, null, []).IsProtected);
        Assert.True(protection.Evaluate("master", config, null, []).IsProtected);
        Assert.False(protection.Evaluate("feature/x", config, null, []).IsProtected);
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
    public async Task LoadAsync_empty_object_uses_flag_defaults_and_no_extra_globs_but_keeps_builtins()
    {
        await WriteConfigAsync("""{}""");
        var config = await _sut.LoadAsync(_dir);
        Assert.Empty(config.ProtectedBranches);
        Assert.True(config.ExcludeCurrentBranch);
        Assert.True(config.ExcludeWorktreeBranches);
        AssertBuiltInsProtectedAndFeatureNot(config);
    }

    [Theory]
    [InlineData("""{"protectedBranches":null}""")]
    [InlineData("""{"protectedBranches":[]}""")]
    [InlineData("""{"excludeCurrentBranch":true}""")]
    public async Task LoadAsync_missing_null_or_empty_protectedBranches_means_no_extra_patterns(string json)
    {
        await WriteConfigAsync(json);
        var config = await _sut.LoadAsync(_dir);
        Assert.Empty(config.ProtectedBranches);
        AssertBuiltInsProtectedAndFeatureNot(config);
    }

    [Fact]
    public async Task LoadAsync_configured_patterns_add_to_builtins_never_replace_them()
    {
        await WriteConfigAsync("""{"protectedBranches":["release/*"]}""");
        var config = await _sut.LoadAsync(_dir);
        Assert.Equal(["release/*"], config.ProtectedBranches);

        var protection = new BranchProtectionService();
        Assert.True(protection.Evaluate("main", config, null, []).IsProtected);
        Assert.True(protection.Evaluate("master", config, null, []).IsProtected);
        Assert.True(protection.Evaluate("release/x", config, null, []).IsProtected);
        Assert.False(protection.Evaluate("feature/x", config, null, []).IsProtected);
    }

    [Theory]
    [InlineData("""{"protected_branches":["release/*"]}""")]
    [InlineData("""{"protectedBranch":["release/*"]}""")]
    [InlineData("""{"protectedBranches":["release/*"],"unknownSetting":true}""")]
    [InlineData("""{"protectedBranches":["release/*"],"extra":{"nested":[1,2,3]}}""")]
    public async Task LoadAsync_unknown_or_misspelled_property_throws_fail_closed(string json)
    {
        await WriteConfigAsync(json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
    }

    [Theory]
    [InlineData("""{"protectedBranches":["release/*"],"protectedBranches":[]}""")]
    [InlineData("""{"protectedBranches":["release/*"],"ProtectedBranches":[]}""")]
    [InlineData("""{"excludeCurrentBranch":true,"excludeCurrentBranch":false}""")]
    public async Task LoadAsync_duplicate_property_throws_fail_closed(string json)
    {
        await WriteConfigAsync(json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
    }

    [Fact]
    public async Task LoadAsync_strict_parse_errors_do_not_echo_file_content()
    {
        const string secret = "hunter2-super-secret-token";
        await WriteConfigAsync($$"""{"protectedBranches":["release/*"],"{{secret}}":"{{secret}}"}""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
        Assert.DoesNotContain(secret, ex.Message);
        Assert.DoesNotContain("release", ex.Message);
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
    public async Task LoadAsync_empty_file_throws_fail_closed_as_empty()
    {
        await WriteConfigAsync(string.Empty);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));
        Assert.Contains("is empty", ex.Message);
    }

    [Fact]
    public async Task LoadAsync_file_of_exactly_the_size_cap_is_accepted()
    {
        // The bounded read holds cap + 1 bytes to detect an oversized file;
        // a file of exactly the cap must still load (no off-by-one).
        const string body = """{"protectedBranches":["release/*"]}""";
        await WriteConfigAsync(body + new string(' ', 64 * 1024 - body.Length));
        Assert.Equal(64 * 1024, new FileInfo(Path.Combine(_dir, GitSweepConfig.FileName)).Length);

        var config = await _sut.LoadAsync(_dir);

        Assert.Equal(["release/*"], config.ProtectedBranches);
    }

    [Fact]
    public async Task LoadAsync_accepts_utf8_byte_order_mark()
    {
        // Windows editors save JSON with a BOM; reading through the bounded
        // buffer must keep accepting it.
        var path = Path.Combine(_dir, GitSweepConfig.FileName);
        await File.WriteAllTextAsync(path, """{"protectedBranches":["keep/*"]}""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, (await File.ReadAllBytesAsync(path))[0]);

        var config = await _sut.LoadAsync(_dir);

        Assert.Equal(["keep/*"], config.ProtectedBranches);
    }

    [FileSymlinkFact]
    public async Task LoadAsync_symlinked_config_throws_and_is_not_followed()
    {
        // The link points at a perfectly valid config: if it were followed,
        // LoadAsync would succeed. It must refuse the link itself, which is
        // also what keeps the size cap meaningful (the cap is checked on the
        // file that is read, not on the link).
        var target = Path.Combine(_dir, "real-config.json");
        await File.WriteAllTextAsync(target, """{"protectedBranches":["release/*"]}""");
        TestLinks.CreateFileSymlink(Path.Combine(_dir, GitSweepConfig.FileName), target);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir));

        Assert.Contains("symbolic link", ex.Message);
    }

    [FifoFact]
    public async Task LoadAsync_fifo_config_fails_closed_instead_of_hanging()
    {
        // A FIFO (shippable in a tar archive) reports length 0 and blocks
        // open(2) until a writer appears.
        var path = Path.Combine(_dir, GitSweepConfig.FileName);
        TestFifo.Create(path);

        var ex = await TestFifo.CompleteWithinTimeoutAsync(path, async () =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.LoadAsync(_dir)));

        Assert.Contains("is empty", ex.Message);
    }

    [Fact]
    public async Task GetConfigFilePathAsync_points_to_gitsweep_json()
    {
        var path = await _sut.GetConfigFilePathAsync(_dir);
        Assert.EndsWith(GitSweepConfig.FileName, path);
    }
}
