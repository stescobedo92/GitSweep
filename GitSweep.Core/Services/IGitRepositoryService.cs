using GitSweep.Core.Models;

namespace GitSweep.Core.Services;

public interface IGitRepositoryService
{
    Task<bool> IsGitRepositoryAsync(string path, CancellationToken ct = default);
    Task<string> GetDefaultBranchNameAsync(string path, CancellationToken ct = default);
    Task<List<BranchInfo>> GetLocalBranchesAsync(string path, string targetBranch, CancellationToken ct = default);
    Task<(bool Success, string ErrorMessage)> DeleteBranchAsync(string path, string branchName, CancellationToken ct = default);
}
