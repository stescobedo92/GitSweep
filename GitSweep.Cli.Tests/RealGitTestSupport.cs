using System.Diagnostics;
using System.Text;
using GitSweep.Cli.Commands;
using GitSweep.Core.Models;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;

namespace GitSweep.Cli.Tests;

/// <summary>
/// Test-side git for the CLI tests that run the commands over the real
/// <see cref="GitRepositoryService"/> and <see cref="TrashService"/>. Commits
/// get a fixed date in the past, so <c>--age 0</c> sees them as stale in any
/// time zone.
/// </summary>
internal static class RealGit
{
    public const string PastDate = "2020-01-01T00:00:00Z";

    public static async Task<string> RunAsync(string workingDirectory, params string[] arguments)
    {
        var (exitCode, stdout, stderr) = await RunRawAsync(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {stderr}{stdout}");
        }

        return stdout;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunRawAsync(
        string workingDirectory, string[] arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveGit(),
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_AUTHOR_DATE"] = PastDate;
        startInfo.Environment["GIT_COMMITTER_DATE"] = PastDate;
        if (environment is not null)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    // The same git GitSweep resolves: only fully qualified PATH entries.
    public static string ResolveGit()
    {
        var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var rawEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var entry = rawEntry.Trim().Trim('"');
            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry)) continue;
            var candidate = Path.Combine(entry, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("git was not found on PATH.");
    }

    // Configuration-defined hooks shipped in git 2.54.0. Version-based (an
    // exact check, not a `git hook run` probe that misreads git < 2.36).
    public static async Task<bool> ConfigHooksSupportedAsync(string workingDirectory)
    {
        var output = await RunAsync(workingDirectory, "--version");
        var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\.(\d+)");
        return match.Success &&
               new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) >= new Version(2, 54);
    }

    public static async Task<string> InitRepoAsync(string path)
    {
        Directory.CreateDirectory(path);
        await RunAsync(path, "init", "-b", "main");
        await RunAsync(path, "config", "user.email", "tests@example.com");
        await RunAsync(path, "config", "user.name", "GitSweep Tests");
        return path;
    }

    public static async Task<string> CommitFileAsync(string repo, string fileName, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, fileName), content);
        await RunAsync(repo, "add", ".");
        await RunAsync(repo, "commit", "-m", message, "--no-gpg-sign");
        return (await RunAsync(repo, "rev-parse", "HEAD")).Trim();
    }

    public static async Task<string?> TipAsync(string repo, string branch)
    {
        var (exitCode, stdout, _) = await RunRawAsync(repo, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"]);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    public static async Task<string[]> KeepRefsAsync(string repo) =>
        (await RunAsync(repo, "for-each-ref", "--format=%(refname)", "refs/gitsweep/"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string[] TrashFiles(string repo)
    {
        var trash = Path.Combine(repo, ".git", "gitsweep", "trash");
        return Directory.Exists(trash) ? Directory.GetFiles(trash, "*.json") : [];
    }

    /// <summary>Whether <paramref name="sha"/> still exists as an object.</summary>
    public static async Task<bool> ObjectExistsAsync(string repo, string sha) =>
        (await RunRawAsync(repo, ["cat-file", "-e", sha + "^{commit}"])).ExitCode == 0;

    /// <summary>What a hostile or merely eager <c>git gc</c> does to unreferenced commits.</summary>
    public static async Task ExpireReflogsAndPruneAsync(string repo)
    {
        await RunAsync(repo, "reflog", "expire", "--expire=now", "--expire-unreachable=now", "--all");
        await RunAsync(repo, "gc", "--prune=now", "--quiet");
    }
}

/// <summary>
/// Delegates every call to a real <see cref="IGitRepositoryService"/>; a test
/// overrides the one member it needs to change.
/// </summary>
internal class DelegatingGitRepositoryService(IGitRepositoryService inner) : IGitRepositoryService
{
    public virtual Task<bool> IsGitRepositoryAsync(string path, CancellationToken ct = default) => inner.IsGitRepositoryAsync(path, ct);
    public virtual Task<string> GetDefaultBranchNameAsync(string path, CancellationToken ct = default) => inner.GetDefaultBranchNameAsync(path, ct);
    public virtual Task<List<BranchInfo>> GetLocalBranchesAsync(string path, string targetBranch, CancellationToken ct = default) => inner.GetLocalBranchesAsync(path, targetBranch, ct);
    public virtual Task<(bool Success, string ErrorMessage)> DeleteBranchAsync(string path, string branchName, CancellationToken ct = default) => inner.DeleteBranchAsync(path, branchName, ct);
    public virtual Task<(bool Success, string ErrorMessage)> FetchPruneAsync(string path, CancellationToken ct = default) => inner.FetchPruneAsync(path, ct);
    public virtual Task<bool> IsSquashOrRebaseMergedAsync(string path, string targetBranch, string branchName, CancellationToken ct = default) => inner.IsSquashOrRebaseMergedAsync(path, targetBranch, branchName, ct);
    public virtual Task<BranchRiskSignals> GetBranchRiskSignalsAsync(string path, string targetBranch, string branchName, CancellationToken ct = default) => inner.GetBranchRiskSignalsAsync(path, targetBranch, branchName, ct);
    public virtual Task<string?> GetGitCommonDirectoryAsync(string path, CancellationToken ct = default) => inner.GetGitCommonDirectoryAsync(path, ct);
    public virtual Task<string?> GetBranchTipShaAsync(string path, string branchName, CancellationToken ct = default) => inner.GetBranchTipShaAsync(path, branchName, ct);
    public virtual Task<bool> BranchExistsAsync(string path, string branchName, CancellationToken ct = default) => inner.BranchExistsAsync(path, branchName, ct);
    public virtual Task<(bool Success, string ErrorMessage)> CreateBranchAsync(string path, string branchName, string commitSha, CancellationToken ct = default) => inner.CreateBranchAsync(path, branchName, commitSha, ct);
    public virtual Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default) => inner.GetCurrentBranchAsync(path, ct);
    public virtual Task<IReadOnlyList<string>> GetWorktreeBranchesAsync(string path, CancellationToken ct = default) => inner.GetWorktreeBranchesAsync(path, ct);
    public virtual Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) => inner.GetRepositoryRootAsync(path, ct);
    public virtual Task<(bool Success, string ErrorMessage)> CreateTrashKeepRefAsync(string path, string trashId, string commitSha, CancellationToken ct = default) => inner.CreateTrashKeepRefAsync(path, trashId, commitSha, ct);
    public virtual Task<bool> DeleteTrashKeepRefAsync(string path, string trashId, string commitSha, CancellationToken ct = default) => inner.DeleteTrashKeepRefAsync(path, trashId, commitSha, ct);
}

/// <summary>Runs the built CLI (<c>gitsweep-cli.dll</c>) through the dotnet host.</summary>
internal static class CliProcess
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(typeof(RestoreCommand).Assembly.Location);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdoutTask + await stderrTask);
    }

    private static string ResolveDotnetHost()
    {
        var fromCli = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(fromCli) && File.Exists(fromCli))
        {
            return fromCli;
        }

        var fileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var rawEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var entry = rawEntry.Trim().Trim('"');
            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry)) continue;
            var candidate = Path.Combine(entry, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("The dotnet host was not found (DOTNET_HOST_PATH or PATH).");
    }
}
