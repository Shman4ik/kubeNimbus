using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// A search pinned as a coloured highlight: it stays marked in every line and on the
/// overview ruler while the box goes on to search for something else.
/// </summary>
/// <remarks>
/// <para>
/// This is Notepad++'s Mark, klogg's colour labels, lnav's named searches and stern's
/// <c>--highlight</c> — the thing people copy a log out to an editor for when one question
/// becomes two: "where are the timeouts, and where are the retries around them". Pins last
/// for the pane's life and are not persisted, like the rest of the search.
/// </para>
/// <para>
/// Five colours, one per pin, and no sixth pin: past five, colours stop being told apart
/// at a glance, which is the only thing a pin is for. Each colour has a translucent brush
/// for the boxes painted under the text, so the text and its level colour still read
/// through, and a solid one for the chip and the ruler's tick.
/// </para>
/// </remarks>
public sealed class LogPin
{
    public const int Max = 5;

    private static readonly Color[] Palette =
    [
        Color.Parse("#2EC4B6"),
        Color.Parse("#B07CFF"),
        Color.Parse("#F15BB5"),
        Color.Parse("#8BC34A"),
        Color.Parse("#29B6F6"),
    ];

    public LogPin(string text, LogQuery query, int slot)
    {
        Text = text;
        Query = query;
        var color = Palette[slot % Palette.Length];
        SolidBrush = new ImmutableSolidColorBrush(color);
        Brush = new ImmutableSolidColorBrush(Color.FromArgb(0x59, color.R, color.G, color.B));
        Slot = slot;
    }

    /// <summary>What was typed, shown on the chip.</summary>
    public string Text { get; }

    public LogQuery Query { get; }

    /// <summary>The colour's index; the next pin takes the first free one.</summary>
    public int Slot { get; }

    /// <summary>The box painted under a matching run of text.</summary>
    public IBrush Brush { get; }

    /// <summary>The chip's swatch and the ruler's tick.</summary>
    public IBrush SolidBrush { get; }
}
