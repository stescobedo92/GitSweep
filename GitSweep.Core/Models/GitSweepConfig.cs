namespace GitSweep.Core.Models;

/// <summary>
/// Configuración de protección cargada desde <c>.gitsweep.json</c> en la raíz
/// del working tree. Solo JSON (<see cref="System.Text.Json"/>); nunca YAML.
/// </summary>
/// <param name="ProtectedBranches">Patrones glob de ramas que nunca se borran (p. ej. <c>release/*</c>).</param>
/// <param name="ExcludeCurrentBranch">Si es <see langword="true"/> (defecto), la rama actual nunca es candidata.</param>
/// <param name="ExcludeWorktreeBranches">Si es <see langword="true"/> (defecto), las ramas en worktrees nunca son candidatas.</param>
public sealed record GitSweepConfig(
    IReadOnlyList<string> ProtectedBranches,
    bool ExcludeCurrentBranch = true,
    bool ExcludeWorktreeBranches = true)
{
    public const string FileName = ".gitsweep.json";
    public const int MaxProtectedPatterns = 100;

    /// <summary>Configuración por defecto cuando no existe <c>.gitsweep.json</c>.</summary>
    public static GitSweepConfig Default { get; } = new(["main", "master"]);

    /// <summary>Patrones efectivos: si la lista está vacía se usan los defectos <c>main</c>/<c>master</c>? No: vacío significa sin glob extra.</summary>
    public static GitSweepConfig Empty { get; } = new([]);
}
