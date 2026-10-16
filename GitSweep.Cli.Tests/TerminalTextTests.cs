using GitSweep.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace GitSweep.Cli.Tests;

/// <summary>
/// SEC-B02: <see cref="TerminalText"/> turns every control and invisible
/// formatting character of external text into a visible escape, so nothing
/// a repository or remote supplies can drive the terminal (CSI/OSC
/// sequences, BEL, carriage-return overwrites, bidi spoofing).
/// </summary>
public sealed class TerminalTextTests
{
    [Theory]
    [InlineData("\u001b", @"\x1B")]          // ESC
    [InlineData("\u0007", @"\x07")]          // BEL
    [InlineData("\r", @"\x0D")]              // CR
    [InlineData("\t", @"\x09")]              // TAB is a C0 control too
    [InlineData("\0", @"\x00")]              // NUL
    [InlineData("\u001f", @"\x1F")]          // last C0 control
    [InlineData("\u007f", @"\x7F")]          // DEL
    [InlineData("\u0080", @"\x80")]          // first C1 control
    [InlineData("\u009b", @"\x9B")]          // C1 CSI
    [InlineData("\u009f", @"\x9F")]          // last C1 control
    [InlineData("\u202e", @"\u202E")]        // RIGHT-TO-LEFT OVERRIDE
    [InlineData("\u200b", @"\u200B")]        // ZERO WIDTH SPACE
    [InlineData("\u061c", @"\u061C")]        // ARABIC LETTER MARK
    [InlineData("\u200e", @"\u200E")]        // LEFT-TO-RIGHT MARK
    [InlineData("\u200f", @"\u200F")]        // RIGHT-TO-LEFT MARK
    [InlineData("\u202a", @"\u202A")]        // LEFT-TO-RIGHT EMBEDDING
    [InlineData("\u2066", @"\u2066")]        // LEFT-TO-RIGHT ISOLATE
    [InlineData("\u2069", @"\u2069")]        // POP DIRECTIONAL ISOLATE
    [InlineData("\ufeff", @"\uFEFF")]        // ZERO WIDTH NO-BREAK SPACE / BOM
    public void Sanitize_replaces_a_control_or_invisible_character_with_a_visible_escape(string input, string expected)
    {
        Assert.Equal(expected, TerminalText.Sanitize(input));
        Assert.Equal("a" + expected + "b", TerminalText.Sanitize("a" + input + "b"));
    }

    [Fact]
    public void Sanitize_neutralizes_whole_escape_sequences()
    {
        var hostile = "pre\u001b]0;PWNED\u0007post\u001b[2J";

        var sanitized = TerminalText.Sanitize(hostile);

        Assert.Equal(@"pre\x1B]0;PWNED\x07post\x1B[2J", sanitized);
        Assert.DoesNotContain('\u001b', sanitized);
        Assert.DoesNotContain('\u0007', sanitized);
    }

    [Fact]
    public void Sanitize_escapes_newline_unless_newlines_are_allowed()
    {
        Assert.Equal(@"line1\x0Aline2", TerminalText.Sanitize("line1\nline2"));
        Assert.Equal("line1\nline2", TerminalText.Sanitize("line1\nline2", allowNewlines: true));

        // Only LF is allowed: CR (which rewrites the line) and the rest of
        // the controls are still escaped in a multi-line block.
        Assert.Equal("a\\x0D\nb\\x1B[2J", TerminalText.Sanitize("a\r\nb\u001b[2J", allowNewlines: true));
    }

    [Theory]
    [InlineData("feature/login")]
    [InlineData("fix]")]
    [InlineData("[red]not markup[/]")]
    [InlineData(@"C:\Users\dev\repo")]
    [InlineData("rama/canción-ñandú")]
    [InlineData("ветка/функция")]
    [InlineData("分支/功能")]
    [InlineData("emoji 🚀 ok")]
    [InlineData("nbsp\u00a0and é")]
    [InlineData("")]
    public void Sanitize_leaves_printable_text_and_non_ascii_letters_unchanged(string input)
    {
        Assert.Equal(input, TerminalText.Sanitize(input));
    }

    [Fact]
    public void Sanitize_and_Markup_turn_null_into_an_empty_string()
    {
        Assert.Equal(string.Empty, TerminalText.Sanitize(null));
        Assert.Equal(string.Empty, TerminalText.Markup(null));
    }

    [Fact]
    public void Sanitize_is_idempotent()
    {
        var once = TerminalText.Sanitize("a\u001b[2J\u202eb\n");

        Assert.Equal(once, TerminalText.Sanitize(once));
    }

    [Fact]
    public void Markup_sanitizes_then_escapes_markup_brackets()
    {
        Assert.Equal(@"fix]] \x1B[[2J [[red]]x[[/]]", TerminalText.Markup("fix] \u001b[2J [red]x[/]"));
        Assert.Equal("a\nb", TerminalText.Markup("a\nb", allowNewlines: true));
        Assert.Equal(@"a\x0Ab", TerminalText.Markup("a\nb"));
    }

    [Fact]
    public void Markup_output_renders_as_the_literal_sanitized_text()
    {
        var console = new TestConsole().Width(400);

        console.MarkupLine($"[red]Error:[/] {TerminalText.Markup("fix] \u001b]0;PWNED\u0007")}");

        Assert.Contains(@"Error: fix] \x1B]0;PWNED\x07", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', console.Output);
    }
}
