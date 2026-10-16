namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Límites de cada invocación de git: plazo para la plomería local, plazo
/// para <c>git fetch</c> y topes de caracteres de stdout/stderr. Producción
/// usa siempre <see cref="Default"/>; el constructor interno de
/// <see cref="GitRepositoryService"/> permite a las pruebas inyectar límites
/// diminutos sin esperar los plazos reales.
/// </summary>
internal sealed class GitCommandLimits
{
    // CancellationTokenSource.CancelAfter no admite más de int.MaxValue ms.
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    public GitCommandLimits(
        TimeSpan localCommandTimeout,
        TimeSpan fetchTimeout,
        int maxStandardOutputChars,
        int maxStandardErrorChars)
    {
        ValidateTimeout(localCommandTimeout, nameof(localCommandTimeout));
        ValidateTimeout(fetchTimeout, nameof(fetchTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStandardOutputChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStandardErrorChars, 1);

        LocalCommandTimeout = localCommandTimeout;
        FetchTimeout = fetchTimeout;
        MaxStandardOutputChars = maxStandardOutputChars;
        MaxStandardErrorChars = maxStandardErrorChars;
    }

    /// <summary>Límites de producción (ver las constantes de <see cref="GitRepositoryService"/>).</summary>
    public static GitCommandLimits Default { get; } = new(
        GitRepositoryService.LocalCommandTimeout,
        GitRepositoryService.FetchTimeout,
        GitRepositoryService.MaxStandardOutputChars,
        GitRepositoryService.MaxStandardErrorChars);

    public TimeSpan LocalCommandTimeout { get; }

    public TimeSpan FetchTimeout { get; }

    public int MaxStandardOutputChars { get; }

    public int MaxStandardErrorChars { get; }

    private static void ValidateTimeout(TimeSpan timeout, string paramName)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(paramName, timeout, "Timeout must be positive and at most int.MaxValue milliseconds.");
        }
    }
}
