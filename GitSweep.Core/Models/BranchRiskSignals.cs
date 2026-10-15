namespace GitSweep.Core.Models;

/// <summary>
/// Señales medidas con git local para el semáforo de riesgo de una rama.
/// Un conteo <see langword="null"/> significa «desconocido»: nunca se trata
/// como cero, para clasificar de forma conservadora (fail-closed).
/// </summary>
/// <param name="UniqueCommitCount">
/// Commits de la rama cuyo parche no está en la rama destino
/// (<c>git cherry</c> con «+»); 0 cuando el merge es real (ancestro o
/// squash/rebase), porque el parche acumulado ya está en el destino.
/// </param>
/// <param name="UnpushedCommitCount">
/// Commits no presentes en el upstream. Sin upstream es el número de
/// commits que no están en el destino: todo ese trabajo solo existe local.
/// </param>
/// <param name="HasUpstream">Si la rama tiene upstream configurado y resoluble.</param>
/// <param name="IsReallyMerged">
/// Merge real en el destino: ancestro (<c>git branch --merged</c>) o
/// squash/rebase detectado por equivalencia de parche.
/// </param>
/// <param name="IsUpstreamGone">
/// El upstream está configurado pero su ref de seguimiento ya no existe
/// (<c>[gone]</c> tras <c>git fetch --prune</c>). Por sí solo nunca hace
/// segura una rama: solo lo es si además hay merge real y cero únicos.
/// </param>
public record BranchRiskSignals(
    int? UniqueCommitCount,
    int? UnpushedCommitCount,
    bool HasUpstream,
    bool IsReallyMerged,
    bool IsUpstreamGone = false)
{
    /// <summary>Señales desconocidas: el clasificador nunca devuelve «segura» con esto.</summary>
    public static BranchRiskSignals Unknown { get; } = new(null, null, false, false);
}
