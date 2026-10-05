using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The order of the Helm release browser. Each column by what it means: the revision as a
/// number, Updated as the instant it prints (ascending is oldest first, as a timestamp reads;
/// an age column would go the other way), Status worst first — failed, then pending, then
/// deployed, then superseded. A tie-break of namespace/name that is not reversed.
/// </summary>
public sealed class HelmReleaseComparer(string column, bool descending) : IComparer<HelmReleaseRowViewModel>
{
    public const string Namespace = "namespace";
    public const string Name = "name";
    public const string Chart = "chart";
    public const string App = "app";
    public const string Revision = "revision";
    public const string Status = "status";
    public const string Updated = "updated";

    /// <summary>namespace/name, the order the browser opens in.</summary>
    public static IComparer<HelmReleaseRowViewModel> ByKey { get; } =
        Comparer<HelmReleaseRowViewModel>.Create(TieBreak);

    public int Compare(HelmReleaseRowViewModel? x, HelmReleaseRowViewModel? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? 1 : -1;
        }

        var result = column switch
        {
            Namespace => ListSort.Text(x.Namespace, y.Namespace),
            Name => ListSort.Text(x.Name, y.Name),
            Chart => ListSort.Text(x.Chart, y.Chart),
            App => ListSort.Text(x.AppVersion, y.AppVersion),
            Revision => x.Revision.CompareTo(y.Revision),
            Status => ListSort.HealthRank(x.StatusHealth).CompareTo(ListSort.HealthRank(y.StatusHealth)) is var rank and not 0
                ? rank
                : ListSort.Text(x.Status, y.Status),
            Updated => ListSort.Instant(x.Updated, y.Updated),
            _ => 0,
        };

        if (descending)
        {
            result = -result;
        }

        return result != 0 ? result : TieBreak(x, y);
    }

    private static int TieBreak(HelmReleaseRowViewModel x, HelmReleaseRowViewModel y)
    {
        var byNamespace = string.CompareOrdinal(x.Namespace, y.Namespace);
        return byNamespace != 0 ? byNamespace : string.CompareOrdinal(x.Name, y.Name);
    }
}

/// <summary>
/// The order of the Argo CD dashboard. Its default is <see cref="ArgoApplicationRowViewModel.Rank"/>,
/// the worst first, as it always was. Sync and Health also put the worst first ascending —
/// OutOfSync above Synced, Degraded above Healthy — and the rest compare as text. A tie-break
/// of the application's key that is not reversed.
/// </summary>
public sealed class ArgoApplicationComparer(string column, bool descending) : IComparer<ArgoApplicationRowViewModel>
{
    public const string Name = "name";
    public const string Project = "project";
    public const string Sync = "sync";
    public const string Health = "health";
    public const string Revision = "revision";
    public const string Destination = "destination";
    public const string Source = "source";
    public const string Policy = "policy";

    /// <summary>Most urgent first, then by key: the order the dashboard opens in.</summary>
    public static IComparer<ArgoApplicationRowViewModel> ByRank { get; } =
        Comparer<ArgoApplicationRowViewModel>.Create((x, y) =>
            ArgoApplicationRowViewModel.Rank(x).CompareTo(ArgoApplicationRowViewModel.Rank(y)) is var rank and not 0
                ? rank
                : string.CompareOrdinal(x.Application.Key, y.Application.Key));

    public int Compare(ArgoApplicationRowViewModel? x, ArgoApplicationRowViewModel? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? 1 : -1;
        }

        var result = column switch
        {
            Name => ListSort.Text(x.Name, y.Name),
            Project => ListSort.Text(x.Project, y.Project),
            Sync => SyncRank(x.Application.Sync).CompareTo(SyncRank(y.Application.Sync)),
            Health => HealthRank(x.Application.Health).CompareTo(HealthRank(y.Application.Health)),
            Revision => ListSort.Text(x.Revision, y.Revision),
            Destination => ListSort.Text(x.Destination, y.Destination),
            Source => ListSort.Text(x.SourceSummary, y.SourceSummary),
            Policy => ListSort.Text(x.SyncPolicy, y.SyncPolicy),
            _ => 0,
        };

        if (descending)
        {
            result = -result;
        }

        return result != 0 ? result : string.CompareOrdinal(x.Application.Key, y.Application.Key);
    }

    private static int SyncRank(ArgoSyncState sync) => sync switch
    {
        ArgoSyncState.OutOfSync => 0,
        ArgoSyncState.Unknown => 1,
        _ => 2,
    };

    private static int HealthRank(ArgoHealthState health) => health switch
    {
        ArgoHealthState.Degraded => 0,
        ArgoHealthState.Missing => 1,
        ArgoHealthState.Unknown => 2,
        ArgoHealthState.Progressing => 3,
        ArgoHealthState.Suspended => 4,
        _ => 5,
    };
}

/// <summary>The comparisons the list comparers share.</summary>
internal static class ListSort
{
    /// <summary>Text, case-insensitively; an empty value is "no value" and sorts after the rest.</summary>
    public static int Text(string x, string y)
    {
        if (x.Length == 0 || y.Length == 0)
        {
            return x.Length == y.Length ? 0 : x.Length == 0 ? 1 : -1;
        }

        var result = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        return result != 0 ? result : string.CompareOrdinal(x, y);
    }

    /// <summary>Instants, earliest first; a missing one sorts after the rest.</summary>
    public static int Instant(DateTimeOffset? x, DateTimeOffset? y) =>
        x is null || y is null
            ? x is null && y is null ? 0 : x is null ? 1 : -1
            : x.Value.CompareTo(y.Value);

    /// <summary>The shell's health words, worst first.</summary>
    public static int HealthRank(string health) => health switch
    {
        ResourceHealth.Error => 0,
        ResourceHealth.Warn => 1,
        ResourceHealth.Ok => 2,
        _ => 3,
    };
}
