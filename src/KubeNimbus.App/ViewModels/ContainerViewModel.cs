using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>One container row in the pod detail view (spec + live status merged).</summary>
public sealed partial class ContainerViewModel(string name, string image) : ObservableObject
{
    public string Name { get; } = name;

    public string Image { get; } = image;

    [ObservableProperty]
    private bool _ready;

    [ObservableProperty]
    private int _restartCount;

    [ObservableProperty]
    private string _state = "Unknown";

    [ObservableProperty]
    private IReadOnlyList<int> _tcpPorts = [];

    /// <summary>Live usage readout from metrics.k8s.io ("120m · 84Mi"), empty when unavailable.</summary>
    [ObservableProperty]
    private string _usageDisplay = "";
}
