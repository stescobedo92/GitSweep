namespace GitSweep.Infrastructure.Services;

/// <summary>
/// El repositorio analizado es un <em>clon parcial</em> (promisor): le faltan
/// objetos que git trae bajo demanda del remoto mediante un «lazy fetch». Ese
/// fetch perezoso ejecuta el programa de <c>remote.&lt;promisor&gt;.uploadpack</c>
/// configurado por el repositorio a través del shell, así que cualquier comando
/// que lea un objeto ausente (<c>git cherry</c> en la detección de squash/rebase,
/// <c>git branch &lt;rama&gt; &lt;sha&gt;</c> al restaurar desde la papelera...)
/// podría ejecutar código de un clon hostil incluso sin <c>--fetch</c>.
/// <c>GIT_NO_LAZY_FETCH=1</c> lo impide, pero solo en versiones de git que lo
/// respetan; para cerrarlo en cualquier versión, el ejecutor compartido de
/// <see cref="GitRepositoryService"/> detecta el clon parcial con un
/// <c>git config</c> de solo lectura antes de cualquier otro comando y lanza
/// esta excepción (fallar cerrado es preferible a una cobertura parcial).
/// También se lanza, con un mensaje distinto («Could not determine whether…»),
/// cuando la configuración no se puede leer y no hay forma de confirmar que el
/// repositorio NO es un clon parcial.
/// </summary>
/// <remarks>
/// Deriva directamente de <see cref="Exception"/> a propósito, igual que
/// <see cref="GitCommandLimitException"/>: ningún <c>catch</c> de la CLI que
/// degrada <see cref="IOException"/>/<see cref="InvalidOperationException"/>/
/// <see cref="ArgumentException"/> a un valor permisivo puede tragársela, de
/// modo que llega al manejador global de la CLI, que imprime el mensaje en
/// inglés y termina con código 1 sin borrar, restaurar ni escanear nada.
/// </remarks>
public sealed class PartialCloneNotSupportedException : Exception
{
    public PartialCloneNotSupportedException(string message)
        : base(message)
    {
    }
}
