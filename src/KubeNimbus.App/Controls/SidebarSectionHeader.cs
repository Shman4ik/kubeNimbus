using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace KubeNimbus.App.Controls;

/// <summary>
/// A sidebar section's header row: the <see cref="Border"/> it always was, drawn by the same
/// <c>Border.sectionHeaderRow</c> styles, plus an automation peer with the ExpandCollapse
/// pattern (#236). A screen reader used to hear only the section's title, with nothing to
/// say whether its kinds were showing, and UI Automation could not fold or unfold it.
/// </summary>
/// <remarks>
/// <see cref="IsExpanded"/> is bound one way to the section's <c>ShowKinds</c>, which is
/// what the reader sees (a filter force-expands a folded section); expanding and collapsing
/// through the pattern go through <see cref="ExpandRequested"/> to the section's own
/// <c>IsExpanded</c>, the same flag a click toggles.
/// </remarks>
public sealed class SidebarSectionHeader : Border
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SidebarSectionHeader, bool>(nameof(IsExpanded));

    static SidebarSectionHeader()
    {
        IsExpandedProperty.Changed.AddClassHandler<SidebarSectionHeader>((header, e) =>
        {
            if (ControlAutomationPeer.FromElement(header) is SectionHeaderPeer peer)
            {
                peer.RaiseStateChanged((bool)e.OldValue!, (bool)e.NewValue!);
            }
        });
    }

    /// <summary>Whether the section's kinds are showing.</summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Raised with the state UI Automation asked for.</summary>
    public event EventHandler<bool>? ExpandRequested;

    /// <summary>Drawn as a <see cref="Border"/>: the type selector in the styles stays the same.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    protected override AutomationPeer OnCreateAutomationPeer() => new SectionHeaderPeer(this);

    private sealed class SectionHeaderPeer(SidebarSectionHeader owner) : ControlAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState =>
            owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public bool ShowsMenu => false;

        public void Expand() => owner.ExpandRequested?.Invoke(owner, true);

        public void Collapse() => owner.ExpandRequested?.Invoke(owner, false);

        internal void RaiseStateChanged(bool oldValue, bool newValue) =>
            RaisePropertyChangedEvent(
                ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                oldValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed,
                newValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);

        // A header the reader can fold, not a pane: UIA's own type for a header row with
        // an expand/collapse state in a list is a group header, reported as Group.
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
