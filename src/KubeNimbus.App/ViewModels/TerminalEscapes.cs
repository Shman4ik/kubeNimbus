using System.Text;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Removes terminal escape sequences and other control characters from a log line.
/// </summary>
/// <remarks>
/// <para>
/// A container's stdout is often written for a terminal that is not there: .NET's console
/// logger, Rails, Go's zap and most CLI tools colour their output with ANSI SGR sequences
/// (<c>ESC[32m</c>) whenever they decide a TTY might be attached, and a Kubernetes log is
/// that byte stream verbatim. The log panes draw text, not a terminal, so the sequences
/// arrived as a box glyph for ESC followed by <c>[40m[32minfo[39m[22m[49m</c> — on every
/// line, ahead of the words, and in the way of both the search and the severity keywords
/// (<c>[32minfo</c> has no word boundary before <c>info</c> that a reader would expect).
/// </para>
/// <para>
/// They are removed rather than rendered. Colour from the application would compete with
/// the pane's own severity colour, which is the one thing the pane promises means the same
/// on every line; and a line drawn as one string is what keeps selection, copy and the
/// search highlight simple (see <c>Controls/LogLineText</c>).
/// </para>
/// <para>
/// What is recognised is ECMA-48's shape, not a list of known codes: CSI
/// (<c>ESC [</c> parameters, intermediates, one final byte), OSC and the other string
/// sequences (up to BEL or <c>ESC \</c>), and two-byte escapes. Any other C0 control
/// character except tab is dropped too, since the pane would draw it as a box. A line with
/// no control character at all is returned as the same instance, which is every line from
/// an application that does not colour its output.
/// </para>
/// </remarks>
public static class TerminalEscapes
{
    private const char Esc = '\u001b';
    private const char Bel = '\u0007';

    public static string Strip(string line)
    {
        var first = FirstControl(line);
        if (first < 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        builder.Append(line, 0, first);
        var i = first;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == Esc)
            {
                i = SkipEscape(line, i);
                continue;
            }

            if (c != '\t' && char.IsControl(c))
            {
                i++;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private static int FirstControl(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c != '\t' && char.IsControl(c))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The index just past the escape sequence starting at <paramref name="start"/> (an ESC).</summary>
    private static int SkipEscape(string line, int start)
    {
        var i = start + 1;
        if (i >= line.Length)
        {
            return i;
        }

        switch (line[i])
        {
            case '[':
                // CSI: parameter bytes 0x30–0x3F, intermediate bytes 0x20–0x2F, then one
                // final byte 0x40–0x7E. A sequence cut off by the end of the line ends there.
                i++;
                while (i < line.Length && line[i] is >= '0' and <= '?')
                {
                    i++;
                }

                while (i < line.Length && line[i] is >= ' ' and <= '/')
                {
                    i++;
                }

                return i < line.Length && line[i] is >= '@' and <= '~' ? i + 1 : i;

            case ']' or 'P' or 'X' or '^' or '_':
                // OSC, DCS, SOS, PM, APC: a string terminated by BEL or ST (ESC \).
                i++;
                while (i < line.Length)
                {
                    if (line[i] == Bel)
                    {
                        return i + 1;
                    }

                    if (line[i] == Esc && i + 1 < line.Length && line[i + 1] == '\\')
                    {
                        return i + 2;
                    }

                    i++;
                }

                return i;

            default:
                // Any other escape: intermediate bytes 0x20–0x2F, then one final byte
                // 0x30–0x7E (ESC 7, ESC =, ESC ( B). ESC before a control character or
                // another ESC drops the ESC alone and lets the loop deal with what follows.
                while (i < line.Length && line[i] is >= ' ' and <= '/')
                {
                    i++;
                }

                return i < line.Length && line[i] is >= '0' and <= '~' ? i + 1 : i;
        }
    }
}
