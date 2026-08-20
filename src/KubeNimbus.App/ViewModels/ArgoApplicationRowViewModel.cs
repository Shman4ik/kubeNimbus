using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One Argo CD Application in the GitOps dashboard's list. It carries <em>two</em>
/// independent states rather than one — sync and health — because that is the whole reason
/// this list exists in place of the ordinary Application list: an Application can be Synced
/// and Degraded (Git applied cleanly, the pods crash) or OutOfSync and Healthy (someone
/// changed the cluster by hand and what they left works), and one Status column cannot say
/// both.
/// </summary>
public sealed partial class ArgoApplicationRowViewModel : ObservableObject
{
    public ArgoApplicationRowViewModel(ArgoApplication application, ResourceDescriptor descriptor, string clusterName = "")
    {
        Application = application;
        Descriptor = descriptor;
        ClusterName = clusterName;
    }

    public ArgoApplication Application { get; }

    /// <summary>
    /// The Application kind's descriptor on this row's own cluster — what a sync or a refresh
    /// patches through. Carried on the row for the same reason the resource list resolves a
    /// row's descriptor rather than the tab's: the same CRD is served at different versions on
    /// different clusters.
    /// </summary>
    public ResourceDescriptor Descriptor { get; }

    public string ClusterName { get; }

    public string Name => Application.Name;

    public string Namespace => Application.Namespace;

    public string Project => Application.Project;

    public string SyncText => Application.Sync.ToString();

    public string HealthText => Application.Health.ToString();

    public string Revision => Application.ShortRevision;

    public string RepoUrl => Application.RepoUrl;

    public string SourceSummary => Application.SourceSummary;

    public string Destination => Application.DestinationNamespace.Length > 0
        ? Application.DestinationNamespace
        : Application.DestinationServer;

    public string SyncPolicy => Application.SyncPolicySummary;

    public bool NeedsAttention => Application.NeedsAttention;

    /// <summary>
    /// The sync pill's colour. Out of sync is <b>warn</b>, never error: an Application that
    /// has drifted from Git is the normal state between a commit and a reconcile, and
    /// colouring every fresh deploy red would teach people to ignore the colour.
    /// </summary>
    public string SyncHealth => Application.Sync switch
    {
        ArgoSyncState.Synced => "ok",
        ArgoSyncState.OutOfSync => "warn",
        _ => "idle",
    };

    /// <summary>The health pill's colour, in the app's own statusDot/pill vocabulary.</summary>
    public string HealthHealth => Application.Health switch
    {
        ArgoHealthState.Healthy => "ok",
        ArgoHealthState.Progressing => "warn",
        ArgoHealthState.Degraded or ArgoHealthState.Missing => "error",
        _ => "idle",
    };

    /// <summary>Hovering a row explains what Argo said, which no pill has room for.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>(4) { $"{Application.Key} · project {Project}" };
            if (RepoUrl.Length > 0)
            {
                lines.Add($"{RepoUrl} @ {SourceSummary}");
            }

            if (Application.HealthMessage.Length > 0)
            {
                lines.Add(Application.HealthMessage);
            }

            if (Application.OperationPhase.Length > 0)
            {
                lines.Add($"Last operation: {Application.OperationPhase}");
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Orders the dashboard: what needs attention first, and within that the worst health
    /// first, so a Degraded Application is never below an OutOfSync one. Ties break on the
    /// key, which is what stops the list reshuffling under the pointer when a poll comes back
    /// with the same set in a different order.
    /// </summary>
    public static int Rank(ArgoApplicationRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.Application.Health switch
        {
            ArgoHealthState.Degraded => 0,
            ArgoHealthState.Missing => 1,
            _ when row.Application.Sync == ArgoSyncState.OutOfSync => 2,
            ArgoHealthState.Unknown => 3,
            ArgoHealthState.Progressing => 4,
            ArgoHealthState.Suspended => 5,
            _ => 6,
        };
    }

    /// <summary>Matches the dashboard's search box — name, namespace, project and repository.</summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Namespace.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Project.Contains(query, StringComparison.OrdinalIgnoreCase)
        || RepoUrl.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the row shown in the detail pane.</summary>
    [ObservableProperty]
    private bool _isSelected;
}
