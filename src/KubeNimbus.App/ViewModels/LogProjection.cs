namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Which buffered lines a log pane shows, kept incrementally as lines arrive and are
/// trimmed. Shared by pod detail's pane and the multi-pod pane, which used to carry the
/// same filter, rebuild and trim code twice.
/// </summary>
/// <remarks>
/// <para>
/// A line is <em>admitted</em> when the pane's own narrowing lets it through (its pod, its
/// level, Errors only) — that part is the pane's, passed in as a predicate. It is
/// <em>shown</em> when it is admitted and, while the search filters, when it matches or is
/// within <see cref="Context"/> admitted lines of a line that does: <c>grep -C</c>, the one
/// thing a filter alone could not do and the reason a filtered error used to be copied out
/// to an editor to see what led up to it.
/// </para>
/// <para>
/// Context is kept as lines stream in, without a rebuild: the last <see cref="Context"/>
/// admitted lines that were not shown wait in a small queue, a match shows them and itself,
/// and the next <see cref="Context"/> admitted lines after it are shown as its trailing
/// context. A line shown after a hidden run carries <see cref="LogLineViewModel.IsGapBefore"/>
/// (the view draws grep's <c>--</c> as a rule), and a shown line that did not match carries
/// <see cref="LogLineViewModel.IsContextLine"/> (drawn dimmer), so a group reads as "the
/// match, and what surrounds it".
/// </para>
/// </remarks>
public sealed class LogProjection
{
    private readonly Func<LogLineViewModel, bool> _admits;
    private readonly Queue<LogLineViewModel> _before = new();
    private int _afterRemaining;
    private bool _gap;

    // Lines shown by the current AppendRange/Rebuild, added to Shown in one notification at
    // its end. Null outside a batch, when Show adds straight to Shown.
    private List<LogLineViewModel>? _batch;

    // How many shown lines precede the batch: Shown's count for AppendRange, zero for
    // Rebuild, whose batch replaces everything Shown still holds.
    private int _batchBase;

    public LogProjection(Func<LogLineViewModel, bool> admits) => _admits = admits;

    /// <summary>What is rendered.</summary>
    public LogLineCollection Shown { get; } = [];

    private int ShownCount => _batch is { } batch ? _batchBase + batch.Count : Shown.Count;

    /// <summary>The search; null when the box is empty or its pattern does not parse.</summary>
    public LogQuery? Query { get; set; }

    /// <summary>Whether the search hides what does not match, rather than highlighting what does.</summary>
    public bool FilterMode { get; set; }

    /// <summary>Lines of context kept around each match while filtering.</summary>
    public int Context { get; set; }

    private bool Filtering => FilterMode && Query is { HasInclude: true };

    /// <summary>
    /// Admitted lines a <c>!term</c> took out, counted so the box can say "40 hidden" — a
    /// pane that silently lost lines to an exclusion reads as a quiet container.
    /// </summary>
    public int Excluded { get; private set; }

    /// <summary>Re-reads the whole buffer — a filter, a query or a level changed.</summary>
    public void Rebuild(IReadOnlyList<LogLineViewModel> buffer)
    {
        Excluded = 0;
        _before.Clear();
        _afterRemaining = 0;
        _gap = false;
        _batch = [];
        _batchBase = 0;
        try
        {
            foreach (var line in buffer)
            {
                Append(line);
            }
        }
        finally
        {
            var shown = _batch;
            _batch = null;
            Shown.ReplaceAll(shown);
        }
    }

    /// <summary>
    /// Takes a flush's worth of new lines at the end of the buffer, with one notification
    /// for everything it shows rather than one per line.
    /// </summary>
    public void AppendRange(IReadOnlyList<LogLineViewModel> lines)
    {
        _batch = [];
        _batchBase = Shown.Count;
        try
        {
            foreach (var line in lines)
            {
                Append(line);
            }
        }
        finally
        {
            var shown = _batch;
            _batch = null;
            Shown.AddRange(shown);
        }
    }

    /// <summary>Takes one new line at the end of the buffer.</summary>
    public void Append(LogLineViewModel line)
    {
        if (!_admits(line))
        {
            return;
        }

        // Exclusions hide in both modes: "!healthz" is asked for to get rid of the lines.
        if (Query is { HasExcludes: true } query && !query.Admits(line.Message))
        {
            Excluded++;
            return;
        }

        if (!Filtering)
        {
            Show(line, context: false);
            return;
        }

        if (line.Matches(Query))
        {
            while (_before.Count > 0)
            {
                Show(_before.Dequeue(), context: true);
            }

            Show(line, context: false);
            _afterRemaining = Context;
            return;
        }

        if (_afterRemaining > 0)
        {
            _afterRemaining--;
            Show(line, context: true);
            return;
        }

        if (Context == 0)
        {
            _gap = ShownCount > 0;
            return;
        }

        _before.Enqueue(line);
        if (_before.Count > Context)
        {
            _before.Dequeue();
            _gap = ShownCount > 0;
        }
    }

    /// <summary>
    /// Drops the oldest buffered lines. They are always the oldest, so the shown ones among
    /// them are at the front of <see cref="Shown"/> and nothing has to be searched for.
    /// </summary>
    public void TrimFront(IReadOnlyList<LogLineViewModel> dropped)
    {
        var index = 0;
        foreach (var line in dropped)
        {
            if (index < Shown.Count && ReferenceEquals(Shown[index], line))
            {
                index++;
            }
        }

        Shown.RemoveFromFront(index);

        if (Query is { HasExcludes: true } query)
        {
            Excluded = Math.Max(0, Excluded - dropped.Count(l => _admits(l) && !query.Admits(l.Message)));
        }

        if (Shown.Count > 0)
        {
            Shown[0].IsGapBefore = false;
        }

        // A line trimmed from the buffer must not come back as a later match's context.
        if (_before.Count > 0 && dropped.Count > 0)
        {
            var gone = new HashSet<LogLineViewModel>(dropped);
            var kept = _before.Where(l => !gone.Contains(l)).ToList();
            _before.Clear();
            foreach (var line in kept)
            {
                _before.Enqueue(line);
            }
        }
    }

    private void Show(LogLineViewModel line, bool context)
    {
        line.IsContextLine = context && Filtering;
        line.IsGapBefore = _gap && Filtering;
        _gap = false;
        if (_batch is { } batch)
        {
            batch.Add(line);
        }
        else
        {
            Shown.Add(line);
        }
    }
}
