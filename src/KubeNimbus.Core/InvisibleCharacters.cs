using System.Buffers;
using System.Globalization;
using System.Text;

namespace KubeNimbus.Core;

/// <summary>
/// Makes the characters that change how text <em>looks</em> without being seen — the bidi
/// controls and the zero-width ones — visible, as <c>⟨U+202E⟩</c>, in text a cluster wrote
/// and the app draws: log lines, an Event's reason and message, a CRD's printer columns, Argo
/// CD's messages, the Applications page's findings.
/// </summary>
/// <remarks>
/// <para>
/// None of them is a control character to <see cref="char.IsControl"/> (they are Unicode
/// format characters), so the log pane's escape stripping let them through. A right-to-left
/// override (U+202E) reorders everything after it on the line: "exe.txt" can be made to read
/// "txt.exe", an error message can be made to read as its opposite, and a log line can be
/// made to look like a different one. A zero-width space makes two different strings look
/// identical. Text from a cluster is written by whoever runs a container or writes an
/// object, so what the app draws should be what the text is.
/// </para>
/// <para>
/// A marker rather than removal, because the character is part of the text and somebody
/// investigating why a line reads strangely needs to see that it is there. Copy and search
/// work on the same marked text, so what is found and copied is what is shown.
/// </para>
/// <para>
/// The zero-width joiner and non-joiner (U+200C, U+200D) are deliberately left alone: they
/// shape Persian and Indic text and join emoji sequences, they reorder nothing, and marking
/// them would turn ordinary text into noise.
/// </para>
/// </remarks>
public static class InvisibleCharacters
{
    private static readonly SearchValues<char> Revealed = SearchValues.Create(
        "\u061C" // ARABIC LETTER MARK
        + "\u200B" // ZERO WIDTH SPACE
        + "\u200E\u200F" // LEFT-TO-RIGHT MARK, RIGHT-TO-LEFT MARK
        + "\u202A\u202B\u202C\u202D\u202E" // embeddings, pop, overrides
        + "\u2060" // WORD JOINER
        + "\u2066\u2067\u2068\u2069" // isolates
        + "\uFEFF"); // ZERO WIDTH NO-BREAK SPACE (byte order mark)

    /// <summary>Whether <paramref name="c"/> is one of the characters <see cref="Reveal"/> marks.</summary>
    public static bool IsRevealed(char c) => Revealed.Contains(c);

    /// <summary>
    /// <paramref name="text"/> with each bidi or zero-width character replaced by
    /// <c>⟨U+XXXX⟩</c>. The same instance when there is none, which is nearly every string.
    /// </summary>
    public static string Reveal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var first = text.AsSpan().IndexOfAny(Revealed);
        if (first < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        builder.Append(text, 0, first);
        for (var i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (Revealed.Contains(c))
            {
                builder.Append(Marker(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>"⟨U+202E⟩".</summary>
    public static string Marker(char c) => $"⟨U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)}⟩";
}
