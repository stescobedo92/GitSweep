using GitSweep.Core.Models;
using GitSweep.Core.Services;
using System.Diagnostics;

namespace GitSweep.Infrastructure.Services;

public class GitRepositoryService : IGitRepositoryService
{
    public async Task<bool> IsGitRepositoryAsync(string path, CancellationToken ct = default)
    {
        var result = await ExecuteGitCommandAsync(path, ["rev-parse", "--git-dir"], ct);
        return result.ExitCode == 0;
    }

    public async Task<string> GetDefaultBranchNameAsync(string path, CancellationToken ct = default)
    {
        // First, try to get the remote default branch
        var remoteResult = await ExecuteGitCommandAsync(path, ["symbolic-ref", "refs/remotes/origin/HEAD"], ct);
        if (remoteResult.ExitCode == 0)
        {
            return remoteResult.Output.Replace("refs/remotes/origin/", string.Empty).Trim();
        }

        // Fallback to local HEAD
        var localResult = await ExecuteGitCommandAsync(path, ["symbolic-ref", "--short", "HEAD"], ct);
        return localResult.ExitCode == 0 ? localResult.Output.Trim() : "main";
    }

    public async Task<List<BranchInfo>> GetLocalBranchesAsync(string path, string targetBranch, CancellationToken ct = default)
    {
        var branches = new List<BranchInfo>();

        // Get merged branches
        var mergedResult = await ExecuteGitCommandAsync(path, ["branch", "--merged", targetBranch], ct);
        var mergedBranchNames = ParseBranchList(mergedResult.Output);

        // Get all local branches with their last commit date
        var allBranchesResult = await ExecuteGitCommandAsync(
            path,
            ["for-each-ref", "--sort=-committerdate", "--format=%(refname:short)|%(committerdate:iso8601)", "refs/heads/"],
            ct
        );

        foreach (var line in allBranchesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length != 2) continue;

            var name = parts[0];
            var date = DateTime.TryParse(parts[1], out var commitDate) ? commitDate : (DateTime?)null;

            // Skip the target branch itself
            if (name.Equals(targetBranch, StringComparison.OrdinalIgnoreCase)) continue;

            branches.Add(new BranchInfo(name, date, mergedBranchNames.Contains(name)));
        }

        return branches;
    }

    private static HashSet<string> ParseBranchList(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('*', ' '))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> ExecuteGitCommandAsync(string workingDirectory, string[] arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = string.Join(" ", arguments),
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, output);
    }
}
