using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The stable identifiers for the resource list's columns.
///
/// <para>
/// Every column in <c>ClusterTabView</c> carries one as its <c>Tag</c>, and everything
/// that has to name a column — showing and hiding it from code-behind, remembering a
/// dragged width, saying which column the list is sorted by — names it by this id
/// rather than by its header text. Header text used to be the identifier, and it was
/// the wrong one twice over: a CRD's printer column is a *CRD author's* string, so
/// cert-manager calling one of its Certificate columns "Ready" made the grid's own
/// Ready-column match fire and hide it; and the sort indicator is drawn into the
/// header, so the header is no longer a constant even for the app's own columns.
/// </para>
/// </summary>
public static class ResourceColumn
{
    /// <summary>The 28px health dot. Not sortable and never resized — it is one glyph.</summary>
    public const string Health = "health";

    public const string Cluster = "cluster";
    public const string Namespace = "namespace";
    public const string Name = "name";
    public const string Ready = "ready";
    public const string Status = "status";
    public const string Details = "details";
    public const string Restarts = "restarts";
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Age = "age";

    /// <summary>
    /// A CRD printer column, by the CRD author's own name for it.
    ///
    /// <para>
    /// The grid's printer columns are ten fixed <em>slots</em>, so the obvious id would
    /// be the slot number — and it would be wrong in the one place it matters. The
    /// advanced view is this app's <c>-o wide</c>: turning it on brings a CRD's
    /// <c>priority: 1</c> columns into the same list, in declaration order, so every
    /// slot after the first of them means a different column than it did a moment
    /// before. A width or a sort keyed by slot would silently move to a neighbour when
    /// that switch is flipped. The name is what does not move.
    /// </para>
    /// </summary>
    public static string Printer(string columnName) => $"crd:{columnName}";

    /// <summary>The CRD column name behind <see cref="Printer"/>, or null for anything else.</summary>
    public static string? PrinterName(string columnId) =>
        columnId.StartsWith("crd:", StringComparison.Ordinal) ? columnId[4..] : null;
}

/// <summary>
/// Orders the list's rows by one column.
///
/// <para>
/// This is a <em>view</em> concern and nothing else: it orders
/// <see cref="ClusterTabViewModel.VisibleRows"/> and never touches
/// <see cref="ClusterTabViewModel.Rows"/>, which stays the informer's own list in
/// arrival order (UI rule 13). Sorting the watch's list would break the same invariant
/// filtering it would.
/// </para>
///
/// <para>
/// Two rules run through every comparison below. A column is compared by <b>what it
/// means</b>, not by the string it renders — restarts as a number, age as an instant,
/// CPU as nanocores — because "10" sorts before "9" as text and a 2-hour-old pod sorts
/// before a 2-day-old one for the same reason. And a row with <b>no value</b> for the
/// sorted column sorts after everything else in ascending order (and, by plain
/// negation, before everything in descending order), rather than being treated as a
/// zero: a pod that reports no CPU is not a pod using none.
/// </para>
/// </summary>
public sealed class ResourceRowComparer(
    string columnId,
    bool descending,
    IReadOnlyList<PrinterColumn> printerColumns) : IComparer<ResourceRowViewModel>
{
    /// <summary>
    /// Whether this column can order the list as things currently stand. Everything the
    /// app declares always can; a CRD column can only while the kind in front of you
    /// declares it — a sort remembered for a Certificate list is meaningless the moment
    /// the CRD stops declaring that column, and ordering by a column nobody can see
    /// would look like the list had shuffled itself.
    /// </summary>
    public static bool CanSort(string columnId, IReadOnlyList<PrinterColumn> printerColumns) =>
        ResourceColumn.PrinterName(columnId) is not { } name
        || printerColumns.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    public int Compare(ResourceRowViewModel? x, ResourceRowViewModel? y)
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

        // The tie-break is deliberately *not* reversed with the direction: it exists to
        // make the order total (two pods with the same status must not swap places on
        // an unrelated watch tick), and a tie-break that flipped would make the list
        // jump when the arrow is clicked for reasons the sorted column cannot explain.
        return result != 0 ? result : string.CompareOrdinal(x.Key, y.Key);
    }

    private int CompareColumn(ResourceRowViewModel x, ResourceRowViewModel y)
    {
        if (ResourceColumn.PrinterName(columnId) is { } printerName)
        {
            return ComparePrinter(x, y, printerName);
        }

        return columnId switch
        {
            ResourceColumn.Cluster => Text(x.ClusterName, y.ClusterName),
            ResourceColumn.Namespace => Text(x.Namespace, y.Namespace),
            ResourceColumn.Name => Text(x.Name, y.Name),
            ResourceColumn.Status => Text(x.Status, y.Status),
            ResourceColumn.Details => Text(x.Details, y.Details),
            ResourceColumn.Ready => Number(ReadyFraction(x.ReadyText), ReadyFraction(y.ReadyText)),
            ResourceColumn.Restarts => x.Restarts.CompareTo(y.Restarts),
            ResourceColumn.Cpu => Number(x.LatestCpuNanocores, y.LatestCpuNanocores),
            ResourceColumn.Memory => Number(x.LatestMemoryBytes, y.LatestMemoryBytes),

            // Ascending Age means the *smallest age* first, which is the newest object —
            // so the instants compare the other way round from the number people read.
            ResourceColumn.Age => -Instant(x.CreatedAt, y.CreatedAt),
            _ => 0,
        };
    }

    private int ComparePrinter(ResourceRowViewModel x, ResourceRowViewModel y, string printerName)
    {
        var index = -1;
        for (var i = 0; i < printerColumns.Count; i++)
        {
            if (string.Equals(printerColumns[i].Name, printerName, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return 0;
        }

        var type = printerColumns[index].Type;

        // A `type: date` cell renders as an age off the shared timer, so its text is
        // "5d" and sorting it as text is meaningless; the instant behind it is what the
        // row keeps for exactly this kind of reason.
        if (string.Equals(type, "date", StringComparison.OrdinalIgnoreCase))
        {
            return -Instant(x.PrinterDate(index), y.PrinterDate(index));
        }

        var left = x.PrinterCells[index].Text;
        var right = y.PrinterCells[index].Text;

        if (string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "number", StringComparison.OrdinalIgnoreCase))
        {
            return Number(ParseNumber(left), ParseNumber(right));
        }

        return Text(left, right);
    }

    private static int Text(string x, string y)
    {
        // Empty is "no value", not "the smallest string": an unset cell belongs at the
        // end of an ascending sort with the other unknowns, not above every name.
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

    /// <summary>
    /// kubectl's READY column as a fraction ("2/3" → 0.667), which is what puts the
    /// pods that are short of replicas at the top rather than sorting "10/10" above
    /// "2/3" as text. Anything that is not a ratio (a Job's "1/1" is; a blank is not)
    /// comes back null and sorts with the unknowns.
    /// </summary>
    private static double? ReadyFraction(string ready)
    {
        var slash = ready.IndexOf('/');
        if (slash <= 0
            || !double.TryParse(ready.AsSpan(0, slash), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var have)
            || !double.TryParse(ready.AsSpan(slash + 1), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var want))
        {
            return null;
        }

        return want <= 0 ? 0 : have / want;
    }

    private static double? ParseNumber(string text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
