using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// OBS-02 / OBS-03: the git child gets a PATH made only of fully-qualified
/// entries (an empty or relative entry would let git resolve helper
/// programs such as <c>ssh</c> inside the possibly-hostile repository it
/// runs in) and never inherits <c>GIT_EXEC_PATH</c> (which would make git
/// load its sub-programs from an arbitrary directory). The helper is
/// exercised with the platform parameterized, like the Task 2 tests.
/// </summary>
public sealed class GitChildEnvironmentTests
{
    // Same host-aware absolute directories as GitExecutableLocatorTests: UNC
    // on Windows (no ':' so it can also be joined with ':'), rooted on Unix.
    private static string Abs(string name) =>
        OperatingSystem.IsWindows()
            ? @"\\gitsweep-test\share\" + name
            : "/gitsweep-test/" + name;

    private static string Join(bool isWindows, params string[] entries) =>
        string.Join(isWindows ? ';' : ':', entries);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Child_PATH_is_reduced_to_its_fully_qualified_entries(bool isWindows)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Join(isWindows, ".", Abs("a"), "", "tools", @".\bin", "   ", Abs("b"), "C:drive-relative", @"\rooted")
        };

        GitRepositoryService.ApplyHardenedEnvironment(environment, isWindows);

        Assert.Equal(Join(isWindows, Abs("a"), Abs("b")), environment["PATH"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Child_PATH_without_any_fully_qualified_entry_is_removed_not_left_empty(bool isWindows)
    {
        // POSIX execvp treats an empty PATH (or an empty entry) as the
        // current directory, so an empty value would reopen the hole.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Join(isWindows, ".", "", "tools")
        };

        GitRepositoryService.ApplyHardenedEnvironment(environment, isWindows);

        Assert.False(environment.ContainsKey("PATH"));
    }

    [Fact]
    public void Windows_PATH_variable_is_matched_case_insensitively()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Path"] = Join(true, ".", Abs("a"), "tools")
        };

        GitRepositoryService.ApplyHardenedEnvironment(environment, isWindows: true);

        Assert.Equal(Abs("a"), environment["Path"]);
    }

    [Fact]
    public void Inherited_git_variables_are_removed_and_safe_defaults_are_forced()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_EXEC_PATH"] = Abs("planted-exec-path"),
            ["GIT_DIR"] = Abs("other-repo"),
            ["GIT_SSH_COMMAND"] = "planted-ssh",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.fsmonitor",
            ["GIT_TRACE2"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "1",
            ["GITSWEEP_UNRELATED"] = "kept"
        };

        GitRepositoryService.ApplyHardenedEnvironment(environment, OperatingSystem.IsWindows());

        Assert.False(environment.ContainsKey("GIT_EXEC_PATH"));
        Assert.False(environment.ContainsKey("GIT_DIR"));
        Assert.False(environment.ContainsKey("GIT_SSH_COMMAND"));
        Assert.False(environment.ContainsKey("GIT_CONFIG_COUNT"));
        Assert.False(environment.ContainsKey("GIT_CONFIG_KEY_0"));
        Assert.False(environment.ContainsKey("GIT_TRACE2"));
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("cat", environment["GIT_PAGER"]);
        // GIT_NO_LAZY_FETCH=1 stops a partial clone's lazy fetch from running
        // the repo-configured promisor uploadpack (OBS-1) on git versions that
        // honor it; GitSweep also refuses partial clones outright for the rest.
        Assert.Equal("1", environment["GIT_NO_LAZY_FETCH"]);
        Assert.Equal("kept", environment["GITSWEEP_UNRELATED"]);
    }
}

/// <summary>
/// Tests that change this process's environment. They run alone (no other
/// test runs in parallel with this collection) so no concurrent git child
/// can observe the planted variables.
/// </summary>
[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class GitExecPathInheritanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));

    public GitExecPathInheritanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task GIT_EXEC_PATH_set_in_the_parent_does_not_reach_the_git_child()
    {
        var planted = Path.Combine(_root, "planted-exec-path");
        Directory.CreateDirectory(planted);
        var original = Environment.GetEnvironmentVariable("GIT_EXEC_PATH");
        try
        {
            Environment.SetEnvironmentVariable("GIT_EXEC_PATH", planted);

            // Control: a git child that inherits the variable reports it, so
            // `git --exec-path` really observes GIT_EXEC_PATH.
            var inherited = await RunInheritingGitAsync(_root, "--exec-path");
            var hardened = await GitRepositoryService.RunGitProcessAsync(
                _root, ["--exec-path"], TimeSpan.FromMinutes(1), 1024 * 1024, 1024 * 1024, CancellationToken.None);

            Assert.Equal(Normalize(planted), Normalize(inherited));
            Assert.Equal(0, hardened.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(hardened.Output));
            Assert.NotEqual(Normalize(planted), Normalize(hardened.Output));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_EXEC_PATH", original);
        }
    }

    private static string Normalize(string path) =>
        path.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static async Task<string> RunInheritingGitAsync(string workingDirectory, params string[] arguments)
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
        _ = await stderrTask;
        return stdout;
    }
}
