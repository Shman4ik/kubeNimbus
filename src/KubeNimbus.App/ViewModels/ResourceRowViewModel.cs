using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One row in the generic live list view — works for any resource kind (built-in
/// or CRD), updated in place as watch events arrive. The columns are the ones
/// kubectl shows (Namespace/Name/Ready/Status/Restarts/Age, plus a kind-specific
/// Details); which of them apply to the selected kind is decided by
/// <see cref="ResourceStatusSummary"/> and applied in
/// <see cref="Views.ClusterTabView"/>'s code-behind.
///
/// <para>
/// A CRD gets a second set on top of that: the columns it declares for itself in
/// <c>additionalPrinterColumns</c>, landing in <see cref="PrinterCells"/>. Those
/// replace the generic Status/Details pair rather than joining it, because they are
/// the CRD author's own answer to the same question and kubectl shows no other.
/// Built-in kinds never have any — they are not CustomResourceDefinitions — so
/// nothing about a Pod, Deployment, Node or Event list changes.
/// </para>
/// </summary>
public sealed partial class ResourceRowViewModel : ObservableObject
{
    public string Key { get; }

    /// <summary>
    /// Which cluster served this row, in the aggregated fleet view; empty for an
    /// ordinary single-cluster list. Also part of <see cref="Key"/>, because the same
    /// namespace/name exists on every cluster in a fleet and two of those are two rows.
    /// </summary>
    public string ClusterName { get; }

    [ObservableProperty]
    private DynamicResource _resource;

    [ObservableProperty]
    private string _namespace;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private string _statusHealth = ResourceHealth.Idle; // -> Ellipse.statusDot / Border.statusPill class

    /// <summary>kubectl's READY column ("2/3") — empty for kinds with no readiness notion.</summary>
    [ObservableProperty]
    private string _readyText = "";

    /// <summary>
    /// kubectl's RESTARTS column, including its "(43m ago)" suffix. A restart count
    /// with no age is nearly useless: 200 restarts that stopped yesterday and 200
    /// still accumulating are the same number and completely different problems.
    /// </summary>
    [ObservableProperty]
    private string _restartsText = "0";

    /// <summary>
    /// What kubectl shows in place of a status for kinds that have none — a Service's
    /// type/cluster-IP/ports, a ConfigMap's key count. Empty (and its column hidden)
    /// for kinds whose Status column already carries the story.
    /// </summary>
    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private DateTimeOffset? _createdAt;

    /// <summary>
    /// Compact age ("5m", "2h", "3d", "21d"). Recomputed by
    /// <see cref="RefreshTimes"/> off the list view's shared clock rather than
    /// stored, because age changes with no watch event to trigger it.
    /// </summary>
    [ObservableProperty]
    private string _ageText = "";

    /// <summary>The exact creation timestamp, for the Age cell's tooltip — the
    /// compact form is for scanning, but "when exactly" is a real question.</summary>
    [ObservableProperty]
    private string _ageTooltip = "";

    private int _restarts;
    private DateTimeOffset? _lastRestartAt;

    /// <summary>
    /// kubectl's RESTARTS count as a number. <see cref="RestartsText"/> is the rendered
    /// form and carries "(43m ago)" with it, so sorting by the column has to read this
    /// instead — "10" sorts above "9" as text.
    /// </summary>
    public int Restarts => _restarts;

    /// <summary>
    /// The most recent metrics reading, or null when this kind has none, the cluster
    /// runs no metrics-server, or the sample has not landed yet. Null rather than zero
    /// for the reason the sparkline breaks its line across gaps: a subject that reports
    /// nothing is not a subject using nothing.
    /// </summary>
    public long? LatestCpuNanocores => History.Latest?.CpuNanocores;

    public long? LatestMemoryBytes => History.Latest?.MemoryBytes;

    /// <summary>
    /// The instant behind a <c>type: date</c> printer cell, whose text is an age off the
    /// shared timer. Sorting that column by its text would order "9m" above "5d".
    /// </summary>
    public DateTimeOffset? PrinterDate(int index) =>
        index >= 0 && index < PrinterCellCount ? _printerDates[index] : null;

    /// <summary>
    /// The cells for the selected kind's CRD-declared printer columns, in the order
    /// <see cref="ClusterTabViewModel.VisiblePrinterColumns"/> lists them. A fixed
    /// array of small observables rather than a variable list, because the grid's
    /// printer columns are declared in XAML with compiled bindings
    /// (<c>{Binding PrinterCells[3].Text}</c>) — a DataGridColumn is outside the visual
    /// tree, so building the columns in code would mean building their bindings in code
    /// too, and a code-built binding is a reflection binding, which NativeAOT is exactly
    /// what this repo cannot accept. Slots past the current column count stay empty and
    /// their columns stay hidden.
    /// </summary>
    public PrinterCellViewModel[] PrinterCells { get; } =
        [.. Enumerable.Range(0, PrinterCellCount).Select(_ => new PrinterCellViewModel())];

    /// <summary>
    /// How many CRD printer columns the list can draw at once. Above every real CRD
    /// surveyed while building this: the widest found (KEDA's ScaledObject, eleven
    /// declared) needs nine once its own Age column is folded into the list's.
    /// </summary>
    public const int PrinterCellCount = 10;

    private IReadOnlyList<PrinterColumn> _printerColumns = [];

    /// <summary>
    /// The timestamps behind any <c>type: date</c> printer cells, so the shared age
    /// timer can re-render them. Without this a "Last Run" or "Expires" column would
    /// freeze at whatever it said when the last watch event arrived — the exact bug the
    /// list's own Age column has a timer to avoid.
    /// </summary>
    private readonly DateTimeOffset?[] _printerDates = new DateTimeOffset?[PrinterCellCount];

    /// <summary>
    /// Measured CPU/memory for this row, when the kind has any (pods and nodes)
    /// and the cluster runs metrics-server. Rendered as text because the columns
    /// are display-only; "—" is the deliberate stand-in for "not reported yet",
    /// which is different from zero.
    /// </summary>
    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _memoryText = "—";

    /// <summary>
    /// This row's rolling usage window (30 min at the 15s poll cadence), behind the
    /// sparkline drawn in the CPU/Memory cells. Lives per row so it survives watch
    /// events (which update the row in place) and dies with the row when the kind or
    /// namespace changes — exactly the lifetime the graph should have.
    /// </summary>
    public UsageHistory History { get; } = new();

    /// <summary>
    /// Chart series, re-published as a fresh array on each poll: the chart binds to
    /// the property, and a ring buffer mutated in place raises no change
    /// notification. 120 doubles per row per poll is cheaper than any observable
    /// collection plumbing would be.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<double?> _cpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<double?> _memorySeries = [];

    [ObservableProperty]
    private string _cpuTooltip = "No CPU samples yet.";

    [ObservableProperty]
    private string _memoryTooltip = "No memory samples yet.";

    public ResourceRowViewModel(DynamicResource resource, string clusterName = "")
    {
        ClusterName = clusterName;
        Key = KeyFor(clusterName, resource.Key);
        _resource = resource;
        _namespace = resource.Namespace ?? "";
        _name = resource.Name;
        _status = "";
        Update(resource);
    }

    /// <summary>
    /// Row identity: the resource's own <c>namespace/name</c> key, qualified by cluster
    /// when there is one. Shared with the metrics poll so its samples land on the right
    /// rows in fleet mode.
    /// </summary>
    public static string KeyFor(string clusterName, string resourceKey) =>
        clusterName.Length == 0 ? resourceKey : $"{clusterName}/{resourceKey}";

    public void Update(DynamicResource resource)
    {
        Resource = resource;
        Namespace = resource.Namespace ?? "";
        Name = resource.Name;
        CreatedAt = resource.CreationTimestamp;
        AgeTooltip = resource.CreationTimestamp is { } created
            ? $"Created {created.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "";

        var summary = ResourceStatusSummary.Summarize(resource);
        Status = summary.Status;
        StatusHealth = summary.Health;
        ReadyText = summary.Ready;
        Details = summary.Details;
        _restarts = summary.Restarts;
        _lastRestartAt = summary.LastRestartAt;
        RefreshPrinterCells();
        RefreshTimes();
    }

    /// <summary>
    /// Points this row at the printer columns the selected kind declares. Called once
    /// when the row is created and again whenever the set changes — which is the
    /// advanced-view switch adding or removing the CRD's own <c>priority: 1</c>
    /// columns, and nothing else. Re-evaluating is pure JSON reading over the object
    /// the row already holds: no fetch, no watch restart, no lost state, which is what
    /// keeps that switch a display switch.
    /// </summary>
    public void SetPrinterColumns(IReadOnlyList<PrinterColumn> columns)
    {
        _printerColumns = columns;
        RefreshPrinterCells();
    }

    private void RefreshPrinterCells()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < PrinterCellCount; i++)
        {
            if (i >= _printerColumns.Count)
            {
                _printerDates[i] = null;
                PrinterCells[i].Text = "";
                continue;
            }

            var column = _printerColumns[i];
            _printerDates[i] = PrinterColumns.DateValue(column, Resource.Raw);
            PrinterCells[i].Text = PrinterColumns.Evaluate(column, Resource.Raw, now);
        }
    }

    /// <summary>
    /// The list's name filter: case-insensitive substring over the fields that
    /// <em>identify</em> the object — name, namespace, and the cluster in fleet mode.
    /// Deliberately not the status: "Running" matches most of a healthy list, and what
    /// people type into a search box is a name they half-remember. Namespace is in
    /// because "All namespaces" is the default and "demo-shop" is how you narrow it
    /// without leaving the list.
    ///
    /// <para>
    /// A CRD's printer cells are out for the same reason the status is. They are the
    /// same *kind* of content — "True", "Ready", "1.15.2", a replica count — so
    /// including them would make one-letter queries match most of a list, and would
    /// change what the box matches from kind to kind, which is worse than either
    /// answer on its own. The identity fields are the ones that mean the same thing
    /// everywhere.
    /// </para>
    /// </summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Namespace.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (ClusterName.Length > 0 && ClusterName.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Recomputes the two cells whose text is a function of wall-clock rather than of
    /// the object — Age, and the "(43m ago)" on Restarts. Driven by one shared timer
    /// in <see cref="Views.ClusterTabView"/>: a timer per row would mean thousands of
    /// them on a busy cluster. Assignments are no-ops when the rendered string hasn't
    /// changed (<c>ObservableObject.SetProperty</c> compares first), so a tick over a
    /// list of day-old pods raises no change notifications at all.
    /// </summary>
    public void RefreshTimes()
    {
        var now = DateTimeOffset.UtcNow;
        AgeText = CreatedAt is { } created ? RelativeTime.Compact(now - created) : "";
        RestartsText = _restarts == 0
            ? "0"
            : _lastRestartAt is { } last
                ? $"{_restarts} ({RelativeTime.Compact(now - last)} ago)"
                : $"{_restarts}";

        // A CRD's `type: date` columns are ages too, and a watch event is not what
        // makes them change either.
        for (var i = 0; i < PrinterCellCount; i++)
        {
            if (_printerDates[i] is { } at)
            {
                PrinterCells[i].Text = RelativeTime.Compact(now - at);
            }
        }
    }

    /// <summary>
    /// Applies one metrics.k8s.io sample; nulls render as "—". <paramref name="at"/>
    /// defaults to now — it is only passed explicitly by the screenshot harness,
    /// which needs a realistic time axis without waiting out real poll intervals.
    /// </summary>
    public void ApplyUsage(long? cpuNanocores, long? memoryBytes, DateTimeOffset? at = null)
    {
        History.Add(cpuNanocores, memoryBytes, at);
        CpuText = Quantity.FormatCpu(cpuNanocores);
        MemoryText = Quantity.FormatMemory(memoryBytes);
        RefreshSeries();
    }

    /// <summary>
    /// Records that this poll had no sample for the row (the pod is too young to be
    /// aggregated, or it vanished from the metrics response). The reading goes into
    /// the history as a gap rather than a zero, and rather than being dropped — a
    /// subject that stopped reporting should break the line, not shift the graph.
    /// </summary>
    public void ClearUsage(DateTimeOffset? at = null)
    {
        History.Add(null, null, at);
        CpuText = "—";
        MemoryText = "—";
        RefreshSeries();
    }

    private void RefreshSeries()
    {
        CpuSeries = History.CpuSeries();
        MemorySeries = History.MemorySeries();
        CpuTooltip = UsageFormat.Tooltip("CPU", CpuText, Quantity.FormatCpu(History.PeakCpuNanocores), History);
        MemoryTooltip = UsageFormat.Tooltip("Mem", MemoryText, Quantity.FormatMemory(History.PeakMemoryBytes), History);
    }
}

/// <summary>
/// One CRD printer-column cell on one row. A tiny observable of its own rather than a
/// string on the row, because the grid's printer columns are fixed slots bound with
/// compiled bindings (<c>{Binding PrinterCells[2].Text}</c>): indexing an array is not
/// itself observable, so the change notification has to come from the element.
/// </summary>
public sealed partial class PrinterCellViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = "";
}
