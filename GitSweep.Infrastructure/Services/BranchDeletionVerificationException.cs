namespace GitSweep.Infrastructure.Services;

/// <summary>
/// <c>git branch -d</c> ya borró la rama, pero la comprobación posterior no
/// pudo confirmar que otra ref siga conteniendo su punta, o la rama no se
/// pudo volver a crear en esa punta. El resultado no es «no se borró»:
/// <see cref="GitRepositoryService.DeleteBranchAsync"/> lanza esta excepción
/// en lugar de devolver <c>(false, ...)</c>, porque <c>clean</c> revertiría
/// entonces la entrada de la papelera, que puede ser el único registro del
/// SHA. El mensaje (en inglés) nombra la rama y el SHA de su punta.
/// </summary>
/// <remarks>
/// Deriva directamente de <see cref="Exception"/> a propósito, igual que
/// <see cref="GitCommandLimitException"/>: no es
/// <see cref="InvalidOperationException"/>, <see cref="IOException"/> ni
/// <see cref="ArgumentException"/>, así que ningún <c>catch</c> filtrado de
/// la CLI puede tragársela. Llega al manejador global, <c>clean</c> termina
/// con error sin borrar nada más y la entrada de la papelera se conserva.
/// </remarks>
public sealed class BranchDeletionVerificationException : Exception
{
    public BranchDeletionVerificationException(string message)
        : base(message)
    {
    }

    public BranchDeletionVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
