using System.Text;
using System.Text.RegularExpressions;

namespace GitSweep.Core.Services;

/// <summary>
/// Matcher glob seguro para nombres de rama: <c>*</c> coincide con cualquier
/// secuencia (incluido <c>/</c>), <c>?</c> con un carácter. Comparación
/// insensible a mayúsculas, coherente con el resto de GitSweep. El patrón se
/// valida y se convierte a regex anclada con timeout; nunca se pasa a git ni
/// a un shell.
/// </summary>
public static class BranchGlobMatcher
{
    public const int MaxPatternLength = 255;

    public static bool IsValidPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPatternLength)
            return false;
        if (pattern.Trim() != pattern)
            return false;
        foreach (var c in pattern)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
            // Caracteres que nunca aparecen en un nombre de rama válido;
            // permitirlos en el patrón solo crearía patrones muertos/confusos.
            if (c is '~' or '^' or ':' or '[' or ']' or '\\')
                return false;
        }
        if (pattern.Contains("..", StringComparison.Ordinal) || pattern.Contains("@{", StringComparison.Ordinal))
            return false;
        return true;
    }

    public static bool IsMatch(string branchName, string pattern)
    {
        if (string.IsNullOrEmpty(branchName) || !IsValidPattern(pattern))
            return false;
        var regex = ToRegex(pattern);
        return regex.IsMatch(branchName);
    }

    /// <summary>Primer patrón que coincide, o <see langword="null"/>.</summary>
    public static string? FirstMatchingPattern(string branchName, IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (var pattern in patterns)
        {
            if (IsMatch(branchName, pattern))
                return pattern;
        }
        return null;
    }

    internal static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }
}
