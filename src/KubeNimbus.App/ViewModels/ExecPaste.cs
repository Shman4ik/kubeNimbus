using System.Text;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// What the exec pane sends for a paste: the clipboard's text made safe to hand to a shell,
/// and wrapped in bracketed-paste markers when the shell asked for them. The same rules as
/// XTerm.NET's own <c>Terminal.Paste</c> — which the terminal control's
/// <c>PasteFromClipboardAsync</c> bypasses, sending the clipboard raw.
/// </summary>
/// <remarks>
/// <para>
/// Raw meant two things. The bracketed-paste promise — <c>ESC[200~</c> … <c>ESC[201~</c>,
/// everything between them data — was never made, so a shell that had asked for it (bash,
/// zsh) ran each pasted line as it arrived. And the text was not filtered, so a clipboard
/// holding <c>ESC[201~</c> could end a bracketed paste early and the rest would run as typed,
/// and any other escape sequence reached the shell's line editor as keystrokes.
/// </para>
/// <para>
/// Line endings become carriage returns, which is what the Return key sends and what a shell
/// reading a terminal expects; every other control character except tab is dropped — C1 as
/// well as C0, because U+009B opens a sequence exactly as <c>ESC [</c> does.
/// </para>
/// </remarks>
public static class ExecPaste
{
    public const string BracketStart = "\u001b[200~";
    public const string BracketEnd = "\u001b[201~";

    /// <summary>The text to send, or empty when nothing is left once the controls are gone.</summary>
    public static string Prepare(string text, bool bracketed)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        var kept = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (!char.IsControl(c) || c is '\r' or '\t')
            {
                kept.Append(c);
            }
        }

        if (kept.Length == 0)
        {
            return "";
        }

        return bracketed ? BracketStart + kept + BracketEnd : kept.ToString();
    }

    /// <summary>
    /// How many lines an unbracketed paste holds: one per line ending, and one more for text
    /// after the last. A single line — with or without its own Return — is what typing it
    /// would have done; more than one is a script that runs line by line as it arrives, which
    /// is what the pane asks about first.
    /// </summary>
    public static int LineCount(string prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Length == 0)
        {
            return 0;
        }

        var endings = prepared.Count(c => c == '\r');
        return endings + (prepared.EndsWith('\r') ? 0 : 1);
    }
}
