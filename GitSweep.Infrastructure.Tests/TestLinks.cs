using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// Helpers for the security tests that plant symbolic links (or Windows
/// junctions) in a temp repository and assert GitSweep never follows them.
/// </summary>
internal static class TestLinks
{
    private static readonly Lazy<string> FileSymlinkProblem = new(() => Probe(directory: false));
    private static readonly Lazy<string> DirectoryLinkProblem = new(() => Probe(directory: true));

    /// <summary>Why file symlinks cannot be created here, or null when they can.</summary>
    public static string FileSymlinkUnavailableReason => FileSymlinkProblem.Value;

    /// <summary>Why directory links cannot be created here, or null when they can.</summary>
    public static string DirectoryLinkUnavailableReason => DirectoryLinkProblem.Value;

    public static void CreateFileSymlink(string linkPath, string targetPath) =>
        File.CreateSymbolicLink(linkPath, targetPath);

    /// <summary>
    /// Creates a directory symlink; on Windows falls back to a junction (also
    /// a reparse point, and it needs no extra privilege). Throws when no kind
    /// of link can be created: callers are guarded by
    /// <see cref="DirectoryLinkFactAttribute"/>, so a failure here is a real
    /// failure, never a reason to silently pass.
    /// </summary>
    public static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            if (!TryCreateJunction(linkPath, targetPath))
            {
                throw new IOException($"Cannot create a symbolic link or junction at '{linkPath}'.");
            }
        }
    }

    /// <summary>
    /// Creates a Windows directory junction (never a symlink): the link kind
    /// any unprivileged user can create, and the one a hostile archive
    /// would ship. Throws when it cannot be created or off Windows.
    /// </summary>
    public static void CreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows() || !TryCreateJunction(linkPath, targetPath))
        {
            throw new IOException($"Cannot create a junction at '{linkPath}'.");
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Tries to create a link in a scratch directory. Runs once, at test
    /// discovery, so a machine that cannot create links gets its link tests
    /// reported as skipped instead of silently passing.
    /// </summary>
    private static string Probe(bool directory)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitSweepLinkProbe", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "target");
            var link = Path.Combine(root, "link");
            if (directory)
            {
                Directory.CreateDirectory(target);
                CreateDirectoryLink(link, target);
            }
            else
            {
                File.WriteAllText(target, "probe");
                CreateFileSymlink(link, target);
            }

            return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0
                ? null
                : "the created link is not reported as a reparse point";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // Best effort: the probe lives under the system temp dir.
            }
        }
    }
}

/// <summary>
/// A <c>[Fact]</c> that needs file symbolic links. xUnit v2 has no runtime
/// skip, so the capability is probed when the attribute is constructed
/// (during discovery) and the test is reported as skipped, with the reason,
/// when links cannot be created (e.g. Windows without Developer Mode).
/// </summary>
internal sealed class FileSymlinkFactAttribute : FactAttribute
{
    public FileSymlinkFactAttribute()
    {
        var reason = TestLinks.FileSymlinkUnavailableReason;
        if (reason is not null)
        {
            Skip = "Cannot create file symbolic links on this machine (" + reason + ").";
        }
    }
}

/// <summary>
/// A <c>[Fact]</c> that needs directory links (symlink, or junction on
/// Windows). Skipped, with the reason, when neither can be created.
/// </summary>
internal sealed class DirectoryLinkFactAttribute : FactAttribute
{
    public DirectoryLinkFactAttribute()
    {
        var reason = TestLinks.DirectoryLinkUnavailableReason;
        if (reason is not null)
        {
            Skip = "Cannot create directory symbolic links or junctions on this machine (" + reason + ").";
        }
    }
}

/// <summary>
/// A <c>[Theory]</c> that needs directory links; skipped like
/// <see cref="DirectoryLinkFactAttribute"/> when none can be created.
/// </summary>
internal sealed class DirectoryLinkTheoryAttribute : TheoryAttribute
{
    public DirectoryLinkTheoryAttribute()
    {
        var reason = TestLinks.DirectoryLinkUnavailableReason;
        if (reason is not null)
        {
            Skip = "Cannot create directory symbolic links or junctions on this machine (" + reason + ").";
        }
    }
}

/// <summary>
/// A <c>[Fact]</c> that needs a case-sensitive file system (so that
/// <c>Feature</c> and <c>feature</c> can coexist). Skipped, with the reason,
/// on a case-insensitive file system (Windows/macOS), where the scenario the
/// test guards cannot be built with loose refs.
/// </summary>
internal sealed class CaseSensitiveFileSystemFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> CaseSensitive = new(Probe);

    public CaseSensitiveFileSystemFactAttribute()
    {
        if (!CaseSensitive.Value)
        {
            Skip = "Requires a case-sensitive file system (Feature vs feature cannot coexist here).";
        }
    }

    private static bool Probe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GitSweepCaseProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "probe"), "x");
            return !File.Exists(Path.Combine(dir, "PROBE"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }
}
