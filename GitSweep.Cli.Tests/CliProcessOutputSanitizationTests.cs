using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GitSweep.Cli.Commands;
using Xunit;

namespace GitSweep.Cli.Tests;

/// <summary>
/// SEC-B02, end to end through the real CLI process (<c>gitsweep-cli.dll</c>,
/// with Program.cs's forced ANSI/interactive console): the global exception
/// handler prints exception messages, which can quote external text (here
/// Spectre.Console.Cli quotes the raw <c>--age</c> value it failed to
/// convert), and restore prints the repository path. The terminal must
/// receive the visible escapes, never the raw ESC/BEL sequences. The negative
/// checks run on the raw stdout bytes, so a leaked sequence cannot be hidden.
/// </summary>
public sealed class CliProcessOutputSanitizationTests
{
    [Fact]
    public async Task Global_error_handler_prints_control_characters_of_the_message_as_visible_escapes()
    {
        var (exitCode, stdout) = await RunCliAsync(["clean", "--age", "x\u001b[2J\u001b]0;PWNED\u0007"]);

        Assert.True(exitCode == 1, $"expected exit 1, got {exitCode}. Output:\n{stdout}");
        Assert.Contains("Error:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[2J", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b]0;PWNED", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', stdout);
        Assert.Contains(@"x\x1B[2J\x1B]0;PWNED\x07", Flatten(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_prints_a_hostile_repository_path_as_visible_escapes()
    {
        var path = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"), "no-repo\u001b]0;PWNED\u0007");

        var (exitCode, stdout) = await RunCliAsync(["restore", "-p", path, "--all", "--yes"]);

        Assert.True(exitCode == 1, $"expected exit 1, got {exitCode}. Output:\n{stdout}");
        Assert.Contains("is not a Git repository", Flatten(stdout), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b]0;PWNED", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', stdout);
        Assert.Contains(@"no-repo\x1B]0;PWNED\x07", Flatten(stdout), StringComparison.Ordinal);
    }

    // For the positive "contains" checks only: Spectre wraps long lines at the
    // console width and re-applies the color on each wrapped line, so drop
    // line breaks and SGR color sequences (ESC [ ... m). Never used for the
    // "does not contain" checks, which run on the raw bytes.
    private static string Flatten(string output) =>
        Regex.Replace(output, "\u001b\\[[0-9;]*m|\r|\n", string.Empty);

    // Runs the real CLI (same build output as this test) through the dotnet
    // host, so Program.cs's console setup and global handler are exercised.
    private static async Task<(int ExitCode, string Stdout)> RunCliAsync(string[] arguments)
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
