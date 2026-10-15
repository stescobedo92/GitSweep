using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

public sealed class SquashMergeDetectionTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _sut = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoPath);

        try
        {
            await RunGitAsync("init", "-b", "main");
        }
        catch (InvalidOperationException)
        {
            await RunGitAsync("init");
            await RunGitAsync("checkout", "-b", "main");
        }

        await RunGitAsync("config", "user.email", "tests@example.com");
        await RunGitAsync("config", "user.name", "GitSweep Tests");

        await CommitFileAsync("README.md", "initial", "initial commit");
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
    public async Task Squash_merged_branch_is_detected_even_though_not_in_merged_list()
    {
        await RunGitAsync("checkout", "-b", "feature/squash");
        await CommitFileAsync("squash.txt", "one", "squash part one");
        await AppendFileAsync("squash.txt", "two", "squash part two");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("merge", "--squash", "feature/squash");
        await RunGitAsync("commit", "-m", "squash merge feature/squash", "--no-gpg-sign");

        // Precondition: plain ancestor detection cannot see this merge.
        var mergedList = await RunGitAsync("branch", "--merged", "main");
        Assert.DoesNotContain("feature/squash", mergedList);

        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/squash");
        Assert.True(detected);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/squash");
        Assert.False(branch.IsMerged);
        Assert.Equal(BranchMergeKind.SquashOrRebase, branch.MergeKind);
        Assert.True(branch.IsSquashOrRebaseMerged);
        Assert.True(branch.IsEffectivelyMerged);
    }

    [Fact]
    public async Task Cherry_picked_branch_is_detected_as_rebase_merged()
    {
        await RunGitAsync("checkout", "-b", "feature/rebased");
        await CommitFileAsync("rebased.txt", "rebased work", "rebased work");
        await RunGitAsync("checkout", "main");
        // Advance main first so the cherry-picked commit gets a new parent
        // (and therefore a new SHA), exactly like a rebase merge would.
        await CommitFileAsync("main.txt", "main advance", "main advance");
        await RunGitAsync("cherry-pick", "feature/rebased");

        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/rebased");
        Assert.True(detected);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/rebased");
        Assert.Equal(BranchMergeKind.SquashOrRebase, branch.MergeKind);
        Assert.True(branch.IsEffectivelyMerged);
    }

    [Fact]
    public async Task Branch_with_unique_changes_is_not_detected()
    {
        await RunGitAsync("checkout", "-b", "feature/active");
        await CommitFileAsync("active.txt", "active work", "active work");
        await RunGitAsync("checkout", "main");

        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/active");
        Assert.False(detected);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/active");
        Assert.Equal(BranchMergeKind.NotMerged, branch.MergeKind);
        Assert.False(branch.IsEffectivelyMerged);
    }

    [Fact]
    public async Task Branch_with_extra_commit_after_squash_is_not_detected()
    {
        await RunGitAsync("checkout", "-b", "feature/partial");
        await CommitFileAsync("partial.txt", "part one", "part one");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("merge", "--squash", "feature/partial");
        await RunGitAsync("commit", "-m", "squash merge feature/partial", "--no-gpg-sign");

        await RunGitAsync("checkout", "feature/partial");
        await CommitFileAsync("extra.txt", "not merged yet", "extra work after squash");
        await RunGitAsync("checkout", "main");

        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/partial");
        Assert.False(detected);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/partial");
        Assert.Equal(BranchMergeKind.NotMerged, branch.MergeKind);
    }

    [Fact]
    public async Task Ancestor_merged_branch_is_ancestor_not_squash()
    {
        await RunGitAsync("checkout", "-b", "feature/merged");
        await CommitFileAsync("merged.txt", "merged work", "merged work");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("merge", "feature/merged", "--no-ff", "-m", "merge feature/merged", "--no-gpg-sign");

        // Ancestor merges belong to `git branch --merged`, not to squash detection.
        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/merged");
        Assert.False(detected);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/merged");
        Assert.True(branch.IsMerged);
        Assert.Equal(BranchMergeKind.Ancestor, branch.MergeKind);
        Assert.True(branch.IsEffectivelyMerged);
    }

    [Fact]
    public async Task Invalid_branch_names_are_rejected()
    {
        Assert.False(await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "--force"));
        Assert.False(await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "-x", "feature/x"));
        Assert.False(await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/../secret"));
        Assert.False(await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature x"));
        Assert.False(await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", ""));
        Assert.False(await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "main"));

        var delete = await _sut.DeleteBranchAsync(_repoPath, "--force");
        Assert.False(delete.Success);
    }

    [Fact]
    public async Task Tag_sharing_branch_name_does_not_fabricate_a_merge()
    {
        // A tag named like the branch resolves BEFORE refs/heads/<name> in
        // gitrevisions order. Point the tag at a commit whose patch already
        // exists on main: resolving the bare name would see that commit and
        // wrongly report the branch as merged even though the branch itself
        // carries unique work.
        await RunGitAsync("checkout", "-b", "side");
        await CommitFileAsync("side.txt", "side work", "side work");
        await RunGitAsync("tag", "amb");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("checkout", "-b", "amb");
        await CommitFileAsync("amb.txt", "unique amb work", "unique amb work");
        await RunGitAsync("checkout", "main");
        // Advance main first so the cherry-picked copy gets a new SHA (the
        // tag target itself must stay outside main's ancestry).
        await CommitFileAsync("main.txt", "main advance", "main advance");
        await RunGitAsync("cherry-pick", "side");

        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "amb");
        Assert.False(detected);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "amb");
        Assert.Equal(BranchMergeKind.NotMerged, branch.MergeKind);
        Assert.False(branch.IsEffectivelyMerged);
    }

    [Fact]
    public async Task Squash_detection_works_with_hostile_signing_config()
    {
        await RunGitAsync("checkout", "-b", "feature/squash");
        await CommitFileAsync("squash.txt", "squashed", "squashed work");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("merge", "--squash", "feature/squash");
        await RunGitAsync("commit", "-m", "squash merge feature/squash", "--no-gpg-sign");

        // A cloned repository must not be able to make the scan invoke a
        // repo-configured signing program (or fail) via commit.gpgsign.
        await RunGitAsync("config", "commit.gpgsign", "true");
        await RunGitAsync("config", "gpg.program", "nonexistent-gitsweep-test-program");

        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/squash");
        Assert.True(detected);
    }

    [Fact]
    public async Task Squash_detection_does_not_write_objects_into_the_repository()
    {
        await RunGitAsync("checkout", "-b", "feature/squash");
        await CommitFileAsync("squash.txt", "squashed", "squashed work");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("merge", "--squash", "feature/squash");
        await RunGitAsync("commit", "-m", "squash merge feature/squash", "--no-gpg-sign");

        var looseBefore = await CountLooseObjectsAsync();

        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/squash");

        Assert.True(detected);
        // The synthetic squash commit must live in a throwaway quarantine
        // object directory: a scan must never mutate the scanned repository.
        Assert.Equal(looseBefore, await CountLooseObjectsAsync());
    }

    [Fact]
    public async Task Injected_git_config_environment_does_not_apply()
    {
        await RunGitAsync("checkout", "-b", "feature/squash");
        await CommitFileAsync("squash.txt", "squashed", "squashed work");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("merge", "--squash", "feature/squash");
        await RunGitAsync("commit", "-m", "squash merge feature/squash", "--no-gpg-sign");

        // GIT_CONFIG_* variables inject config at command-line precedence
        // into every git child process. The service must strip them, so this
        // hostile signing setup can neither break nor hijack the scan.
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "2");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "commit.gpgsign");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "true");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", "gpg.program");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", "nonexistent-gitsweep-test-program");
        try
        {
            var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/squash");
            Assert.True(detected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", null);
        }
    }

    [Fact]
    public async Task Repository_auto_gc_hook_is_not_executed_during_scan()
    {
        await RunGitAsync("checkout", "-b", "feature/squash");
        await CommitFileAsync("squash.txt", "squashed", "squashed work");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("merge", "--squash", "feature/squash");
        await RunGitAsync("commit", "-m", "squash merge feature/squash", "--no-gpg-sign");

        // A hostile repo must not get its pre-auto-gc hook executed by a scan:
        // auto-gc / auto-maintenance are disabled on every git invocation.
        var hooksDirectory = Path.Combine(_repoPath, "hostile-hooks");
        Directory.CreateDirectory(hooksDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(hooksDirectory, "pre-auto-gc"),
            "#!/bin/sh\necho ran > gitsweep-hook-marker.txt\n");
        await RunGitAsync("config", "core.hooksPath", hooksDirectory);
        await RunGitAsync("config", "gc.auto", "1");

        var detected = await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/squash");

        Assert.True(detected);
        Assert.False(File.Exists(Path.Combine(_repoPath, "gitsweep-hook-marker.txt")));
    }

    private async Task<int> CountLooseObjectsAsync()
    {
        var output = await RunGitAsync("count-objects", "-v");
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(entry => entry.StartsWith("count:", StringComparison.Ordinal));
        return int.Parse(line["count:".Length..].Trim());
    }

    [Fact]
    public async Task Qualified_ref_input_cannot_escape_local_branches()
    {
        Assert.False(await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "main", "refs/heads/main"));
        Assert.False(await _sut.IsSquashOrRebaseMergedAsync(_repoPath, "refs/heads/main", "feature/x"));
    }

    [Fact]
    public async Task DeleteBranchAsync_never_force_deletes_squash_merged_branch()
    {
        await RunGitAsync("checkout", "-b", "feature/squash");
        await CommitFileAsync("squash.txt", "squashed", "squashed work");
        await RunGitAsync("checkout", "main");
        await RunGitAsync("merge", "--squash", "feature/squash");
        await RunGitAsync("commit", "-m", "squash merge feature/squash", "--no-gpg-sign");

        // Detection says the changes are in main, but deletion still goes
        // through the safe `git branch -d`, which refuses because the tip is
        // not an ancestor. GitSweep must never escalate to `-D` on its own.
        var result = await _sut.DeleteBranchAsync(_repoPath, "feature/squash");

        Assert.False(result.Success);
        var remaining = await RunGitAsync("branch", "--list", "feature/squash");
        Assert.Contains("feature/squash", remaining);
    }

    private async Task CommitFileAsync(string fileName, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, fileName), content);
        await RunGitAsync("add", ".");
        await RunGitAsync("commit", "-m", message, "--no-gpg-sign");
    }

    private async Task AppendFileAsync(string fileName, string content, string message)
    {
        await File.AppendAllTextAsync(Path.Combine(_repoPath, fileName), content);
        await RunGitAsync("add", ".");
        await RunGitAsync("commit", "-m", message, "--no-gpg-sign");
    }

    private async Task<string> RunGitAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
        }

        return stdout;
    }
}
