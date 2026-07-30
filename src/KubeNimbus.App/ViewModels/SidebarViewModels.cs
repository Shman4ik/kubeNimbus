using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>One sidebar section header (Workloads/Network/Config/Storage/CRDs) with its resource kinds.</summary>
public sealed partial class SidebarSectionViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    public string IconKey { get; } = SidebarGrouping.IconKeyFor(title);

    public ObservableCollection<SidebarKindViewModel> Kinds { get; } = [];

    /// <summary>CRDs tends to dwarf the built-in sections (dozens of kinds) — start it
    /// collapsed so a fresh connection doesn't open on a wall of unfamiliar kinds.</summary>
    [ObservableProperty]
    private bool _isExpanded = title != "CRDs";

    /// <summary>True while a sidebar filter is active and this section has a match —
    /// force-expands the section without touching the user's own collapse choice.</summary>
    [ObservableProperty]
    private bool _isForceExpanded;

    /// <summary>False when a sidebar filter is active and no kind in this section matches.</summary>
    [ObservableProperty]
    private bool _hasVisibleKinds = true;

    public int KindCount => Kinds.Count;

    public bool ShowKinds => IsExpanded || IsForceExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowKinds));

    partial void OnIsForceExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowKinds));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>One browsable resource kind in the sidebar, built from discovery — never hardcoded.</summary>
public sealed partial class SidebarKindViewModel(ResourceDescriptor descriptor, string iconKey) : ObservableObject
{
    public ResourceDescriptor Descriptor { get; } = descriptor;

    public string IconKey { get; } = iconKey;

    public string DisplayName { get; } = Pluralize(descriptor.Kind);

    /// <summary>
    /// True for the synthetic Helm entry, which switches the content area to the
    /// release browser instead of starting a watch (Helm releases aren't an API
    /// kind — see <see cref="SidebarGrouping.HelmReleaseDescriptor"/>).
    /// </summary>
    public bool IsHelmReleases => ReferenceEquals(Descriptor, SidebarGrouping.HelmReleaseDescriptor);

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>False when a sidebar filter is active and this kind doesn't match it.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    private static string Pluralize(string kind) =>
        kind.EndsWith('s') || kind.EndsWith('x') ? kind + "es" : kind + "s";
}
