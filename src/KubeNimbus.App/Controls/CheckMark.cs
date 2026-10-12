using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace KubeNimbus.App.Controls;

/// <summary>
/// A drawn check box: the <see cref="Border"/> the namespace pickers draw their check in,
/// with an automation peer that UI Automation reads as a check box with the Toggle pattern
/// (#236). The pickers draw the check rather than use a <see cref="CheckBox"/> so the view
/// model stays the one writer of the state, and the drawn box was not in the UIA tree at
/// all, so its state could be neither read nor set.
/// </summary>
/// <remarks>
/// <see cref="IsChecked"/> is bound one way from the view model. A Toggle through the
/// pattern raises <see cref="ToggleRequested"/>, which the view answers the way it answers a
/// click on the box, and the state the reader then gets is the one the view model kept.
/// </remarks>
public sealed class CheckMark : Border
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<CheckMark, bool>(nameof(IsChecked));

    static CheckMark()
    {
        IsCheckedProperty.Changed.AddClassHandler<CheckMark>((mark, e) =>
        {
            if (ControlAutomationPeer.FromElement(mark) is CheckMarkPeer peer)
            {
                peer.RaiseStateChanged((bool)e.OldValue!, (bool)e.NewValue!);
            }
        });
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    /// <summary>Raised when UI Automation toggles the box.</summary>
    public event EventHandler? ToggleRequested;

    protected override Type StyleKeyOverride => typeof(Border);

    protected override AutomationPeer OnCreateAutomationPeer() => new CheckMarkPeer(this);

    private sealed class CheckMarkPeer(CheckMark owner) : ControlAutomationPeer(owner), IToggleProvider
    {
        public ToggleState ToggleState => owner.IsChecked ? ToggleState.On : ToggleState.Off;

        public void Toggle() => owner.ToggleRequested?.Invoke(owner, EventArgs.Empty);

        internal void RaiseStateChanged(bool oldValue, bool newValue) =>
            RaisePropertyChangedEvent(
                TogglePatternIdentifiers.ToggleStateProperty,
                oldValue ? ToggleState.On : ToggleState.Off,
                newValue ? ToggleState.On : ToggleState.Off);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
