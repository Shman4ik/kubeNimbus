using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.ViewModels;

/// <summary>The Applications list's columns, as a header click names them.</summary>
public enum ApplicationSortColumn
{
    Health,
    Name,
    Namespace,
    Pods,
    Restarts,
    LastDeploy,
    Sync,
}

/// <summary>
/// The order a header click puts the Applications list in. The same two rules as the
/// resource grid's <see cref="ResourceRowComparer"/>: a column is compared by what it means
/// (restarts as a count, the last deploy as an instant, pods as the Ready fraction), and a
/// row with no value sorts after every row that has one in ascending order, and by plain
/// negation before them in descending order.
/// </summary>
/// <remarks>
/// Ascending means "worst first" wherever a column is a verdict: Health follows
/// <see cref="AppStatus"/>'s urgency order, Pods puts the app shortest of Ready replicas on
/// top, and Sync puts OutOfSync above Synced. Last deploy prints an age, so ascending is the
/// most recent deploy first, as the resource grid's Age is. The tie-break (name, then key) is
/// not reversed with the direction: it makes the order total, and a tie-break that flipped
/// would move rows the sorted column says nothing about.
/// </remarks>
public sealed class ApplicationRowComparer(ApplicationSortColumn column, bool descending) : IComparer<ApplicationRowViewModel>
{
    public int Compare(ApplicationRowViewModel? x, ApplicationRowViewModel? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        var result = CompareColumn(x, y);
        if (descending)
        {
            result = -result;
        }

        if (result != 0)
        {
            return result;
        }

        var byName = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(x.Key, y.Key);
    }

    private int CompareColumn(ApplicationRowViewModel x, ApplicationRowViewModel y) => column switch
    {
        ApplicationSortColumn.Health => x.Status.CompareTo(y.Status),
        ApplicationSortColumn.Name => Text(x.Name, y.Name),
        ApplicationSortColumn.Namespace => Text(FirstNamespace(x), FirstNamespace(y)),
        ApplicationSortColumn.Pods => Number(ReadyFraction(x.Assessment), ReadyFraction(y.Assessment)),
        ApplicationSortColumn.Restarts => x.Assessment.Restarts.CompareTo(y.Assessment.Restarts),
        ApplicationSortColumn.LastDeploy => -Instant(x.Assessment.LastDeploy?.At, y.Assessment.LastDeploy?.At),
        ApplicationSortColumn.Sync => Number(SyncRank(x), SyncRank(y)),
        _ => 0,
    };

    private static string FirstNamespace(ApplicationRowViewModel row) => row.Namespaces.Count > 0 ? row.Namespaces[0] : "";

    /// <summary>
    /// "2/3" as 0.667, so the app short of replicas comes first rather than "10/10" sorting
    /// above "2/3" as text. Scaled to zero ("0/0") is fully Ready: nothing it asked for is
    /// missing. Nothing that runs a fixed number of replicas ("—") has no value.
    /// </summary>
    private static double? ReadyFraction(ApplicationAssessment assessment) =>
        assessment.Desired switch
        {
            < 0 => null,
            0 => 1,
            var desired => (double)assessment.Ready / desired,
        };

    /// <summary>OutOfSync, then Unknown, then Synced; an app outside Argo CD has no sync state.</summary>
    private static double? SyncRank(ApplicationRowViewModel row) =>
        row.Entry.Argo is { } argo
            ? argo.Sync switch
            {
                ArgoSyncState.OutOfSync => 0,
                ArgoSyncState.Unknown => 1,
                _ => 2,
            }
            : null;

    private static int Text(string x, string y)
    {
        if (x.Length == 0 || y.Length == 0)
        {
            return x.Length == y.Length ? 0 : x.Length == 0 ? 1 : -1;
        }

        var result = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        return result != 0 ? result : string.CompareOrdinal(x, y);
    }

    private static int Number(double? x, double? y) =>
        x is null || y is null
            ? x is null && y is null ? 0 : x is null ? 1 : -1
            : x.Value.CompareTo(y.Value);

    private static int Instant(DateTimeOffset? x, DateTimeOffset? y) =>
        x is null || y is null
            ? x is null && y is null ? 0 : x is null ? -1 : 1
            : x.Value.CompareTo(y.Value);
}

/// <summary>
/// One row of the Applications namespace picker: a namespace some application runs in, how
/// many do, and whether it is chosen. <see cref="IsChecked"/> is written by the view model
/// only — the row draws a check, not a CheckBox, so a click on the box and a click on the row
/// cannot both toggle it (UI rule 8b's double flip, by another route).
/// </summary>
public sealed partial class ApplicationNamespaceChoice(string name, int count, bool isAll = false) : ObservableObject
{
    public string Name { get; } = name;

    public int Count { get; } = count;

    /// <summary>The All namespaces row, which clears the selection rather than joining it.</summary>
    public bool IsAll { get; } = isAll;

    [ObservableProperty]
    private bool _isChecked;

    public string CountText => Count == 0 ? "" : Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The row's accessible name, which says whether it is chosen: the check is drawn, not a control a reader can query.</summary>
    public override string ToString() =>
        (Count == 0 ? Name : $"{Name}, {Count} application{(Count == 1 ? "" : "s")}") + (IsChecked ? ", chosen" : "");
}
