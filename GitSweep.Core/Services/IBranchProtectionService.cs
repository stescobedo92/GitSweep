using GitSweep.Core.Models;

namespace GitSweep.Core.Services;

/// <summary>
/// Decide si una rama está protegida por nombre integrado (<c>main</c> y
/// <c>master</c>, <see cref="GitSweepConfig.BuiltInProtectedBranches"/>), por
/// glob (<c>.gitsweep.json</c>), por ser la rama actual o por estar en un
/// worktree. La protección integrada se une siempre a los patrones configurados
/// (nunca los sustituye ni se puede desactivar), así que toda
/// <see cref="GitSweepConfig"/> protege <c>main</c> y <c>master</c>. Fail-closed:
/// un nombre inválido se considera protegido (nunca borrable).
/// </summary>
public interface IBranchProtectionService
{
    BranchProtectionResult Evaluate(
        string branchName,
        GitSweepConfig config,
        string? currentBranch,
        IReadOnlyCollection<string>? worktreeBranches);

    IReadOnlyList<BranchProtectionResult> FilterProtected(
        IEnumerable<BranchInfo> branches,
        GitSweepConfig config,
        string? currentBranch,
        IReadOnlyCollection<string>? worktreeBranches,
        out List<BranchInfo> unprotected);
}
