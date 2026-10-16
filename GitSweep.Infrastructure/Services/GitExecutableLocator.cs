namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Localiza el ejecutable de git como una ruta absoluta de confianza. Lanzar
/// <c>git</c> por nombre es inseguro en Windows: <c>CreateProcess</c> busca
/// primero en el directorio actual del proceso padre y despues en PATH, y el
/// directorio actual por defecto de GitSweep es el repositorio que se analiza.
/// Un <c>git.exe</c> plantado dentro de un clon hostil se ejecutaria entonces
/// con los privilegios del usuario, saltandose todo el endurecimiento de
/// argumentos y entorno. Aqui solo se aceptan entradas de PATH totalmente
/// cualificadas, de modo que nada se resuelve contra el directorio actual.
/// </summary>
internal static class GitExecutableLocator
{
    internal const string NotFoundMessage =
        "Git executable was not found on PATH. Install Git and make sure it is on PATH.";

    // Resolucion unica por proceso: PATH no cambia durante la ejecucion de la
    // CLI y asi cada invocacion de git no vuelve a recorrer el sistema de
    // archivos. Un resultado null tambien se cachea (fallo cerrado).
    private static readonly Lazy<string?> ProcessWide = new(
        () => Resolve(
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows(),
            File.Exists),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Ruta absoluta de git para este proceso, o <c>null</c> si no hay un git
    /// en ninguna entrada de PATH totalmente cualificada.
    /// </summary>
    internal static string? ResolvedPath => ProcessWide.Value;

    /// <summary>
    /// Ruta absoluta de git para este proceso. Falla cerrado: si git no se
    /// encuentra lanza <see cref="InvalidOperationException"/> y nunca recurre
    /// al nombre desnudo <c>git</c>.
    /// </summary>
    internal static string GetRequiredPath() => Require(ResolvedPath);

    internal static string Require(string? resolvedPath) =>
        resolvedPath ?? throw new InvalidOperationException(NotFoundMessage);

    /// <summary>
    /// Resolucion pura (sin estado global) para poder probarla en cualquier
    /// plataforma. Recorre <paramref name="pathVariable"/> en orden y devuelve
    /// la primera ruta completa <c>&lt;entrada&gt;/git[.exe]</c> que
    /// <paramref name="fileExists"/> confirma, o <c>null</c>.
    /// </summary>
    /// <param name="pathVariable">Valor de PATH (puede ser <c>null</c>).</param>
    /// <param name="isWindows">
    /// Selecciona el separador (<c>;</c> o <c>:</c>) y el nombre del ejecutable.
    /// </param>
    /// <param name="fileExists">Sonda del sistema de archivos (inyectable).</param>
    internal static string? Resolve(string? pathVariable, bool isWindows, Func<string, bool> fileExists)
    {
        // Solo git.exe en Windows: un git.cmd/git.bat lo interpretaria cmd.exe
        // (ejecucion de comandos y expansion de argumentos por el shell).
        var candidate = isWindows ? "git.exe" : "git";

        foreach (var entry in FullyQualifiedEntries(pathVariable, isWindows))
        {
            var fullPath = Path.Combine(entry, candidate);
            if (fileExists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Filtro de entradas de PATH compartido por <see cref="Resolve"/> y por el
    /// PATH que hereda el proceso git hijo: devuelve, en orden, solo las
    /// entradas totalmente cualificadas, sin espacios ni comillas alrededor.
    /// </summary>
    /// <param name="pathVariable">Valor de PATH (puede ser <c>null</c>).</param>
    /// <param name="isWindows">Selecciona el separador (<c>;</c> o <c>:</c>).</param>
    internal static IEnumerable<string> FullyQualifiedEntries(string? pathVariable, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            yield break;
        }

        var separator = isWindows ? ';' : ':';
        foreach (var rawEntry in pathVariable.Split(separator))
        {
            var entry = rawEntry.Trim().Trim('"').Trim();

            // Vacía o no totalmente cualificada ("." , "tools", ".\bin", "\x",
            // "C:x"): se resolvería contra el directorio actual, que en un
            // clon hostil está controlado por el atacante.
            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry))
            {
                continue;
            }

            yield return entry;
        }
    }

    /// <summary>
    /// PATH para el proceso git hijo: solo las entradas totalmente
    /// cualificadas de <paramref name="pathVariable"/>, unidas con el
    /// separador de la plataforma. Git busca sus programas auxiliares (sh
    /// para los alias, ssh durante <c>--fetch</c>, helpers remotos) en PATH
    /// desde su directorio de trabajo, que es el repositorio analizado; una
    /// entrada vacía o relativa los resolvería dentro de un clon hostil.
    /// Devuelve una cadena vacía si no queda ninguna entrada.
    /// </summary>
    internal static string FilterPathVariable(string? pathVariable, bool isWindows) =>
        string.Join(isWindows ? ';' : ':', FullyQualifiedEntries(pathVariable, isWindows));
}
