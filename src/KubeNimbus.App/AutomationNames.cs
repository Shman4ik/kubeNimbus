using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace KubeNimbus.App;

/// <summary>
/// Gives every interactive control an accessible name without writing the sentence twice.
/// A button that draws only an icon reports its UI Automation name as the icon's type
/// (<c>Avalonia.Controls.PathIcon</c>), which is what a screen reader reads out, and what
/// <c>scripts/qa-ui.ps1</c> sees as <c>&lt;unnamed&gt;</c>. Every such control in this app
/// already carries a tooltip saying what it does, so the name is taken from it: one string,
/// one place to keep it true (ENG-4, ENG-47).
/// </summary>
/// <remarks>
/// <para>
/// A class handler like <c>Nimbus.Ui.Controls.ToolTipHitTesting</c>, installed from
/// <c>App.Initialize</c> so the screenshot harness gets it too. It reacts whenever a tooltip
/// is set or changes, which covers tooltips that are bindings, and it leaves alone any
/// control whose name was set by hand: a name written in markup is a decision, a name
/// derived from a tooltip is a default.
/// </para>
/// <para>
/// The tooltip is shortened to what it is called, not everything it says: text after an em
/// dash or a colon is explanation, and a trailing parenthesis is the shortcut, which a
/// screen reader already announces from the control's access key.
/// </para>
/// <para>
/// Controls whose name comes from their own text (a button with a string for content) are
/// skipped, because the tooltip there is usually the longer sentence and would replace the
/// better name. A text box with no tooltip takes its placeholder.
/// </para>
/// </remarks>
internal static partial class AutomationNames
{
    private static bool _installed;

    private static readonly Dictionary<string, string> PartNames = new(StringComparer.Ordinal)
    {
        ["PART_CloseButton"] = "Close",
        ["PART_IncreaseButton"] = "Increase",
        ["PART_DecreaseButton"] = "Decrease",
    };

    private static readonly AttachedProperty<bool> DerivedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Derived", typeof(AutomationNames));

    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, _) => Derive(control));
        TextBox.PlaceholderTextProperty.Changed.AddClassHandler<TextBox>((control, _) => Derive(control));

        // A button or list item whose content is a layout of text and icons reads as the layout's
        // type name. Once it has its content, name it after the text it shows.
        Control.LoadedEvent.AddClassHandler<ContentControl>((control, _) => NameFromContentText(control));

        // Template parts a control brings with it: their names are fixed by the template, so
        // they are named by what they do once, here, instead of in every theme that restyles them.
        Button.LoadedEvent.AddClassHandler<Button>((button, _) =>
        {
            if (!button.IsSet(AutomationProperties.NameProperty) && button.Name is { } part && PartNames.TryGetValue(part, out var name))
            {
                button.SetValue(AutomationProperties.NameProperty, name);
            }
        });
    }

    /// <summary>What a tooltip is called: its first clause, without a trailing shortcut.</summary>
    internal static string NameFromTooltip(string tooltip)
    {
        var text = tooltip.Trim();
        foreach (var separator in new[] { " — ", ": ", ". " })
        {
            var at = text.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0)
            {
                text = text[..at];
            }
        }

        return TrailingParenthesis().Replace(text, "").Trim();
    }

    /// <summary>
    /// Names the control at the right of each preferences card after the label at its left, so
    /// the page's switches, drop-downs and number boxes are read as "Advanced view" and not
    /// as an unnamed control. A card is a two-column grid with the label first in a stack
    /// panel, the shape every row of that page shares; the label stays the one place the
    /// words are written. It walks the logical tree, not the visual one: the page is tabs,
    /// and only the tab on screen has visuals when the page loads.
    /// </summary>
    internal static void NameCardControls(Control page)
    {
        foreach (var card in page.GetLogicalDescendants().OfType<Grid>())
        {
            if (card.ColumnDefinitions.Count != 2)
            {
                continue;
            }

            var label = card.Children.OfType<StackPanel>().FirstOrDefault(p => Grid.GetColumn(p) == 0)
                ?.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("label"));
            if (label?.Text is not { Length: > 0 } text)
            {
                continue;
            }

            foreach (var control in card.Children.Where(c => Grid.GetColumn(c) == 1))
            {
                if (control is ToggleSwitch or ComboBox or NumericUpDown && !control.IsSet(AutomationProperties.NameProperty))
                {
                    control.SetValue(AutomationProperties.NameProperty, text);
                }
            }
        }
    }

    /// <summary>
    /// The fallback for a control with no tooltip and no hand-written name whose content is not
    /// text: the text it displays, first few visible blocks, which is how a sighted reader names
    /// it. Skipped when the content already names itself through <c>ToString()</c>, as the
    /// Applications rows do, and when anything has set a name.
    /// </summary>
    private static void NameFromContentText(ContentControl control)
    {
        if (control is not (Button or ToggleButton or ListBoxItem) || control.IsSet(AutomationProperties.NameProperty)
            || control.Content is null or string)
        {
            return;
        }

        var content = control.Content;
        if (content.ToString() != content.GetType().ToString())
        {
            return;
        }

        var words = control.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!.Trim())
            .Take(3)
            .ToList();
        if (words.Count > 0)
        {
            control.SetCurrentValue(AutomationProperties.NameProperty, string.Join(", ", words));
        }
    }

    private static void Derive(Control control)
    {
        if (!IsInteractive(control) || HasHandWrittenName(control))
        {
            return;
        }

        if (control is ContentControl { Content: string })
        {
            return;
        }

        // A tooltip is a string, or a text block when it needs a binding or a wrap.
        var tip = ToolTip.GetTip(control);
        var source = tip as string ?? (tip as TextBlock)?.Text;
        if (string.IsNullOrWhiteSpace(source) && control is TextBox box)
        {
            source = box.PlaceholderText;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var name = NameFromTooltip(source);
        if (name.Length == 0)
        {
            return;
        }

        control.SetCurrentValue(AutomationProperties.NameProperty, name);
        control.SetValue(DerivedProperty, true);
    }

    private static bool HasHandWrittenName(Control control) =>
        control.IsSet(AutomationProperties.NameProperty) && !control.GetValue(DerivedProperty);

    private static bool IsInteractive(Control control) =>
        control is Button or ToggleButton or ListBoxItem or TextBox or ComboBox or MenuItem or TabItem
            or NumericUpDown or Slider;

    [GeneratedRegex(@"\s*\([^()]*\)\s*$")]
    private static partial Regex TrailingParenthesis();
}
