using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// Security tests for the gone+fetch feature: a hostile cloned repository
/// must not turn <c>clean --fetch</c> into local code execution, fetch
/// errors must not leak credentials, and gone parsing must not be forgeable
/// through crafted branch names.
/// </summary>
public sealed class GoneFetchSecurityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private string _repoPath = "";
    private string _remotePath = "";
    private readonly GitRepositoryService _sut = new();

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
    public async Task FetchPrune_does_not_execute_repo_configured_ssh_command()
    {
        // A cloned repo ships core.sshCommand pointing at a script; a plain
        // `git fetch` would run it as the ssh program. The fetch must use
        // the pinned `ssh` instead: the (unreachable) fetch fails and the
        // script's marker file is never created.
        var repo = Path.Combine(_root, "hostile-ssh");
        Directory.CreateDirectory(repo);
        await RunGitAsync(repo, "init", "-b", "main");
        var marker = Path.Combine(_root, "ssh-owned.txt");
        var scriptPath = Path.Combine(_root, "pwn.sh");
        var forwardRoot = _root.Replace('\\', '/');
        await File.WriteAllTextAsync(scriptPath,
            $"#!/bin/sh\necho owned > {forwardRoot}/ssh-owned.txt\nexit 1\n");
        await RunGitAsync(repo, "config", "core.sshCommand", $"{forwardRoot}/pwn.sh");
        await RunGitAsync(repo, "remote", "add", "origin", "git@127.0.0.1:repo.git");

        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.False(fetch.Success);
        Assert.False(File.Exists(marker), "repo-configured core.sshCommand was executed");
    }

    [Fact]
    public async Task FetchPrune_failure_does_not_leak_credentials_from_remote_url()
    {
        const string secret = "FetchSecretToken98765";
        var repo = Path.Combine(_root, "credential-url");
        Directory.CreateDirectory(repo);
        await RunGitAsync(repo, "init", "-b", "main");
        await RunGitAsync(repo, "remote", "add", "origin",
            $"https://user:{secret}@127.0.0.1:1/repo.git");

        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.False(fetch.Success);
        Assert.DoesNotContain(secret, fetch.ErrorMessage);
    }

    [Fact]
    public void Redact_removes_url_userinfo_query_secrets_and_bearer_tokens()
    {
        Assert.Equal(
            "fatal: unable to access 'https://***@example.com/repo.git/': failed",
            GitOutputRedactor.Redact("fatal: unable to access 'https://user:secretpass@example.com/repo.git/': failed"));
        Assert.Equal(
            "GET https://example.com/x?token=***&other=1",
            GitOutputRedactor.Redact("GET https://example.com/x?token=abc123&other=1"));
        Assert.Equal(
            "Authorization: Bearer ***",
            GitOutputRedactor.Redact("Authorization: Bearer abc.def.ghi"));
        Assert.Equal("plain git output", GitOutputRedactor.Redact("plain git output"));
        Assert.Equal(string.Empty, GitOutputRedactor.Redact(null));
    }

    [Fact]
    public async Task Branch_with_pipe_in_name_and_gone_upstream_keeps_its_full_name()
    {
        // '|' is legal in git ref names. Written through packed-refs (loose
        // ref files cannot hold '|' on Windows filesystems), the branch
        // must survive parsing intact: a printable '|' delimiter would
        // split the name and could forge the "[gone]" field.
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/pipe-source");
        await CommitFileAsync("pipe.txt", "pipe", "pipe work");
        var tip = (await RunGitAsync(_repoPath, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "merge", "feature/pipe-source", "--no-ff", "-m", "merge pipe", "--no-gpg-sign");

        var gitDir = Path.Combine(_repoPath, ".git");
        var packedRefsPath = Path.Combine(gitDir, "packed-refs");
        await File.AppendAllTextAsync(packedRefsPath,
            $"{tip} refs/heads/feature/pipe|gone\n{tip} refs/remotes/origin/feature/pipe|gone\n");
        await RunGitAsync(_repoPath, "config", "branch.feature/pipe|gone.remote", "origin");
        await RunGitAsync(_repoPath, "config", "branch.feature/pipe|gone.merge", "refs/heads/feature/pipe|gone");
        // Simulate the remote-tracking ref removal that fetch --prune
        // performs (rewriting packed-refs directly: even deleting a packed
        // ref through update-ref needs a loose lock file, whose name NTFS
        // rejects for '|' on Windows).
        var remaining = (await File.ReadAllLinesAsync(packedRefsPath))
            .Where(line => !line.EndsWith(" refs/remotes/origin/feature/pipe|gone", StringComparison.Ordinal));
        // packed-refs is LF-terminated; WriteAllLinesAsync would emit CRLF
        // on Windows and corrupt the ref names.
        await File.WriteAllTextAsync(packedRefsPath, string.Join("\n", remaining) + "\n");

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/pipe|gone");
        Assert.Equal("origin/feature/pipe|gone", branch.UpstreamName);
        Assert.True(branch.IsUpstreamGone);
        Assert.True(branch.IsSafeGoneCandidate);
    }

    [Fact]
    public async Task FetchPrune_does_not_apply_repo_configured_refspec_to_local_branches()
    {
        // Hostile cloned repo: remote.origin.fetch maps remote heads onto
        // LOCAL branches. A plain `git fetch --prune` would force-reset the
        // victim branch to the remote tip (losing its local-only commit)
        // and prune the checked-out local-only branch outright. The fetch
        // must only touch refs/remotes/origin/*.
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/victim");
        await CommitFileAsync("victim.txt", "victim", "victim work");
        await RunGitAsync(_repoPath, "push", "origin", "feature/victim");
        await CommitFileAsync("local-only.txt", "local", "local only work");
        var victimTip = (await RunGitAsync(_repoPath, "rev-parse", "refs/heads/feature/victim")).Trim();
        // Current branch is not on the remote, so a hostile prune would
        // delete it instead of refusing to fetch into a checked-out branch.
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/local-only");
        await RunGitAsync(_repoPath, "config", "remote.origin.fetch", "+refs/heads/*:refs/heads/*");

        var fetch = await _sut.FetchPruneAsync(_repoPath);

        Assert.True(fetch.Success, fetch.ErrorMessage);
        Assert.Equal(victimTip, await _sut.GetBranchTipShaAsync(_repoPath, "feature/victim"));
        Assert.True(await _sut.BranchExistsAsync(_repoPath, "feature/local-only"));
        Assert.True(await _sut.BranchExistsAsync(_repoPath, "feature/victim"));
    }

    [Fact]
    public async Task FetchPrune_does_not_execute_remote_helper_transport()
    {
        // A remote URL of the form `foo::bar` (or any non-built-in scheme)
        // makes git run a `git-remote-foo` program found on PATH. With the
        // protocol allowlist the transport is refused before any helper
        // can run, so the marker file is never created.
        var fakeBin = Path.Combine(_root, "fake-bin");
        Directory.CreateDirectory(fakeBin);
        var marker = Path.Combine(_root, "remote-helper-owned.txt");
        await File.WriteAllTextAsync(
            Path.Combine(fakeBin, "git-remote-foo"),
            $"#!/bin/sh\necho owned > {marker.Replace('\\', '/')}\nexit 1\n");
        var repo = Path.Combine(_root, "hostile-helper");
        Directory.CreateDirectory(repo);
        await RunGitAsync(repo, "init", "-b", "main");
        await RunGitAsync(repo, "remote", "add", "origin", "foo::bar");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);

            var fetch = await _sut.FetchPruneAsync(repo);

            Assert.False(fetch.Success);
            Assert.False(File.Exists(marker), "remote-helper transport program was executed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task FetchPrune_fails_closed_when_origin_is_not_configured()
    {
        var repo = Path.Combine(_root, "no-origin");
        Directory.CreateDirectory(repo);
        await RunGitAsync(repo, "init", "-b", "main");

        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.False(fetch.Success);
        Assert.Contains("origin", fetch.ErrorMessage);
    }

    [Fact]
    public void Redact_covers_multi_at_passwords_and_more_secret_shapes()
    {
        // A password containing '@': redaction must run to the LAST '@' of
        // the authority, not stop at the first one and leak the tail.
        Assert.Equal(
            "https://***@example.com/repo.git",
            GitOutputRedactor.Redact("https://user:p@ss@example.com/repo.git"));
        Assert.Equal(
            "https://example.com/x?client_secret=***&sig=***&ok=1",
            GitOutputRedactor.Redact("https://example.com/x?client_secret=abc&sig=def&ok=1"));
        Assert.Equal(
            "Authorization: Token ***",
            GitOutputRedactor.Redact("Authorization: Token abc123"));
        Assert.Equal(
            "Proxy-Authorization: Basic ***",
            GitOutputRedactor.Redact("Proxy-Authorization: Basic abc123"));
        // No scheme-relative or path '@' is treated as userinfo.
        Assert.Equal(
            "https://example.com/path@x",
            GitOutputRedactor.Redact("https://example.com/path@x"));
    }

    [Fact]
    public async Task Upstream_whose_name_contains_gone_is_not_gone_while_it_exists()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/gone-alive");
        await CommitFileAsync("alive.txt", "alive", "alive work");
        await RunGitAsync(_repoPath, "push", "-u", "origin", "feature/gone-alive");
        await RunGitAsync(_repoPath, "checkout", "main");

        var fetch = await _sut.FetchPruneAsync(_repoPath);
        Assert.True(fetch.Success, fetch.ErrorMessage);

        var branches = await _sut.GetLocalBranchesAsync(_repoPath, "main");
        var branch = Assert.Single(branches, b => b.Name == "feature/gone-alive");
        Assert.False(branch.IsUpstreamGone);
        Assert.False(branch.IsSafeGoneCandidate);
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
