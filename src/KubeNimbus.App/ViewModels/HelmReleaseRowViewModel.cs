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

    /// <summary>The instant itself, which the Updated column sorts on (never on <see cref="UpdatedText"/>).</summary>
    public DateTimeOffset? Updated => Release.Updated;

    /// <summary>
    /// The Updated column's text: an age ("5m", "3d"), the way the resource list's Age column
    /// reads, through the same <see cref="RelativeTime.Compact"/>. It used to print the
    /// <c>DateTimeOffset</c> itself, the widest thing the column could hold, which at 1280px
    /// was cut in the middle of its offset ("07/20/2026 08:41:02 +00:", FEAT-72). "—" says Helm
    /// recorded no time for the revision. Recomputed by <see cref="RefreshTimes"/> off the
    /// cluster tab's shared clock, since an age changes with nothing else changing.
    /// </summary>
    [ObservableProperty]
    private string _updatedText = "";

    /// <summary>The exact instant, local time, on the cell's tooltip — as Age's "Created …" is.</summary>
    public string UpdatedTooltip => Updated is { } at
        ? $"Updated {at.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : "Helm recorded no time for this revision";

    public void RefreshTimes(DateTimeOffset now) =>
        UpdatedText = Updated is { } at ? RelativeTime.Compact(now - at) : "—";

    /// <summary>Maps Helm's release status onto the shell's statusDot/pill vocabulary.</summary>
    public string StatusHealth => Release.Status switch
    {
        "deployed" => "ok",
        "superseded" or "uninstalled" => "idle",
        "failed" => "error",
        "pending-install" or "pending-upgrade" or "pending-rollback" or "uninstalling" => "warn",
        ClusterClient.UnreadableStatus => "warn",
        _ => "idle",
    };

    /// <summary>True for the row currently shown in the detail panes (history selection).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public HelmReleaseRowViewModel(HelmRelease release)
    {
        Release = release;
        RefreshTimes(DateTimeOffset.UtcNow);
    }
}
