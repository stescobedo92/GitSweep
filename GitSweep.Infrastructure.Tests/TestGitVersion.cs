#nullable enable

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// Version of the git that the tests (and GitSweep) run, probed once.
/// </summary>
internal static class TestGitVersion
{
    private static readonly Lazy<Version?> Installed = new(Probe);

    public static Version? Current => Installed.Value;

    private static Version? Probe()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = GitExecutableLocator.GetRequiredPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // "git version 2.55.0.windows.5", "git version 2.43.0"
            var match = Regex.Match(output, @"(\d+)\.(\d+)");
            return match.Success
                ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// A <c>[Fact]</c> that needs the protocol v2 <c>promisor-remote</c>
/// capability (git 2.49+): an older git never advertises or accepts promisor
/// remotes, so the test's positive control could not fire. xUnit v2 has no
/// runtime skip, so the version is probed at discovery and the test is
/// reported as skipped, with the reason, on an older git.
/// </summary>
internal sealed class PromisorRemoteCapabilityFactAttribute : FactAttribute
{
    private static readonly Version Required = new(2, 49);

    public PromisorRemoteCapabilityFactAttribute()
    {
        var current = TestGitVersion.Current;
        if (current is null || current < Required)
        {
            Skip = $"git {current?.ToString() ?? "(unknown version)"} has no promisor-remote protocol capability (needs git {Required}+).";
        }
    }
}

/// <summary>
/// Whether this git runs hooks defined in configuration
/// (<c>hook.&lt;name&gt;.command</c> + <c>hook.&lt;name&gt;.event</c>): by an exact
/// version check (git &gt;= 2.54.0, where the feature shipped), not a probe.
/// git 2.43 (the default Linux container) ignores those keys.
/// </summary>
internal static class TestConfigHooks
{
    // Configuration-defined hooks shipped in git 2.54.0 (release notes:
    // "Hook commands are now allowed to be defined ... in the configuration
    // files"). An exact version check (RR-7): a behavioral probe through
    // `git hook run` misreads a git older than 2.36, which has no `hook`
    // subcommand, as supporting them.
    private static readonly Version Required = new(2, 54);

    public static bool IsSupported => TestGitVersion.Current is { } current && current >= Required;
}

/// <summary>
/// Whether fetching by URL honors a <c>[remote "&lt;url&gt;"] vcs</c> section,
/// probed once: git 2.55 runs the named remote helper, git 2.43 stops with
/// "has no configured URL" before any helper runs.
/// </summary>
internal sealed class UrlScopedVcsFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Honored = new(Probe);

    public UrlScopedVcsFactAttribute()
    {
        if (!Honored.Value)
        {
            Skip = $"git {TestGitVersion.Current?.ToString() ?? "(unknown version)"} does not apply remote.<url>.vcs when fetching by URL, so no helper can run.";
        }
    }

    private static bool Probe()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GitSweepVcsProbe", Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(root);
            var startInfo = new ProcessStartInfo
            {
                FileName = GitExecutableLocator.GetRequiredPath(),
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-c", "protocol.foo.allow=always", "-c", "alias.remote-foo=!exit 1",
                         "-c", "remote.https://127.0.0.1:1/probe.git.vcs=foo",
                         "ls-remote", "https://127.0.0.1:1/probe.git"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return stderr.Contains("remote helper", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }
}
