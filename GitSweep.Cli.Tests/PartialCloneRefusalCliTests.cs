using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GitSweep.Cli.Commands;
using GitSweep.Core.Services;
using GitSweep.Infrastructure.Services;
using NSubstitute;
using Xunit;

namespace GitSweep.Cli.Tests;

/// <summary>
/// Task 11 gate V1, end to end: <c>restore</c>/<c>undo</c> on a hostile
/// partial clone recreate a trashed branch with <c>git branch -- &lt;name&gt;
/// &lt;sha&gt;</c>, and a trash entry (inside the untrusted repository) can name
/// an absent SHA, which git would lazily fetch from the promisor — running the
/// repository's <c>remote.origin.uploadpack</c>. The real CLI process
/// (<c>gitsweep-cli.dll</c>, its global exception handler included) must print
/// the partial-clone refusal, exit 1, create no branch and run nothing.
/// </summary>
public sealed class PartialCloneRefusalCliTests : IAsyncLifetime
{
    private const string AbsentSha = "0123456789abcdef0123456789abcdef01234567";
    private const string RestoredBranch = "feature/restored";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));

    // Same pinned options as GitRepositoryService.HardenedGitOptions (for the
    // positive control: hardening without the partial-clone guard).
    private static readonly string[] HardenedOptions =
    [
        "--no-pager",
        "-c", "core.fsmonitor=false", "-c", "core.pager=cat", "-c", "commit.gpgsign=false",
        "-c", "gc.auto=0", "-c", "maintenance.auto=false",
        "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "protocol.http.allow=always",
        "-c", "protocol.ssh.allow=always", "-c", "protocol.git.allow=always", "-c", "protocol.file.allow=always",
        "-c", "protocol.local.allow=always", "-c", "protocol.ext.allow=never",
        "-c", "core.sshCommand=ssh", "-c", "core.gitProxy=", "-c", "core.askPass=", "-c", "credential.helper="
    ];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("undo")]
    public async Task Restore_and_undo_on_a_partial_clone_print_the_refusal_exit_1_and_create_nothing(string command)
    {
        var (work, marker) = await BuildHostilePartialCloneWithTrashEntryAsync();

        // Positive control: hardened-equivalent git WITHOUT GIT_NO_LAZY_FETCH
        // creating the trashed branch at its absent SHA lazily fetches it and
        // runs the hostile uploadpack — exactly what restore would run.
        await RunGitAsync(work, [.. HardenedOptions, "branch", "--", RestoredBranch, AbsentSha], allowFailure: true);
        Assert.True(File.Exists(marker), "positive control: `git branch <name> <absent sha>` did not lazily fetch");
        File.Delete(marker);

        string[] cliArguments = command == "restore"
            ? ["restore", "-p", work, "--all", "--yes"]
            : ["undo", "-p", work, "--yes"];
        var (exitCode, output) = await RunCliAsync(cliArguments);

        Assert.True(exitCode == 1, $"expected exit 1, got {exitCode}. Output:\n{output}");
        Assert.Contains("Error:", output);
        Assert.Contains("is a partial clone", output);
        Assert.False(File.Exists(marker), $"{command} ran the lazy-fetch uploadpack script");
        var (showRefExit, _) = await RunGitWithExitCodeAsync(work, ["show-ref", "--verify", "--quiet", $"refs/heads/{RestoredBranch}"]);
        Assert.NotEqual(0, showRefExit);
        Assert.Single(Directory.GetFiles(Path.Combine(work, ".git", "gitsweep", "trash"), "*.json"));
    }

    private async Task<(string Work, string Marker)> BuildHostilePartialCloneWithTrashEntryAsync()
    {
        var up = Path.Combine(_root, "up");
        Directory.CreateDirectory(up);
        await RunGitAsync(up, ["init", "-b", "main"]);
        await RunGitAsync(up, ["config", "user.email", "tests@example.com"]);
        await RunGitAsync(up, ["config", "user.name", "GitSweep Tests"]);
        await RunGitAsync(up, ["config", "uploadpack.allowFilter", "true"]);
        await File.WriteAllTextAsync(Path.Combine(up, "f.txt"), "base");
        await RunGitAsync(up, ["add", "."]);
        await RunGitAsync(up, ["commit", "-m", "base", "--no-gpg-sign"]);

        var work = Path.Combine(_root, "work");
        await RunGitAsync(_root, [.. HardenedOptions, "clone", "--filter=blob:none", "file://" + up.Replace('\\', '/'), work]);

        var marker = Path.Combine(_root, "cli-marker.txt");
        var script = Path.Combine(_root, "pwn-uploadpack.sh");
        await File.WriteAllTextAsync(script,
            $"#!/bin/sh\necho owned >> {marker.Replace('\\', '/')}\nexit 1\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // Forward slashes: git runs uploadpack through `sh`, where '\' escapes.
        await RunGitAsync(work, ["config", "remote.origin.uploadpack", script.Replace('\\', '/')]);

        // Plant the trash entry exactly as GitSweep writes it (TrashService),
        // pointed at the repository's git dir without running git there.
        var locator = Substitute.For<IGitRepositoryService>();
        locator.GetGitCommonDirectoryAsync(work, Arg.Any<CancellationToken>())
            .Returns(Path.GetFullPath(Path.Combine(work, ".git")));
        await new TrashService(locator).SaveAsync(work, RestoredBranch, AbsentSha, Guid.NewGuid());

        return (work, marker);
    }

    // Runs the real CLI (same build output as this test) through the dotnet
    // host, so Program.cs's global exception handler is part of the test.
    private static async Task<(int ExitCode, string Output)> RunCliAsync(string[] arguments)
    {
        var cliDll = typeof(RestoreCommand).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(cliDll);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Remove("GIT_NO_LAZY_FETCH");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        // Spectre wraps at the console width and colors "Error:": strip ANSI
        // sequences and collapse whitespace before asserting on the text.
        var raw = await stdoutTask + "\n" + await stderrTask;
        var text = Regex.Replace(raw, @"\x1B\[[0-9;?]*[ -/]*[@-~]", string.Empty);
        text = Regex.Replace(text, @"\s+", " ");
        return (process.ExitCode, text);
    }

    private static string ResolveDotnetHost()
    {
        var fromCli = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(fromCli) && File.Exists(fromCli))
        {
            return fromCli;
        }

        return FindOnPath(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
            ?? throw new InvalidOperationException("The dotnet host was not found (DOTNET_HOST_PATH or PATH).");
    }

    private static string ResolveGit() =>
        FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git")
        ?? throw new InvalidOperationException("git was not found on PATH.");

    // Only fully-qualified PATH entries, like GitExecutableLocator.
    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var rawEntry in path.Split(Path.PathSeparator))
        {
            var entry = rawEntry.Trim().Trim('"');
            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry)) continue;
            var candidate = Path.Combine(entry, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static async Task RunGitAsync(string workingDirectory, string[] arguments, bool allowFailure = false)
    {
        var (exitCode, output) = await RunGitWithExitCodeAsync(workingDirectory, arguments);
        if (exitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
    }

    // Test-side git without GIT_NO_LAZY_FETCH: the positive control shows what
    // git does without the variable, so the refusal is what the test proves.
    private static async Task<(int ExitCode, string Output)> RunGitWithExitCodeAsync(string workingDirectory, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveGit(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment.Remove("GIT_NO_LAZY_FETCH");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stderrTask + await stdoutTask);
    }
}
