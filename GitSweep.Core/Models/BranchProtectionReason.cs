namespace GitSweep.Core.Models;

/// <summary>Motivo por el que una rama está protegida y nunca se borra.</summary>
public enum BranchProtectionReason
{
    None = 0,
    GlobPattern,
    CurrentBranch,
    WorktreeBranch,
}

/// <summary>Resultado de evaluar la protección de una rama.</summary>
/// <param name="BranchName">Nombre de la rama evaluada.</param>
/// <param name="Reason">Motivo de protección o <see cref="BranchProtectionReason.None"/>.</param>
/// <param name="MatchedPattern">Patrón glob que coincidió, cuando <paramref name="Reason"/> es glob.</param>
public sealed record BranchProtectionResult(string BranchName, BranchProtectionReason Reason, string? MatchedPattern = null)
{
    public bool IsProtected => Reason != BranchProtectionReason.None;
    public static BranchProtectionResult NotProtected(string branchName) => new(branchName, BranchProtectionReason.None);
}
