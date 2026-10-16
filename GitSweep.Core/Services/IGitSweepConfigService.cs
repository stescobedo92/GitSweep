using GitSweep.Core.Models;

namespace GitSweep.Core.Services;

/// <summary>
/// Carga <c>.gitsweep.json</c> (solo JSON vía <see cref="System.Text.Json"/>).
/// Sin fichero devuelve <see cref="GitSweepConfig.Default"/>. Un fichero
/// presente pero inválido lanza (<see cref="InvalidOperationException"/> o
/// <see cref="ArgumentException"/>): el llamador debe fallar cerrado y no
/// borrar nada, porque la protección configurada no se pudo verificar.
/// La lectura es estricta: una propiedad desconocida, mal escrita o duplicada
/// también es inválida (un error tipográfico no puede degradarse en silencio a
/// «sin protección configurada»). El fichero solo añade patrones; la
/// protección integrada de <c>main</c>/<c>master</c> se aplica siempre.
/// </summary>
public interface IGitSweepConfigService
{
    Task<GitSweepConfig> LoadAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Ruta del fichero buscado para un repositorio (raíz del working tree si se puede resolver).</summary>
    Task<string> GetConfigFilePathAsync(string repoPath, CancellationToken ct = default);
}
