using System.Text.RegularExpressions;

namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Removes secrets from git output before it is shown or logged. Git quotes
/// the remote URL in transport errors, and a remote URL can embed
/// credentials (<c>https://user:token@host/...</c>) or carry them in query
/// parameters; neither may reach the terminal, a log, or a test report.
/// Text without secrets is returned unchanged.
/// </summary>
public static class GitOutputRedactor
{
    // Greedy up to the last '@' before the first '/': a password may itself
    // contain '@', and stopping at the first one would leave its tail
    // behind. The authority segment cannot contain '/', so '@' in a path
    // is never mistaken for userinfo.
    private static readonly Regex UrlUserInfo = new(
        @"([A-Za-z][A-Za-z0-9+.\-]*://)[^/\s]*@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SecretParameter = new(
        @"\b(access[-_]?token|refresh[-_]?token|id[-_]?token|auth[-_]?token|oauth[-_]?token|private[-_]?token|api[-_]?key|api[-_]?secret|client[-_]?secret|session[-_]?id|token|password|passwd|pwd|secret|signature|sig|auth|credential)=[^&\s#]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AuthorizationHeader = new(
        @"((?:Proxy-)?Authorization\s*:\s*(?:[A-Za-z][A-Za-z0-9\-]*\s+)?)\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = UrlUserInfo.Replace(text, "$1***@");
        redacted = SecretParameter.Replace(redacted, "$1=***");
        redacted = AuthorizationHeader.Replace(redacted, "$1***");
        return redacted;
    }
}
