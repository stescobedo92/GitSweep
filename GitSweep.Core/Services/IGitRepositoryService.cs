using GitSweep.Core.Models;

namespace GitSweep.Core.Services;

public interface IGitRepositoryService
{
    Task<bool> IsGitRepositoryAsync(string path, CancellationToken ct = default);
    Task<string> GetDefaultBranchNameAsync(string path, CancellationToken ct = default);
    Task<List<BranchInfo>> GetLocalBranchesAsync(string path, string targetBranch, CancellationToken ct = default);
    Task<(bool Success, string ErrorMessage)> DeleteBranchAsync(string path, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Fetches from the resolved <c>origin</c> URL with one explicit refspec
    /// (<c>+refs/heads/*:refs/remotes/origin/*</c>, no shell,
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>) plus
    /// <c>--prune --no-tags</c>, so remote-tracking refs deleted on the
    /// remote are removed locally and their local branches become
    /// detectable as upstream-gone. Fetching by URL (never by remote name)
    /// ignores every repository-configured refspec, so a hostile
    /// <c>remote.origin.fetch</c> cannot make the prune move or delete a
    /// local branch; no local branch or tag is ever created, moved, deleted
    /// or force-updated. Without an <c>origin</c> remote it fails closed.
    /// A failure is reported (never hidden) so callers can fail closed and
    /// refuse to classify gone branches from stale remote state. Transport
    /// helpers named by repository config (ssh/proxy/credential programs,
    /// the <c>ext::</c> transport, any non-standard remote-helper transport)
    /// are never executed, no interactive prompt is ever shown, and the
    /// reported message has credentials redacted.
    /// </summary>
    Task<(bool Success, string ErrorMessage)> FetchPruneAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Detects whether <paramref name="branchName"/> was merged into
    /// <paramref name="targetBranch"/> by squash or rebase, a case that
    /// <c>git branch --merged</c> cannot see because the branch tip is not an
    /// ancestor of the target. The check is purely local (merge-base,
    /// commit-tree and cherry patch equivalence); no hosting API is used.
    /// Returns <see langword="false"/> for ancestor-merged branches (those are
    /// reported by <see cref="GetLocalBranchesAsync"/> as
    /// <see cref="BranchMergeKind.Ancestor"/>), for branches with unique
    /// changes, and for invalid inputs.
    /// </summary>
    Task<bool> IsSquashOrRebaseMergedAsync(string path, string targetBranch, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Mide las señales del semáforo de riesgo de <paramref name="branchName"/>
    /// frente a <paramref name="targetBranch"/> solo con git local:
    /// merge real (ancestro o squash/rebase), commits únicos por parche
    /// (<c>git cherry</c>) y commits sin subir según el upstream.
    /// Entradas inválidas o errores de git devuelven
    /// <see cref="BranchRiskSignals.Unknown"/> (conteos nulos), nunca cero.
    /// </summary>
    Task<BranchRiskSignals> GetBranchRiskSignalsAsync(string path, string targetBranch, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Resolves the absolute path of the shared git directory
    /// (<c>git rev-parse --git-common-dir</c>), or <see langword="null"/> when
    /// <paramref name="path"/> is not inside a repository. The common dir is
    /// used so every worktree shares one trash under
    /// <c>.git/gitsweep/trash</c>.
    /// </summary>
    Task<string?> GetGitCommonDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns the full hex SHA of the tip commit of
    /// <paramref name="branchName"/>, or <see langword="null"/> when the
    /// branch does not exist or the name is invalid. Never throws for a
    /// missing branch.
    /// </summary>
    Task<string?> GetBranchTipShaAsync(string path, string branchName, CancellationToken ct = default);

    /// <summary>Returns whether a local branch exists. Invalid names return <see langword="false"/>.</summary>
    Task<bool> BranchExistsAsync(string path, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Creates a local branch pointing at <paramref name="commitSha"/> with
    /// plain <c>git branch</c> (never <c>-f</c>/<c>-D</c>): it fails instead
    /// of overwriting when the branch already exists. Both the name and the
    /// SHA are validated before git is invoked.
    /// </summary>
    Task<(bool Success, string ErrorMessage)> CreateBranchAsync(string path, string branchName, string commitSha, CancellationToken ct = default);

    /// <summary>Rama actualmente en checkout en <paramref name="path"/> (<c>git branch --show-current</c>), o <see langword="null"/> si HEAD está detached o no es un repo.</summary>
    Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default);

    /// <summary>Ramas en checkout en cualquier worktree (<c>git worktree list --porcelain</c>), sin duplicados. Vacío si no es un repo.</summary>
    Task<IReadOnlyList<string>> GetWorktreeBranchesAsync(string path, CancellationToken ct = default);

    /// <summary>Raíz del working tree (<c>git rev-parse --show-toplevel</c>), o <see langword="null"/> si no es un repo.</summary>
    Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default);
}
