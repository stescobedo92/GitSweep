#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// V-01 / O-F1 (final-security-audit.md) and P3-T1 (final-coverage-review.md):
/// <see cref="GitRepositoryService.CreateBranchAsync"/>, used by restore, undo
/// and the post-delete recreation, must create the branch at exactly the
/// requested commit or report failure, and must never change an existing
/// branch.
/// </summary>
public sealed class RestoreStartPointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _sut = new();
    private string _repo = "";

    public async Task InitializeAsync()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        await GitAsync(_repo, "init", "-b", "main");
        await GitAsync(_repo, "config", "user.email", "tests@example.com");
        await GitAsync(_repo, "config", "user.name", "GitSweep Tests");
        await CommitFileAsync("README.md", "initial");
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateBranch_uses_the_saved_sha_even_when_a_tag_is_named_like_it()
    {
        // The user's saved commit S lives on a branch that was deleted; a tag
        // NAMED S (e.g. auto-followed from a hostile remote) points elsewhere.
        await GitAsync(_repo, "checkout", "-b", "feature/saved");
        await CommitFileAsync("saved.txt", "saved work");
        var saved = (await GitAsync(_repo, "rev-parse", "HEAD")).Trim();
        await GitAsync(_repo, "checkout", "main");
        await CommitFileAsync("other.txt", "attacker commit");
        var other = (await GitAsync(_repo, "rev-parse", "HEAD")).Trim();
        await GitAsync(_repo, "tag", saved, other);
        await GitAsync(_repo, "branch", "-D", "feature/saved");

        // Positive control: `git branch -- <name> <sha>` (the old restore
        // argv) resolves the SHA through the tag and creates the branch at the
        // other commit, while reporting success.
        await GitAsync(_repo, "branch", "--", "control", saved);
        Assert.Equal(other, (await GitAsync(_repo, "rev-parse", "refs/heads/control")).Trim());

        var (success, message) = await _sut.CreateBranchAsync(_repo, "feature/saved", saved);

        Assert.True(success, message);
        Assert.Equal(saved, await _sut.GetBranchTipShaAsync(_repo, "feature/saved"));
    }

    [Fact]
    public async Task CreateBranch_reports_failure_when_the_new_branch_does_not_point_at_the_requested_object()
    {
        // A trash file lives in the (untrusted) repository and can name any
        // object, e.g. an annotated tag: git peels it and creates the branch
        // at the tagged commit. The restore must not then count as done.
        await GitAsync(_repo, "tag", "-a", "v1", "-m", "release", "HEAD");
        var tagObject = (await GitAsync(_repo, "rev-parse", "refs/tags/v1")).Trim();
        var commit = (await GitAsync(_repo, "rev-parse", "HEAD")).Trim();
        Assert.NotEqual(commit, tagObject);

        var (success, message) = await _sut.CreateBranchAsync(_repo, "restored", tagObject);

        Assert.False(success);
        Assert.Contains("does not point at commit " + tagObject, message);
    }

    [Fact]
    public async Task CreateBranch_refuses_a_name_that_differs_only_in_case_from_an_existing_branch()
    {
        await GitAsync(_repo, "branch", "Feature");
        var existing = (await GitAsync(_repo, "rev-parse", "refs/heads/Feature")).Trim();
        await CommitFileAsync("newer.txt", "newer");
        var restoredSha = (await GitAsync(_repo, "rev-parse", "HEAD")).Trim();
        await GitAsync(_repo, "pack-refs", "--all");

        // Positive control, only meaningful where the file system ignores
        // case (Windows, macOS): creating `feature` next to a packed
        // `Feature` writes a loose ref that git then also reads as Feature,
        // so the existing branch silently moves.
        var control = Path.Combine(_root, "control");
        Directory.CreateDirectory(control);
        if (IsCaseInsensitive(control))
        {
            await GitAsync(_repo, "branch", "--", "feature", restoredSha);
            Assert.Equal(restoredSha, (await GitAsync(_repo, "rev-parse", "refs/heads/Feature")).Trim());
            await GitAsync(_repo, "update-ref", "-d", "refs/heads/feature");
            await GitAsync(_repo, "pack-refs", "--all");
            Assert.Equal(existing, (await GitAsync(_repo, "rev-parse", "refs/heads/Feature")).Trim());
        }

        var (success, message) = await _sut.CreateBranchAsync(_repo, "feature", restoredSha);

        Assert.False(success);
        Assert.Contains("only in letter case", message);
        Assert.Equal(existing, (await GitAsync(_repo, "rev-parse", "refs/heads/Feature")).Trim());
    }

    private static bool IsCaseInsensitive(string directory)
    {
        var probe = Path.Combine(directory, "CaseProbe");
        File.WriteAllText(probe, "probe");
        return File.Exists(Path.Combine(directory, "caseprobe"));
    }

    private async Task CommitFileAsync(string fileName, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, fileName), content);
        await GitAsync(_repo, "add", ".");
        await GitAsync(_repo, "commit", "-m", content, "--no-gpg-sign");
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutableLocator.GetRequiredPath(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
        }

        return stdout;
    }
}
