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
    }

    /// <summary>
    /// Puts the scale sentence's "to" on the same baseline as the number in the replica box
    /// (#293). Centred in the row beside the box, the 13px word and the box's 14px number
    /// were centred on their line boxes rather than on their baselines, and with the
    /// platform's interface font "to" sat visibly above the number it introduces. A
    /// StackPanel has no baseline alignment, so the word's top margin is set from the two
    /// text layouts once the box has been laid out.
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
    }
}
