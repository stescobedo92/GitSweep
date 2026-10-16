using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// Helpers for the security tests that plant hostile hooks / helper programs
/// in a temp repository and assert GitSweep never runs them.
/// </summary>
internal static class TestScripts
{
    /// <summary>
    /// Writes a script that git can actually execute on the current OS.
    /// </summary>
    /// <remarks>
    /// Git for Windows runs hooks and helper scripts through its bundled
    /// <c>sh</c> regardless of file mode, but git on Linux/macOS silently
    /// ignores a hook (or fails to exec a helper) that lacks the execute bit.
    /// A planted script that cannot run makes a "GitSweep never executes it"
    /// assertion pass vacuously, so every planted script goes through here.
    /// The content is written with LF line endings and UTF-8 without BOM: a
    /// CRLF or BOM in the shebang line breaks <c>/bin/sh</c> on Linux.
    /// </remarks>
    public static async Task WriteExecutableAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
