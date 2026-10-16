using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// Helpers for the tests that plant a FIFO (named pipe) where GitSweep expects
/// a regular file. A FIFO reports length 0 and opening it for reading blocks
/// until a writer shows up, so code that only size-checks the file and then
/// opens it hangs forever.
/// </summary>
internal static class TestFifo
{
    // No .NET API creates a FIFO, so the tests use the system mkfifo through
    // an absolute path (no PATH lookup, no shell).
    public const string MkfifoPath = "/usr/bin/mkfifo";

    // A correct implementation answers in milliseconds; this only bounds how
    // long a regression keeps a run busy before the test fails.
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(10);

    public static bool IsSupported => !OperatingSystem.IsWindows() && File.Exists(MkfifoPath);

    public static void Create(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = MkfifoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {MkfifoPath}.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{MkfifoPath} failed with exit code {process.ExitCode}: {error}");
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a pool thread and fails the test if
    /// it has not finished within the timeout. The pool thread matters: the
    /// code under test opens files synchronously before its first real await,
    /// so a regression would otherwise block the test thread itself and the
    /// timeout could never fire.
    /// </summary>
    public static async Task<T> CompleteWithinTimeoutAsync<T>(string fifoPath, Func<Task<T>> operation)
    {
        var work = Task.Run(operation);
        if (await Task.WhenAny(work, Task.Delay(HangTimeout)) != work)
        {
            ReleaseBlockedReader(fifoPath);
            throw new XunitException(
                $"The operation was still blocked after {HangTimeout.TotalSeconds:0} s on a FIFO: " +
                "it opened the pipe instead of rejecting it unread.");
        }

        return await work;
    }

    // Opening a FIFO for writing completes the reader's pending open, so the
    // regression does not leave a pool thread stuck for the rest of the run.
    private static void ReleaseBlockedReader(string fifoPath) =>
        _ = Task.Run(() =>
        {
            try
            {
                using var writer = new FileStream(fifoPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (Exception)
            {
                // Best effort: the test has already failed.
            }
        });
}

/// <summary>
/// A <c>[Fact]</c> that needs a real FIFO: skipped, with the reason, on
/// Windows (no FIFOs in the file system) or when mkfifo is missing.
/// </summary>
internal sealed class FifoFactAttribute : FactAttribute
{
    public FifoFactAttribute()
    {
        if (!TestFifo.IsSupported)
        {
            Skip = OperatingSystem.IsWindows()
                ? "FIFOs (named pipes in the file system) do not exist on Windows."
                : $"{TestFifo.MkfifoPath} is not available on this machine.";
        }
    }
}
