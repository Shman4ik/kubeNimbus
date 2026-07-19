using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>One sidebar section header (Workloads/Network/Config/Storage/CRDs) with its resource kinds.</summary>
public sealed partial class SidebarSectionViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    public string IconKey { get; } = SidebarGrouping.IconKeyFor(title);

    public ObservableCollection<SidebarKindViewModel> Kinds { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>One browsable resource kind in the sidebar, built from discovery — never hardcoded.</summary>
public sealed partial class SidebarKindViewModel(ResourceDescriptor descriptor, string iconKey) : ObservableObject
{
    public ResourceDescriptor Descriptor { get; } = descriptor;

    public string IconKey { get; } = iconKey;

    public string DisplayName { get; } = Pluralize(descriptor.Kind);

    [ObservableProperty]
    private bool _isSelected;

    private static string Pluralize(string kind) =>
        kind.EndsWith('s') || kind.EndsWith('x') ? kind + "es" : kind + "s";
}
