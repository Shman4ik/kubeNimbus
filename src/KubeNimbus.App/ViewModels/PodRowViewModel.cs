using CommunityToolkit.Mvvm.ComponentModel;
using k8s.Models;

namespace KubeNimbus.App.ViewModels;

/// <summary>One row in the live pod list. Updated in place as watch events arrive.</summary>
public sealed partial class PodRowViewModel : ObservableObject
{
    public string Key { get; }

    [ObservableProperty]
    private string _namespace;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _phase;

    [ObservableProperty]
    private string _ready;

    [ObservableProperty]
    private int _restarts;

    [ObservableProperty]
    private string _node;

    public PodRowViewModel(V1Pod pod)
    {
        Key = KeyFor(pod);
        _namespace = pod.Metadata?.NamespaceProperty ?? "";
        _name = pod.Metadata?.Name ?? "";
        _phase = "";
        _ready = "";
        _node = "";
        Update(pod);
    }

    public static string KeyFor(V1Pod pod) =>
        $"{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}";

    public void Update(V1Pod pod)
    {
        Phase = pod.Status?.Phase ?? "Unknown";
        Node = pod.Spec?.NodeName ?? "";

        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is { Count: > 0 })
        {
            var readyCount = statuses.Count(s => s.Ready);
            Ready = $"{readyCount}/{statuses.Count}";
            Restarts = statuses.Sum(s => s.RestartCount);
        }
        else
        {
            Ready = "0/0";
            Restarts = 0;
        }
    }
}
