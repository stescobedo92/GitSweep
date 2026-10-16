namespace GitSweep.Infrastructure.Services;

/// <summary>
/// La configuración del repositorio analizado define un <em>hook por
/// configuración</em> (<c>hook.&lt;nombre&gt;.command</c> + <c>.event</c>, en
/// git &gt;= 2.54) para un evento que alguno de los comandos de GitSweep puede
/// disparar (por ejemplo <c>reference-transaction</c> en cada actualización de
/// ref, o <c>pre-auto-gc</c> tras un fetch). Esos hooks ejecutan un programa
/// elegido por <c>.git/config</c> y <c>core.hooksPath</c> no los gobierna, así
/// que el ejecutor compartido de <see cref="GitRepositoryService"/> enumera los
/// eventos configurados con un <c>git config</c> de solo lectura y rechaza el
/// repositorio antes de cualquier comando cuando alguno no está en una lista
/// blanca de eventos que GitSweep nunca dispara (denegar por defecto: un evento
/// desconocido o futuro también rechaza). Fallar cerrado es preferible a
/// depender de una clave <c>hook.&lt;evento&gt;.enabled</c> cuyo efecto cambia
/// entre versiones de git (RG-01/RR-1).
/// </summary>
/// <remarks>
/// Deriva directamente de <see cref="Exception"/> a propósito, igual que
/// <see cref="PartialCloneNotSupportedException"/>: ningún <c>catch</c> de la
/// CLI que degrada <see cref="IOException"/>/<see cref="InvalidOperationException"/>/
/// <see cref="ArgumentException"/> a un valor permisivo puede tragársela, de
/// modo que llega al manejador global de la CLI, que sanea el mensaje en inglés
/// y termina con código 1 sin borrar, restaurar ni escanear nada.
/// </remarks>
public sealed class RepositoryHookNotSupportedException : Exception
{
    public RepositoryHookNotSupportedException(string message)
        : base(message)
    {
    }
}
