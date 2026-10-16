using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// SEC-002 / SEC-003 / OBS-A3: every git invocation is bounded in time and
/// output size, a limit failure is never reported as a git answer (and
/// never as truncated success), and the child's stdin is closed at once.
/// Limits are injected through the internal runner overload and the
/// internal <see cref="GitCommandLimits"/> constructor, so no test ever
/// waits for the real 2/5-minute deadlines. Blocking/endless commands are
/// repo-local aliases run through git's shell (<c>sleep</c>/<c>yes</c> ship
/// with Git for Windows and every Linux image), or a real git:// fetch
/// against a TCP server that accepts and then stalls (the SEC-002 attack).
/// </summary>
public sealed class GitCommandLimitsTests : IAsyncLifetime
{
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(1);
    private const int GenerousChars = 1024 * 1024;

    // A command that is killed (or never waited for) must come back well
    // before the 30-second sleep it would otherwise finish.
    private static readonly TimeSpan PromptReturn = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private string _repoPath = "";

    public async Task InitializeAsync()
    {
        _repoPath = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoPath);
        await RunGitAsync(_repoPath, "init", "-b", "main");
        await RunGitAsync(_repoPath, "config", "user.email", "tests@example.com");
        await RunGitAsync(_repoPath, "config", "user.name", "GitSweep Tests");
        await RunGitAsync(_repoPath, "config", "alias.slow", "!sleep 30");
        await RunGitAsync(_repoPath, "config", "alias.endless", "!yes");
        await CommitFileAsync("README.md", "initial", "initial commit");
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void Limits_use_the_documented_values_and_failure_codes_are_distinct_from_git_codes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), GitRepositoryService.LocalCommandTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), GitRepositoryService.FetchTimeout);
        Assert.Equal(64 * 1024 * 1024, GitRepositoryService.MaxStandardOutputChars);
        Assert.Equal(1024 * 1024, GitRepositoryService.MaxStandardErrorChars);

        var defaults = GitCommandLimits.Default;
        Assert.Equal(GitRepositoryService.LocalCommandTimeout, defaults.LocalCommandTimeout);
        Assert.Equal(GitRepositoryService.FetchTimeout, defaults.FetchTimeout);
        Assert.Equal(GitRepositoryService.MaxStandardOutputChars, defaults.MaxStandardOutputChars);
        Assert.Equal(GitRepositoryService.MaxStandardErrorChars, defaults.MaxStandardErrorChars);

        // 0 = success, 1 = "no" for merge-base --is-ancestor / show-ref
        // --verify / rev-parse --verify --quiet, 128 = fatal, 129 = usage.
        // A limit failure must never be mistaken for any of them, and a git
        // process cannot return a negative code on POSIX.
        var meaningful = new[] { 0, 1, 128, 129 };
        Assert.DoesNotContain(GitRepositoryService.TimedOutExitCode, meaningful);
        Assert.DoesNotContain(GitRepositoryService.OutputLimitExceededExitCode, meaningful);
        Assert.NotEqual(GitRepositoryService.TimedOutExitCode, GitRepositoryService.OutputLimitExceededExitCode);
        Assert.True(GitRepositoryService.TimedOutExitCode < 0);
        Assert.True(GitRepositoryService.OutputLimitExceededExitCode < 0);
    }

    [Fact]
    public void Limits_reject_non_positive_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GitCommandLimits(TimeSpan.Zero, Generous, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GitCommandLimits(Generous, TimeSpan.FromSeconds(-1), 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GitCommandLimits(Generous, Generous, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GitCommandLimits(Generous, Generous, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GitCommandLimits(TimeSpan.FromDays(400), Generous, 1, 1));
    }

    [Fact]
    public async Task Command_exceeding_the_deadline_returns_the_timed_out_result_without_waiting_for_it()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(["slow"], timeout: TimeSpan.FromMilliseconds(500));
        stopwatch.Stop();

        Assert.Equal(GitRepositoryService.TimedOutExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("git slow timed out after 0.5 seconds.", result.Error);
        Assert.True(stopwatch.Elapsed < PromptReturn, $"timed-out command took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Timeout_kills_the_whole_process_tree_not_just_git()
    {
        // git -> sh, and sh then execs `sleep 30`. The shell records its own
        // PID first: on Linux exec keeps it, so it is the sleep itself; with
        // Git for Windows it is the Windows PID (MSYS /proc/<pid>/winpid) of
        // the sh process that stays alive as the parent of sleep.exe. Killing
        // only git (or only the cmd\git.exe wrapper) would leave it running.
        // (A shell *fork* is avoided on purpose: on Windows an MSYS fork
        // leaves its grandchild with a dead parent PID, which no process-tree
        // walk can reach; git never forks a shell under GitSweep's hardening.)
        var pidFile = Path.Combine(_root, "sleep.pid").Replace('\\', '/');
        await RunGitAsync(_repoPath, "config", "alias.tree",
            $"!cat /proc/$$/winpid > '{pidFile}' 2>/dev/null || echo $$ > '{pidFile}'; exec sleep 30");

        var result = await RunAsync(["tree"], timeout: TimeSpan.FromSeconds(3));

        Assert.Equal(GitRepositoryService.TimedOutExitCode, result.ExitCode);
        Assert.True(File.Exists(pidFile), "the alias did not start before the deadline");
        var childPid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var stopwatch = Stopwatch.StartNew();
        while (IsProcessRunning(childPid) && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(100);
        }

        Assert.False(IsProcessRunning(childPid), $"git's child process (pid {childPid}) survived the timeout");
    }

    [Fact]
    public async Task User_cancellation_still_throws_OperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(["slow"], timeout: Generous, ct: cancellation.Token));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < PromptReturn, $"cancelled command took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Already_cancelled_token_throws_OperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(["rev-parse", "--git-dir"], ct: cancellation.Token));
    }

    [Fact]
    public async Task Stdout_over_the_cap_is_a_failed_result_never_truncated_success()
    {
        var sha = (await RunGitAsync(_repoPath, "rev-parse", "HEAD")).Trim();
        var expected = sha + "\n";

        var atCap = await RunAsync(["rev-parse", "HEAD"], maxOutputChars: expected.Length);
        var overCap = await RunAsync(["rev-parse", "HEAD"], maxOutputChars: expected.Length - 1);

        Assert.Equal(0, atCap.ExitCode);
        Assert.Equal(expected, atCap.Output);

        Assert.Equal(GitRepositoryService.OutputLimitExceededExitCode, overCap.ExitCode);
        Assert.Equal(string.Empty, overCap.Output);
        Assert.Equal($"git rev-parse output exceeded the {expected.Length - 1}-character limit.", overCap.Error);
    }

    [Fact]
    public async Task Stderr_over_the_cap_is_a_failed_result()
    {
        var uncapped = await RunAsync(["rev-parse", "--verify", "refs/heads/does-not-exist"]);
        var capped = await RunAsync(["rev-parse", "--verify", "refs/heads/does-not-exist"], maxErrorChars: 8);

        Assert.Equal(128, uncapped.ExitCode);
        Assert.Contains("fatal", uncapped.Error);

        Assert.Equal(GitRepositoryService.OutputLimitExceededExitCode, capped.ExitCode);
        Assert.Equal(string.Empty, capped.Output);
        Assert.Equal("git rev-parse output exceeded the 8-character limit.", capped.Error);
    }

    [Fact]
    public async Task Endless_output_is_cut_at_the_cap_without_waiting_for_the_deadline()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(["endless"], timeout: Generous, maxOutputChars: 4096);
        stopwatch.Stop();

        Assert.Equal(GitRepositoryService.OutputLimitExceededExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("git endless output exceeded the 4,096-character limit.", result.Error);
        Assert.True(stopwatch.Elapsed < PromptReturn, $"endless output took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Child_stdin_is_closed_so_a_command_reading_stdin_sees_EOF()
    {
        // With stdin left open, hash-object would block until the deadline.
        var result = await RunAsync(["hash-object", "--stdin"], timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", result.Output.Trim());
    }

    [Fact]
    public async Task Normal_commands_and_meaningful_non_zero_exit_codes_are_unaffected()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/unmerged");
        await CommitFileAsync("unmerged.txt", "unmerged", "unmerged work");
        await RunGitAsync(_repoPath, "checkout", "main");

        var gitDir = await RunAsync(["rev-parse", "--git-dir"]);
        var missingRef = await RunAsync(["show-ref", "--verify", "--quiet", "refs/heads/does-not-exist"]);
        var notAncestor = await RunAsync(
            ["merge-base", "--is-ancestor", "--end-of-options", "refs/heads/feature/unmerged", "refs/heads/main"]);
        var ancestor = await RunAsync(
            ["merge-base", "--is-ancestor", "--end-of-options", "refs/heads/main", "refs/heads/feature/unmerged"]);

        Assert.Equal(0, gitDir.ExitCode);
        Assert.Equal(".git", gitDir.Output.Trim());
        Assert.Equal(string.Empty, gitDir.Error);
        Assert.Equal(1, missingRef.ExitCode);
        Assert.Equal(1, notAncestor.ExitCode);
        Assert.Equal(0, ancestor.ExitCode);

        // The public service with default limits behaves exactly as before.
        var sut = new GitRepositoryService();
        Assert.True(await sut.IsGitRepositoryAsync(_repoPath));
        Assert.Equal("main", await sut.GetCurrentBranchAsync(_repoPath));
        Assert.True(await sut.BranchExistsAsync(_repoPath, "feature/unmerged"));
        Assert.False(await sut.BranchExistsAsync(_repoPath, "feature/does-not-exist"));
    }

    [Fact]
    public async Task FetchPrune_against_a_stalled_origin_times_out_and_leaves_no_git_running()
    {
        // SEC-002: an origin that accepts the TCP connection and then never
        // answers used to make `clean --fetch` hang forever.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        await RunGitAsync(_repoPath, "remote", "add", "origin", $"git://127.0.0.1:{port}/stalled.git");

        var sut = new GitRepositoryService(new GitCommandLimits(
            localCommandTimeout: Generous,
            fetchTimeout: TimeSpan.FromSeconds(3),
            maxStandardOutputChars: GenerousChars,
            maxStandardErrorChars: GenerousChars));

        var stopwatch = Stopwatch.StartNew();
        var (success, message) = await sut.FetchPruneAsync(_repoPath);
        stopwatch.Stop();

        Assert.False(success);
        Assert.Equal("git fetch timed out after 3 seconds.", message);
        Assert.True(stopwatch.Elapsed < PromptReturn, $"stalled fetch took {stopwatch.Elapsed}");

        // git connected to the stalled origin before the deadline. Once its
        // whole process tree is killed the OS closes that socket, so the
        // server reads EOF (or a reset); a surviving git would keep the
        // connection open and this read would hit its own timeout instead.
        Assert.True(acceptTask.IsCompleted, "git never connected to the stalled origin");
        using var connection = await acceptTask;
        Assert.True(
            await ConnectionClosedByPeerAsync(connection, TimeSpan.FromSeconds(15)),
            "the git process that held the connection is still running");
    }

    [Fact]
    public async Task State_queries_throw_on_a_limit_failure_instead_of_reporting_a_default()
    {
        // A 1-character stdout cap makes every command with output fail by
        // limit. None of these may degrade to "not a repository", "no
        // current branch", "no worktrees", "branch does not exist", "no
        // root" (config fallback), "no branches" or a fallback target.
        var sut = new GitRepositoryService(new GitCommandLimits(Generous, Generous, 1, GenerousChars));

        var ex = await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.GetCurrentBranchAsync(_repoPath));
        Assert.Equal("git branch output exceeded the 1-character limit.", ex.Message);

        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.IsGitRepositoryAsync(_repoPath));
        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.GetWorktreeBranchesAsync(_repoPath));
        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.BranchExistsAsync(_repoPath, "main"));
        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.GetBranchTipShaAsync(_repoPath, "main"));
        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.GetRepositoryRootAsync(_repoPath));
        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.GetGitCommonDirectoryAsync(_repoPath));
        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.GetDefaultBranchNameAsync(_repoPath));
        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.GetLocalBranchesAsync(_repoPath, "main"));
        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.DeleteBranchAsync(_repoPath, "feature/any"));
    }

    [Fact]
    public async Task Protection_config_load_fails_closed_when_the_repository_root_is_unknown()
    {
        // GitSweepConfigService falls back to the given path when root
        // resolution throws InvalidOperationException/IOException; from a
        // subdirectory that fallback would miss the root .gitsweep.json and
        // load the default config. A limit failure must not take that path.
        var subdirectory = Path.Combine(_repoPath, "src");
        Directory.CreateDirectory(subdirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_repoPath, ".gitsweep.json"), "{\"protectedBranches\":[\"release/*\"]}");
        var capped = new GitRepositoryService(new GitCommandLimits(Generous, Generous, 1, GenerousChars));

        var loaded = await new GitSweepConfigService(new GitRepositoryService()).LoadAsync(subdirectory);
        Assert.Contains("release/*", loaded.ProtectedBranches);

        await Assert.ThrowsAsync<GitCommandLimitException>(
            () => new GitSweepConfigService(capped).LoadAsync(subdirectory));
    }

    [Fact]
    public async Task Delete_hitting_a_limit_is_an_unknown_outcome_not_reported_as_not_deleted()
    {
        // feature/pushed is merged into its upstream but not into HEAD, so
        // `git branch -d` writes a "not yet merged to HEAD" warning to stderr
        // *before* it deletes the ref. With a 1-character stderr cap the
        // runner kills the tree while git may or may not be deleting: the
        // outcome is indeterminate. That is exactly why it must not come
        // back as (false, ...): clean would roll back the trash entry of a
        // branch that may already be gone. Only the exception is asserted;
        // the deterministic "deleted, then limit" case is the test below.
        await RunGitAsync(_repoPath, "remote", "add", "origin", Path.Combine(_root, "unused.git"));
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/pushed");
        await CommitFileAsync("pushed.txt", "pushed", "pushed work");
        var tip = (await RunGitAsync(_repoPath, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "update-ref", "refs/remotes/origin/feature/pushed", tip);
        await RunGitAsync(_repoPath, "config", "branch.feature/pushed.remote", "origin");
        await RunGitAsync(_repoPath, "config", "branch.feature/pushed.merge", "refs/heads/feature/pushed");

        var sut = new GitRepositoryService(new GitCommandLimits(Generous, Generous, GenerousChars, 1));

        await Assert.ThrowsAsync<GitCommandLimitException>(() => sut.DeleteBranchAsync(_repoPath, "feature/pushed"));
    }

    [Fact]
    public async Task Limit_failure_can_follow_a_completed_branch_delete()
    {
        // Deterministic "deleted, then limit": `git branch -d` prints
        // "Deleted branch ..." to stdout only after the ref is deleted, so a
        // 1-character stdout cap always fails after the deletion. The runner
        // still reports a limit failure (never success, never a "no"), which
        // is why DeleteBranchAsync must not translate it into "not deleted".
        await RunGitAsync(_repoPath, "branch", "feature/done");

        var result = await RunAsync(["branch", "-d", "--", "feature/done"], maxOutputChars: 1);

        Assert.Equal(GitRepositoryService.OutputLimitExceededExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.False(
            await new GitRepositoryService().BranchExistsAsync(_repoPath, "feature/done"),
            "git deletes the ref before it writes the stdout line that overflows the cap");
    }

    [Fact]
    public async Task Merge_and_risk_detection_under_a_limit_failure_never_report_a_safe_signal()
    {
        await RunGitAsync(_repoPath, "checkout", "-b", "feature/squash");
        await CommitFileAsync("squash.txt", "squash", "squash work");
        await RunGitAsync(_repoPath, "checkout", "main");
        await RunGitAsync(_repoPath, "merge", "--squash", "feature/squash");
        await RunGitAsync(_repoPath, "commit", "-m", "squash merge", "--no-gpg-sign");

        Assert.True(
            await new GitRepositoryService().IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/squash"),
            "precondition: with default limits the squash merge is detected");

        var capped = new GitRepositoryService(new GitCommandLimits(Generous, Generous, 1, GenerousChars));
        var squashMerged = await capped.IsSquashOrRebaseMergedAsync(_repoPath, "main", "feature/squash");
        var signals = await capped.GetBranchRiskSignalsAsync(_repoPath, "main", "feature/squash");
        var assessment = new BranchRiskClassifier().Classify(
            new BranchInfo("feature/squash", DateTime.UtcNow, false), signals);

        Assert.False(squashMerged);
        Assert.False(signals.IsReallyMerged);
        Assert.Null(signals.UniqueCommitCount);
        Assert.Null(signals.UnpushedCommitCount);
        Assert.False(signals.IsUpstreamGone);
        Assert.NotEqual(BranchRiskLevel.Safe, assessment.Level);
    }

    [Fact]
    public async Task CreateBranch_hitting_a_limit_reports_failure_so_restore_keeps_the_trash_entry()
    {
        var tip = (await RunGitAsync(_repoPath, "rev-parse", "HEAD")).Trim();
        var sut = new GitRepositoryService(new GitCommandLimits(Generous, Generous, GenerousChars, 1));

        // main already exists: git refuses and explains on stderr.
        var (success, message) = await sut.CreateBranchAsync(_repoPath, "main", tip);

        Assert.False(success);
        Assert.Equal("git branch output exceeded the 1-character limit.", message);
    }

    // N-1: CreateBranchAsync runs a `for-each-ref refs/heads/` listing first
    // (the case-variant check). A limit failure there must be reported as
    // Success == false, not thrown, so restore keeps its record and keep-ref
    // and the batch continues. (The stderr-cap test above exercises the later
    // `branch` command; this one caps stdout, which the listing writes.)
    [Fact]
    public async Task CreateBranch_hitting_a_stdout_limit_on_the_case_variant_listing_reports_failure_not_a_throw()
    {
        var tip = (await RunGitAsync(_repoPath, "rev-parse", "HEAD")).Trim();
        var sut = new GitRepositoryService(new GitCommandLimits(Generous, Generous, 1, GenerousChars));

        var (success, message) = await sut.CreateBranchAsync(_repoPath, "restored", tip);

        Assert.False(success);
        Assert.Contains("for-each-ref", message);
        Assert.Contains("exceeded the 1-character limit", message);
    }

    private Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] arguments,
        TimeSpan? timeout = null,
        int maxOutputChars = GenerousChars,
        int maxErrorChars = GenerousChars,
        CancellationToken ct = default) =>
        GitRepositoryService.RunGitProcessAsync(
            _repoPath, arguments, timeout ?? Generous, maxOutputChars, maxErrorChars, ct);

    private static bool IsProcessRunning(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            // A killed process stays a zombie until its (re)parent reaps it;
            // a zombie runs nothing, so it counts as gone.
            string stat;
            try
            {
                stat = File.ReadAllText($"/proc/{pid}/stat");
            }
            catch (IOException)
            {
                return false;
            }

            var state = stat[stat.LastIndexOf(')') + 2];
            return state is not ('Z' or 'X');
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> ConnectionClosedByPeerAsync(Socket connection, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        try
        {
            // Drain git's request first; a read of 0 bytes is the peer's FIN.
            while (await connection.ReceiveAsync(buffer, SocketFlags.None, deadline.Token) > 0)
            {
            }

            return true;
        }
        catch (SocketException)
        {
            return true; // reset/abort: the peer process is gone.
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task CommitFileAsync(string fileName, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, fileName), content);
        await RunGitAsync(_repoPath, "add", ".");
        await RunGitAsync(_repoPath, "commit", "-m", message, "--no-gpg-sign");
    }

    internal static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
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
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
        return stdout;
    }
}
