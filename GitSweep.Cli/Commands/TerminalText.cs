using System.Globalization;
using System.Text;

namespace GitSweep.Cli.Commands;

/// <summary>
/// The only way text that GitSweep did not author reaches the terminal:
/// branch, upstream and target names, trash fields (SHA, id), repository
/// paths, git stdout/stderr (including a remote's <c>remote:</c> sideband
/// text) and exception or configuration error messages. All of it can be
/// chosen by a hostile repository, remote or trash file.
/// </summary>
/// <remarks>
/// <para><see cref="Sanitize"/> makes every control and invisible formatting
/// character visible instead of letting the terminal act on it: an ESC
/// starts CSI/OSC sequences (OSC 52 writes the clipboard, CSI 2J erases the
/// screen), BEL ends OSC sequences, CR rewrites the current line, C1 controls
/// (U+0080..U+009F, e.g. the single-character CSI U+009B) are honoured by some
/// terminals, and bidi/zero-width characters make a name display differently
/// from what it is. <c>Markup.Escape</c> alone only doubles <c>[</c> and
/// <c>]</c>, so it stops markup injection but lets all of that through.</para>
/// <para>Use <see cref="Markup"/> for anything rendered as Spectre markup
/// (<c>MarkupLine</c>, table cells, prompt choices and converters, progress
/// task descriptions, panels) and <see cref="Sanitize"/> for raw writes to
/// <c>Profile.Out.Writer</c>. Both are idempotent.</para>
/// </remarks>
internal static class TerminalText
{
    /// <summary>
    /// Replaces each C0 control (U+0000..U+001F, except <c>\n</c> when
    /// <paramref name="allowNewlines"/>), DEL (U+007F) and C1 control
    /// (U+0080..U+009F) with <c>\xNN</c>, and each bidi/invisible formatting
    /// character (U+061C, U+200B..U+200F, U+202A..U+202E, U+2066..U+2069,
    /// U+FEFF) with <c>\uXXXX</c>, both in uppercase hex. Everything else,
    /// including non-ASCII letters, is unchanged. <see langword="null"/>
    /// becomes an empty string.
    /// </summary>
    /// <param name="text">External text to display.</param>
    /// <param name="allowNewlines">Keep <c>\n</c> (only LF; CR is still
    /// escaped) for a multi-line block such as git's stderr.</param>
    public static string Sanitize(string? text, bool allowNewlines = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var first = IndexOfUnsafe(text, allowNewlines);
        if (first < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        builder.Append(text, 0, first);
        for (var index = first; index < text.Length; index++)
        {
            var character = text[index];
            if (IsControl(character, allowNewlines))
            {
                builder.Append(@"\x").Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else if (IsInvisibleFormat(character))
            {
                builder.Append(@"\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// <see cref="Sanitize"/> followed by <c>Markup.Escape</c>: safe to
    /// interpolate into Spectre markup.
    /// </summary>
    public static string Markup(string? text, bool allowNewlines = false) =>
        Spectre.Console.Markup.Escape(Sanitize(text, allowNewlines));

    private static int IndexOfUnsafe(string text, bool allowNewlines)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (IsControl(character, allowNewlines) || IsInvisibleFormat(character))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsControl(char character, bool allowNewlines) =>
        character switch
        {
            '\n' => !allowNewlines,
            <= '\u001f' => true,
            >= '\u007f' and <= '\u009f' => true,
            _ => false
        };

    private static bool IsInvisibleFormat(char character) =>
        character is '\u061c'
            or (>= '\u200b' and <= '\u200f')
            or (>= '\u202a' and <= '\u202e')
            or (>= '\u2066' and <= '\u2069')
            or '\ufeff';
}
