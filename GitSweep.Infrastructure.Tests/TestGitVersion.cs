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
