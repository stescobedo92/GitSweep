namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Un comando git superó su plazo o su tope de salida y se mató su árbol de
/// procesos, así que su respuesta es desconocida. Los métodos de
/// <see cref="GitRepositoryService"/> que informan de un hecho del
/// repositorio (rama actual, worktrees, si una rama existe, su SHA, la raíz,
/// el directorio git común, la rama por defecto, la lista de ramas) o que
/// borran una rama lanzan esta excepción en lugar de devolver el valor por
/// defecto de «git respondió que no», que el llamador podría leer como señal
/// segura: «la rama no existe, restaurar puede crearla», «no hay rama actual
/// que proteger», «no se borró, revierte la papelera»...
/// </summary>
/// <remarks>
/// Deriva directamente de <see cref="Exception"/> a propósito: no es
/// <see cref="InvalidOperationException"/> ni <see cref="IOException"/>, así
/// que ningún <c>catch</c> que hoy degrada esos errores a un valor por
/// defecto (por ejemplo, la resolución de la raíz del repositorio en
/// <see cref="GitSweepConfigService"/>, que recurriría a otra ruta de
/// <c>.gitsweep.json</c>) puede tragársela; llega al manejador global de la
/// CLI y la ejecución termina con error sin borrar ni restaurar nada más.
/// El mensaje es el error en inglés que compone el ejecutor de git
/// (subcomando y límite superado).
/// </remarks>
public sealed class GitCommandLimitException : Exception
{
    public GitCommandLimitException(string message)
        : base(message)
    {
    }
}
