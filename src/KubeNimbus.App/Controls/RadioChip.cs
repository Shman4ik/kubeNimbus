using Avalonia.Controls.Primitives;

namespace KubeNimbus.App.Controls;

/// <summary>
/// A chip in a one-of row (the Applications list's All / Needs attention / Deployed &lt; 1 h /
/// Not in Argo CD): a <see cref="ToggleButton"/> that a click can turn on but never off,
/// styled exactly as every other <c>ToggleButton.chip</c>.
/// </summary>
/// <remarks>
/// A plain ToggleButton over a two-way <c>IsChecked</c> unchecks itself on a second click;
/// the view model refusing that write does not help, because the binding does not re-read a
/// source that refused it — the chip rendered off while the list stayed narrowed, which the
/// screenshot harness's click check caught. Refusing the toggle at the control keeps the
/// wiring UI rule 8b asks for (a two-way IsChecked and no command) and makes "one is always
/// on" true on screen as well as in the model.
/// </remarks>
public sealed class RadioChip : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle()
    {
        if (IsChecked != true)
        {
            base.Toggle();
        }
    }
}
