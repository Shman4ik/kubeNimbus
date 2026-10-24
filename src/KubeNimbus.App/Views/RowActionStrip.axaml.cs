using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace KubeNimbus.App.Views;

public partial class RowActionStrip : UserControl
{
    public RowActionStrip()
    {
        InitializeComponent();
        ReplicaInput.LayoutUpdated += (_, _) => AlignBlankLead();
        ActionSentence.LayoutUpdated += (_, _) => AlignGlyph();
    }

    /// <summary>
    /// Centres the verb's glyph on the sentence's first line of text, at the middle of its
    /// capitals. A fixed top margin put it level with the words only while the first line
    /// was a line of text: when the scale box fits on that line, the line is as tall as the
    /// box and its words sit at the bottom, so the glyph stood above them (the owner's
    /// screenshot, 2026-10-21). Measured from the text layout, because the interface font
    /// is a setting (UI rule 23).
    /// </summary>
    private void AlignGlyph()
    {
        if (!ActionGlyph.IsEffectivelyVisible || ActionGlyph.Parent is not Visual grid
            || ActionSentence.TextLayout.TextLines is not [var first, ..]
            || ActionSentence.TranslatePoint(new Point(0, ActionSentence.Padding.Top + first.Baseline), grid) is not { } baseline)
        {
            return;
        }

        var top = Math.Max(0, baseline.Y - (CapMiddle * ActionSentence.FontSize) - (ActionGlyph.Bounds.Height / 2));
        if (Math.Abs(ActionGlyph.Margin.Top - top) > 0.25)
        {
            ActionGlyph.Margin = new Thickness(0, top, 10, 0);
        }
    }

    /// <summary>The middle of a capital above the baseline, in ems: about half the cap height of the interface faces.</summary>
    internal const double CapMiddle = 0.35;

    /// <summary>
    /// Puts the scale sentence's "to" on the same baseline as the number in the replica box
    /// (#293). Centred in the row beside the box, the 13px word and the box's 14px number
    /// were centred on their line boxes rather than on their baselines, and with the
    /// platform's interface font "to" sat visibly above the number it introduces. A
    /// StackPanel has no baseline alignment, so the word's top margin is set from the two
    /// text layouts once the box has been laid out.
    ///
    /// The row stands on the sentence's baseline (<c>BaselineAlignment="Baseline"</c> puts
    /// its bottom there), so the box's number would sit a box-bottom above the words around
    /// it: the owner saw "to" and the box float above the sentence on 2026-10-21. The row
    /// hangs below the line by the distance from the number's baseline to the box's bottom,
    /// which puts the sentence, "to" and the number on one baseline, and the sentence makes
    /// room for that hang under its last line. Measured, not a constant, because the
    /// interface font is a setting (UI rule 23).
    /// </summary>
    private void AlignBlankLead()
    {
        if (!BlankLead.IsEffectivelyVisible || BlankLead.Parent is not Visual row
            || ReplicaInput.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter
            || presenter.TextLayout.TextLines is not [var boxLine, ..]
            || BlankLead.TextLayout.TextLines is not [var leadLine, ..]
            || presenter.TranslatePoint(new Point(0, boxLine.Baseline), row) is not { } boxBaseline)
        {
            return;
        }

        var top = Math.Max(0, boxBaseline.Y - BlankLead.Padding.Top - leadLine.Baseline);
        if (Math.Abs(BlankLead.Margin.Top - top) > 0.25)
        {
            BlankLead.Margin = new Thickness(0, top, 0, 0);
        }

        var hang = Math.Max(0, BlankRow.Bounds.Height - boxBaseline.Y);
        if (Math.Abs(BlankRow.Margin.Bottom + hang) > 0.25)
        {
            BlankRow.Margin = new Thickness(4, 0, 0, -hang);
            ActionSentence.Padding = new Thickness(0, 0, 0, hang);
        }
    }
}
