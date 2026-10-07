using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;
using KubeNimbus.Core;

namespace KubeNimbus.App.Editing;

/// <summary>
/// Draws each bidi or zero-width character in an editor as a <c>⟨U+202E⟩</c> box, the way
/// AvaloniaEdit's own <c>ShowBoxForControlCharacters</c> draws a control character. That
/// option covers <see cref="char.IsControl"/> only, and these are format characters, so a
/// right-to-left override in an annotation or a ConfigMap value reordered the rest of its
/// line in the YAML editor with nothing on screen to say why. The same set as
/// <see cref="InvisibleCharacters"/>, which marks them in the log panes and the lists.
/// </summary>
/// <remarks>
/// Only what is drawn changes: the document keeps the character, so an apply sends the
/// object's real value and a copy from the editor copies it. The element stands in for the
/// one character, so it is selected, deleted and stepped over as one.
/// </remarks>
public sealed class InvisibleCharacterGenerator : VisualLineElementGenerator
{
    /// <summary>Amber reads on both themes' editor backgrounds and is not a syntax colour.</summary>
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x7A, 0x00));

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var document = CurrentContext.Document;
        var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        for (var offset = startOffset; offset < end; offset++)
        {
            if (InvisibleCharacters.IsRevealed(document.GetCharAt(offset)))
            {
                return offset;
            }
        }

        return -1;
    }

    public override VisualLineElement ConstructElement(int offset)
    {
        var c = CurrentContext.Document.GetCharAt(offset);
        var properties = new VisualLineElementTextRunProperties(CurrentContext.GlobalTextRunProperties);
        properties.SetForegroundBrush(MarkerBrush);
        var text = FormattedTextElement.PrepareText(TextFormatter.Current, InvisibleCharacters.Marker(c), properties);
        return new FormattedTextElement(text, 1);
    }
}
