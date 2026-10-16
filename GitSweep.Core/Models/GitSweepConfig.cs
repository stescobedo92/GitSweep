namespace GitSweep.Core.Models;

/// <summary>
/// Configuración de protección cargada desde <c>.gitsweep.json</c> en la raíz
/// del working tree. Solo JSON (<see cref="System.Text.Json"/>); nunca YAML.
/// El fichero es entrada no confiable y solo puede <b>añadir</b> protección:
/// <c>main</c> y <c>master</c> (<see cref="BuiltInProtectedBranches"/>) quedan
/// siempre protegidas por nombre, con o sin fichero y con cualquier contenido,
/// porque <c>IBranchProtectionService.Evaluate</c> une esos nombres a
/// <see cref="ProtectedBranches"/> en toda instancia (cargada, <see cref="Default"/>,
/// <see cref="Empty"/> o construida a mano). No existe ajuste para quitarlas.
/// </summary>
/// <param name="ProtectedBranches">
/// Patrones glob <b>adicionales</b> de ramas que nunca se borran (p. ej. <c>release/*</c>).
/// No incluye <see cref="BuiltInProtectedBranches"/>, que siempre se aplican aparte; una lista
/// vacía significa «sin patrones extra», nunca «sin protección».
/// </param>
/// <param name="ExcludeCurrentBranch">Si es <see langword="true"/> (defecto), la rama actual nunca es candidata.</param>
/// <param name="ExcludeWorktreeBranches">Si es <see langword="true"/> (defecto), las ramas en worktrees nunca son candidatas.</param>
public sealed record GitSweepConfig(
    IReadOnlyList<string> ProtectedBranches,
    bool ExcludeCurrentBranch = true,
    bool ExcludeWorktreeBranches = true)
{
    public const string FileName = ".gitsweep.json";
    public const int MaxProtectedPatterns = 100;

    /// <summary>
    /// Nombres de rama protegidos siempre (<c>main</c> y <c>master</c>), con la misma
    /// comparación insensible a mayúsculas que los patrones configurados. Se aplican
    /// además de <see cref="ProtectedBranches"/> y no pueden desactivarse desde el fichero.
    /// </summary>
    public static IReadOnlyList<string> BuiltInProtectedBranches { get; } = ["main", "master"];

    /// <summary>
    /// Configuración usada cuando no existe <c>.gitsweep.json</c>: sin patrones extra
    /// (<see cref="ProtectedBranches"/> vacío) y con la rama actual y las ramas de worktrees
    /// excluidas. Protege <c>main</c> y <c>master</c> igual que cualquier otra configuración,
    /// porque esa protección procede de <see cref="BuiltInProtectedBranches"/> y no de esta lista.
    /// </summary>
    public static GitSweepConfig Default { get; } = new([]);

    /// <summary>
    /// Configuración sin patrones glob extra: <see cref="ProtectedBranches"/> vacío y
    /// <see cref="ExcludeCurrentBranch"/> / <see cref="ExcludeWorktreeBranches"/> en <see langword="true"/>.
    /// Una lista vacía significa «sin glob extra», nunca «sin protección»: <c>main</c> y <c>master</c>
    /// siguen protegidas por <see cref="BuiltInProtectedBranches"/>, así que esta instancia NO deja
    /// borrables esas ramas. Es equivalente a <see cref="Default"/> y sirve para expresar de forma explícita
    /// «solo protección integrada» (p. ej. en pruebas), no para aislar ni desactivar protección.
    /// </summary>
    public static GitSweepConfig Empty { get; } = new([]);
}
