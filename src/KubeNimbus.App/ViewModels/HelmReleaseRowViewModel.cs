using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One row in the Helm release list (and in a release's history table). Helm
/// releases are read-only here: kubeNimbus never installs, upgrades or rolls
/// back — it shows what the cluster already stores.
/// </summary>
public sealed partial class HelmReleaseRowViewModel : ObservableObject
{
    public HelmRelease Release { get; }

    public string Name => Release.Name;

    public string Namespace => Release.Namespace;

    public string Chart => Release.Chart;

    public string AppVersion => Release.AppVersion;

    public int Revision => Release.Revision;

    public string Status => Release.Status;

    public string Description => Release.Description;

    public DateTimeOffset? Updated => Release.Updated;

    /// <summary>Maps Helm's release status onto the shell's statusDot/pill vocabulary.</summary>
    public string StatusHealth => Release.Status switch
    {
        "deployed" => "ok",
        "superseded" or "uninstalled" => "idle",
        "failed" => "error",
        "pending-install" or "pending-upgrade" or "pending-rollback" or "uninstalling" => "warn",
        _ => "idle",
    };

    /// <summary>True for the row currently shown in the detail panes (history selection).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public HelmReleaseRowViewModel(HelmRelease release) => Release = release;
}
