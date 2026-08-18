using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One log pane over every pod a workload owns — the job <c>stern</c> exists for, and
/// the thing that makes a rolling deployment readable: the pod going away and the pod
/// coming up appear in the same stream, in time order, each line keyed to its pod by
/// colour and by a printed name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which pods.</b> The workload's own <c>spec.selector</c>
/// (<see cref="LabelSelector.ForPodsOf"/>) is turned into a <c>labelSelector</c> and run
/// as a list+watch, so the population is the API server's answer and not a guess from
/// the owner chain: during a rollout that is precisely what includes both ReplicaSets.
/// A pod that appears joins the pane; a pod that is deleted stops streaming but
/// <b>keeps its lines</b>, because the last thing a terminating replica said is usually
/// why the pane was opened.
/// </para>
/// <para>
/// <b>Which container.</b> One per pod — the one <c>kubectl logs</c> with no <c>-c</c>
/// picks, i.e. the first app container — and the chip names it. Tailing every container
/// of a pod is a separate, smaller feature (colour-keyed by container rather than by
/// pod); sources here are already keyed by pod <em>and</em> container so that becomes a
/// change to which sources are created and to nothing else.
/// </para>
/// <para>
/// <b>Ordering.</b> Every stream is requested with <c>timestamps=true</c> (they always
/// were), so each line carries the server's RFC3339 instant and the pane can merge on
/// it. It does that in two stages, and the split is the design decision worth knowing:
/// the <em>opening burst</em> — N pods each answering with their tail at once — is held
/// for <see cref="PrimeWindow"/> and then sorted as one block, because otherwise a
/// three-replica pane opens with pod A's hour, then pod B's hour, then pod C's, which is
/// three streams shown consecutively rather than one stream. After that, each flush tick
/// sorts only the lines that arrived within it. A true k-way merge — holding a line back
/// until every other stream has produced something at least as new — is what a *finished*
/// log file allows and a live tail does not: one quiet replica would stall the pane for
/// everybody, which is the opposite of what a tail is for. Out-of-order arrival past the
/// tick is therefore possible and visible, and the timestamp toggle is what settles it.
/// </para>
/// <para>
/// <b>How much history.</b> See <see cref="PerPodTailLines"/> — the per-pod fetch is
/// derived from the pane's own buffer rather than being the single-pod pane's literal.
/// </para>
/// </remarks>
public sealed partial class WorkloadLogsTabViewModel : InspectorTabViewModelBase
{
    /// <summary>
    /// How long the opening burst is held before it is sorted and shown. Long enough for
    /// several pods' tails to land together over a real network, short enough that the
    /// pane does not look stuck — the placeholder says what it is doing meanwhile.
    /// </summary>
    private static readonly TimeSpan PrimeWindow = TimeSpan.FromMilliseconds(900);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Ceiling on concurrent log streams, and the same number <c>stern</c> defaults its
    /// <c>--max-log-requests</c> to. N pods is N long-lived HTTP connections against one
    /// API server; a Deployment scaled to 400 would otherwise open 400 of them because
    /// someone clicked a menu item. Pods past the cap are listed on the chip strip and
    /// not streamed, and the pane says so rather than quietly showing part of the answer.
    /// </summary>
    public const int MaxSources = 50;

    /// <summary>Floor for <see cref="PerPodTailLines"/>: below this a replica contributes nothing readable.</summary>
    public const int MinPerPodTailLines = 25;

    /// <summary>
    /// Ceiling for <see cref="PerPodTailLines"/>, and the single-pod pane's own fetch.
    /// This pane deliberately does not widen it: how much history a log pane asks for is
    /// its own open question (the pane offers no tail/since control at all, on any
    /// surface), and answering it here for the multi-pod case only would leave the two
    /// panes disagreeing about the same thing.
    /// </summary>
    public const int MaxPerPodTailLines = 200;

    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;

    private readonly DynamicResource _workload;
    private readonly LabelSelector _selector;
    private readonly string? _namespace;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<LogLineViewModel> _allLogLines = [];
    private readonly Dictionary<string, LogSourceViewModel> _sourcesByPod = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _streamsByPod = new(StringComparer.Ordinal);
    private readonly List<(string Raw, LogSourceViewModel Source)> _pending = [];
    private readonly Lock _pendingLock = new();

    /// <summary>
    /// Scrollback cap for the <em>pane</em>, not for a pod. Read once at construction,
    /// same as the single-pod pane and for the same reason: re-trimming a live buffer
    /// when the preference changes would discard lines somebody is reading.
    /// </summary>
    private readonly int _maxLogLines = App.LoadSettings().LogBufferLines;

    private DispatcherTimer? _flushTimer;
    private DateTimeOffset? _primeUntil;
    private int _nextColourIndex;

    public override string Key { get; }

    public string WorkloadKind { get; }

    public string WorkloadName { get; }

    public string? WorkloadNamespace => _namespace;

    /// <summary>Cluster this workload came from in an aggregated fleet list; empty otherwise.</summary>
    public string ClusterName { get; }

    /// <summary>The selector, as kubectl would print it — the pane's one-line answer to "which pods is this?".</summary>
    public string SelectorText { get; }

    /// <summary>The pods contributing to the pane, in the order they first appeared. Legend and selector both.</summary>
    public ObservableCollection<LogSourceViewModel> Sources { get; } = [];

    /// <summary>Filtered view over the buffer — this is what is rendered.</summary>
    public ObservableCollection<LogLineViewModel> LogLines { get; } = [];

    [ObservableProperty]
    private string _logSearchText = "";

    [ObservableProperty]
    private bool _showLogTimestamps;

    [ObservableProperty]
    private bool _wrapLogLines;

    /// <summary>
    /// True while the pod watch and its streams are running. Bound from a
    /// <c>ToggleButton</c>'s <c>IsChecked</c> and from nothing else (UI rule 8b): the
    /// work happens in <see cref="OnIsFollowingChanged"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isFollowing;

    /// <summary>True until the pod list has answered once — "finding pods" is not the same state as "no pods".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasLogPlaceholder))]
    private bool _isResolvingPods = true;

    /// <summary>Why the pane looks the way it does, when there is something to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasLogPlaceholder))]
    private string? _logStatus;

    [ObservableProperty]
    private bool _isLogStatusProblem;

    /// <summary>
    /// The pod-cap notice, present only when the cap actually bit. An aggregated pane
    /// that silently shows 50 of 120 replicas is worse than one that shows 50 and says
    /// which number it is.
    /// </summary>
    [ObservableProperty]
    private string? _capNotice;

    /// <summary>Header caption: how many pods, how many lines. The one number the pane owes the reader.</summary>
    [ObservableProperty]
    private string _summary = "";

    private bool _applyingFollowState;

    /// <summary>
    /// Tab identity, cluster-qualified for the same reason pod detail's is: two clusters
    /// in an aggregated list routinely hold a Deployment with the same
    /// namespace/name, and without the qualifier the second one would silently reuse the
    /// first one's pane.
    /// </summary>
    public static string KeyFor(string clusterName, ResourceDescriptor descriptor, string? @namespace, string name)
    {
        var kind = descriptor.Group.Length == 0 ? descriptor.Kind : $"{descriptor.Group}/{descriptor.Kind}";
        return clusterName.Length == 0
            ? $"logs:{kind}:{@namespace}/{name}"
            : $"logs@{clusterName}:{kind}:{@namespace}/{name}";
    }

    public WorkloadLogsTabViewModel(
        ClusterClient? client,
        ResourceDescriptor descriptor,
        DynamicResource workload,
        LabelSelector selector,
        string clusterName = "")
        : base(
            clusterName.Length == 0 ? $"Logs/{workload.Name}" : $"Logs/{workload.Name} · {clusterName}",
            isDemo: client is null)
    {
        _client = client;
        _workload = workload;
        _selector = selector;
        _namespace = workload.Namespace;
        ClusterName = clusterName;
        WorkloadKind = descriptor.Kind;
        WorkloadName = workload.Name;
        SelectorText = selector.ToQuery();
        Key = KeyFor(clusterName, descriptor, _namespace, workload.Name);

        Sources.CollectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();
        Start();
    }

    /// <summary>
    /// How many lines of history to ask each pod for. Not the single-pod pane's literal
    /// 200, and the reason is arithmetic rather than taste: this pane's buffer is shared
    /// by every pod in it, so N replicas × 200 lines is N × 200 lines of backfill
    /// competing for one <c>LogBufferLines</c> cap, and past a handful of replicas the
    /// oldest pods' history is trimmed away before anyone can read it — a pane that
    /// silently drops a whole replica's backfill is worse than one that asks for less of
    /// each. So the pane's own budget is divided by the number of pods it is about to
    /// stream, clamped to <see cref="MinPerPodTailLines"/> so a replica never contributes
    /// nothing, and to <see cref="MaxPerPodTailLines"/> so this never quietly becomes a
    /// wider window than the single-pod pane's — widening it is a real question about
    /// both panes and belongs to the tail/since control neither of them has yet.
    /// </summary>
    public static int PerPodTailLines(int bufferLines, int podCount) =>
        Math.Clamp(bufferLines / Math.Max(1, podCount), MinPerPodTailLines, MaxPerPodTailLines);

    private void Start()
    {
        SetFollowing(true);
        StartFlushTimer();

        if (_client is null)
        {
            LoadDemoSources();
            return;
        }

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _client.WatchResourceAsync(
                    ResourceDescriptor.Pods,
                    _namespace,
                    connectionLost: ex => Dispatcher.UIThread.Post(() => SetStatus(ex.Message, problem: true)),
                    cancellationToken: token,
                    labelSelector: _selector))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyPodEvent(evt));
                }
            }
            catch (OperationCanceledException)
            {
                // normal on close
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsResolvingPods = false;
                    SetStatus(FirstLine(ex.Message), problem: true);
                });
            }
        }, token);
    }

    /// <summary>
    /// The demo cluster's pod "watch": the shipped dataset filtered through the very same
    /// <see cref="LabelSelector.Matches"/> the live path renders into a query. Everything
    /// downstream — the merge, the colour keying, the buffer, the filter, the placeholder
    /// states — is the production code path; only the source of the bytes differs.
    /// </summary>
    private void LoadDemoSources()
    {
        IsResolvingPods = false;
        foreach (var pod in DemoData.Pods)
        {
            if ((_namespace is null || pod.Namespace == _namespace) && _selector.Matches(pod.Labels))
            {
                AddSource(pod);
            }
        }

        RaisePlaceholder();
    }

    private void ApplyPodEvent(ResourceEvent<DynamicResource> evt)
    {
        switch (evt.Type)
        {
            case ResourceEventType.Reset:
                // Deliberately NOT a clear. A Reset is the informer relisting (initial
                // sync, or a 410 Gone), and every pod still there arrives again as Added
                // immediately after — dropping the sources would cancel healthy streams
                // and throw away the buffered lines a reconnect has no way to refetch.
                // A pod that really went away during the gap is caught by its own log
                // stream ending, which the API server does when the pod does.
                IsResolvingPods = false;
                break;

            case ResourceEventType.Added:
            case ResourceEventType.Modified:
                IsResolvingPods = false;
                if (evt.Resource is { } pod)
                {
                    AddSource(pod);
                }

                break;

            case ResourceEventType.Deleted:
                if (evt.Resource is { } deleted && _sourcesByPod.TryGetValue(deleted.Name, out var source))
                {
                    StopStream(deleted.Name);
                    source.State = LogSourceState.Gone;
                    source.StatusMessage = "Pod deleted; its lines are kept.";
                }

                break;
        }

        UpdateSummary();
        RaisePlaceholder();
    }

    /// <summary>
    /// Registers a pod and opens its stream, once. Called for Modified as well as Added
    /// because a pod that was Pending when the pane opened only becomes readable later —
    /// the first stream attempt on a container that has not started fails with the API
    /// server's own "waiting to start: ContainerCreating", and this is what picks it up
    /// when it does.
    /// </summary>
    private void AddSource(DynamicResource pod)
    {
        if (_sourcesByPod.TryGetValue(pod.Name, out var existing))
        {
            if (existing.State is LogSourceState.Failed && !_streamsByPod.ContainsKey(pod.Name))
            {
                StartStream(existing);
            }

            return;
        }

        if (_sourcesByPod.Count >= MaxSources)
        {
            CapNotice =
                $"Streaming the first {MaxSources} pods. More match {SelectorText} — narrow the selection or use a "
                + "single pod's log pane for the rest.";
            return;
        }

        StartStream(RegisterSource(pod.Name, FirstContainerOf(pod)));
    }

    /// <summary>
    /// Creates a source, assigns it the next colour and puts it on the strip — without
    /// opening its stream. Split out from <see cref="AddSource"/> because the two halves
    /// are genuinely separate concerns (a source that exists versus a stream that is
    /// running: a failed pod keeps the first and loses the second), and because it is
    /// the seam the view-model tests register sources through, which lets them exercise
    /// the real buffer, merge and filter with no socket and no dispatcher loop behind it.
    /// </summary>
    internal LogSourceViewModel RegisterSource(string podName, string containerName)
    {
        var source = new LogSourceViewModel(
            podName,
            containerName,
            LogSourcePalette.ShortNameFor(podName, WorkloadName),
            LogSourcePalette.BrushFor(_nextColourIndex++));

        source.PropertyChanged += OnSourceChanged;
        _sourcesByPod[podName] = source;
        Sources.Add(source);
        return source;
    }

    /// <summary>
    /// The container <c>kubectl logs</c> would pick with no <c>-c</c>: the first app
    /// container. Init and ephemeral containers are deliberately not streamed here —
    /// an init container has already exited by the time a workload has running pods, and
    /// a debug attachment is not part of the workload's own output.
    /// </summary>
    private static string FirstContainerOf(DynamicResource pod)
    {
        if (pod.Raw.ValueKind == JsonValueKind.Object
            && pod.Raw.TryGetProperty("spec", out var spec)
            && spec.ValueKind == JsonValueKind.Object
            && spec.TryGetProperty("containers", out var containers)
            && containers.ValueKind == JsonValueKind.Array)
        {
            foreach (var container in containers.EnumerateArray())
            {
                if (container.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        return "";
    }

    private void StartStream(LogSourceViewModel source)
    {
        StopStream(source.PodName);

        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _streamsByPod[source.PodName] = streamCts;
        var token = streamCts.Token;
        source.State = LogSourceState.Starting;
        source.StatusMessage = null;

        var tail = PerPodTailLines(_maxLogLines, Math.Max(1, _sourcesByPod.Count));

        if (_client is null)
        {
            _ = ReplayDemoAsync(source, token);
            return;
        }

        var podNamespace = _namespace ?? "";
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _client.StreamPodLogsAsync(
                    podNamespace, source.PodName, source.ContainerName.Length == 0 ? null : source.ContainerName,
                    follow: true, tailLines: tail, timestamps: true, cancellationToken: token))
                {
                    Enqueue(line, source);
                }

                await EndSourceAsync(source, LogSourceState.Ended, $"{source.ContainerName} exited.", token);
            }
            catch (OperationCanceledException)
            {
                // normal on close, on Follow off, or when the pod goes away
            }
            catch (Exception ex)
            {
                // The API server's own sentence — "container \"app\" is waiting to start:
                // ContainerCreating" is the whole diagnosis, and a Modified event will
                // bring this pod back round to StartStream once it is running.
                await EndSourceAsync(source, LogSourceState.Failed, FirstLine(ex.Message), token);
            }
        }, token);
    }

    /// <summary>The demo cluster's stand-in for a follow, through the same <see cref="Enqueue"/>.</summary>
    private async Task ReplayDemoAsync(LogSourceViewModel source, CancellationToken token)
    {
        var lines = DemoLogs.For(source.PodName, source.ContainerName);
        try
        {
            foreach (var line in lines)
            {
                await Task.Delay(DemoLogs.Interval, token);
                Enqueue(line, source);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await EndSourceAsync(
            source, LogSourceState.Ended, "Demo cluster: the sample stream has finished.", token);
    }

    private async Task EndSourceAsync(LogSourceViewModel source, LogSourceState state, string message, CancellationToken token) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            // A pod deleted while its stream was closing keeps the more specific state.
            if (source.State is not LogSourceState.Gone)
            {
                source.State = state;
                source.StatusMessage = message;
            }

            _streamsByPod.Remove(source.PodName);
            FlushNow();
            RaisePlaceholder();
        });

    private void StopStream(string podName)
    {
        if (_streamsByPod.Remove(podName, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void StopAllStreams()
    {
        foreach (var podName in _streamsByPod.Keys.ToList())
        {
            StopStream(podName);
        }
    }

    // ----------------------------------------------------------------- buffering

    internal void Enqueue(string rawLine, LogSourceViewModel source)
    {
        lock (_pendingLock)
        {
            _primeUntil ??= DateTimeOffset.UtcNow + PrimeWindow;
            _pending.Add((rawLine, source));
        }
    }

    private void StartFlushTimer()
    {
        _flushTimer ??= CreateFlushTimer();
        _flushTimer.Start();
    }

    private DispatcherTimer CreateFlushTimer()
    {
        var timer = new DispatcherTimer { Interval = FlushInterval };
        timer.Tick += (_, _) => Flush(force: false);
        return timer;
    }

    /// <summary>Drains whatever is pending regardless of the prime window — used when a stream ends or the pane closes.</summary>
    private void FlushNow() => Flush(force: true);

    internal void Flush(bool force)
    {
        (string Raw, LogSourceViewModel Source)[] pending;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            // The opening burst is held so that N pods' tails are sorted together rather
            // than shown one pod after another. Every later tick flushes immediately.
            if (!force && _primeUntil is { } until && DateTimeOffset.UtcNow < until)
            {
                return;
            }

            pending = [.. _pending];
            _pending.Clear();
            _primeUntil = null;
        }

        var built = new List<LogLineViewModel>(pending.Length);
        foreach (var (raw, source) in pending)
        {
            built.Add(new LogLineViewModel(raw, ShowLogTimestamps, source));
            source.LineCount++;
        }

        foreach (var line in OrderBatch(built))
        {
            _allLogLines.Add(line);
            if (MatchesFilter(line))
            {
                LogLines.Add(line);
            }

            if (line.Source is { State: LogSourceState.Starting } starting)
            {
                starting.State = LogSourceState.Streaming;
            }
        }

        TrimBuffer();
        UpdateSummary();
        RaisePlaceholder();
    }

    /// <summary>
    /// Orders one flush's worth of lines by the server timestamp each one carries, and
    /// leaves everything else alone.
    /// </summary>
    /// <remarks>
    /// Two properties are load-bearing and both are pinned by
    /// <c>WorkloadLogMergeTests</c>. The sort is <b>stable</b>, so two pods that logged
    /// in the same millisecond stay in the order they arrived rather than shuffling on
    /// every tick. And a line whose leading token is not a timestamp — a continuation
    /// line, or a server that answered without them — inherits the instant of the last
    /// line that had one, so a stack trace stays attached to the line it belongs to
    /// instead of being flung to the top of the batch.
    /// </remarks>
    internal static IReadOnlyList<LogLineViewModel> OrderBatch(IReadOnlyList<LogLineViewModel> arrived)
    {
        if (arrived.Count < 2)
        {
            return arrived;
        }

        var keys = new DateTimeOffset[arrived.Count];
        var carried = DateTimeOffset.MinValue;
        for (var i = 0; i < arrived.Count; i++)
        {
            carried = arrived[i].At ?? carried;
            keys[i] = carried;
        }

        var indexes = new int[arrived.Count];
        for (var i = 0; i < indexes.Length; i++)
        {
            indexes[i] = i;
        }

        // OrderBy is a stable sort; Array.Sort is not.
        var ordered = new List<LogLineViewModel>(arrived.Count);
        foreach (var index in indexes.OrderBy(i => keys[i]))
        {
            ordered.Add(arrived[index]);
        }

        return ordered;
    }

    private void TrimBuffer()
    {
        var excess = _allLogLines.Count - _maxLogLines;
        if (excess <= 0)
        {
            return;
        }

        var dropped = _allLogLines.GetRange(0, excess);
        _allLogLines.RemoveRange(0, excess);

        var visible = 0;
        foreach (var line in dropped)
        {
            if (MatchesFilter(line))
            {
                visible++;
            }
        }

        for (var i = 0; i < visible && LogLines.Count > 0; i++)
        {
            LogLines.RemoveAt(0);
        }
    }

    /// <summary>
    /// A line is shown when its pod is included <em>and</em> the text filter matches.
    /// Both halves are re-evaluated over the whole buffer rather than applied as lines
    /// arrive, so re-including a pod brings its earlier lines back in place instead of
    /// only its future ones.
    /// </summary>
    private bool MatchesFilter(LogLineViewModel line) =>
        (line.Source?.IsIncluded ?? true)
        && (LogSearchText.Length == 0 || line.Message.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase));

    private void ApplyFilter()
    {
        LogLines.Clear();
        foreach (var line in _allLogLines)
        {
            if (MatchesFilter(line))
            {
                LogLines.Add(line);
            }
        }

        RaisePlaceholder();
    }

    partial void OnLogSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowLogTimestampsChanged(bool value)
    {
        foreach (var line in _allLogLines)
        {
            line.ShowTimestamp = value;
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogSourceViewModel.IsIncluded))
        {
            ApplyFilter();
            UpdateSummary();
        }
    }

    partial void OnIsFollowingChanged(bool value)
    {
        if (_applyingFollowState)
        {
            return;
        }

        if (value)
        {
            // The buffer is cleared before re-following, the same as the single-pod
            // pane: each stream is re-opened with its tail, so keeping what is there
            // would show every buffered line a second time.
            ClearBuffer();
            foreach (var source in Sources)
            {
                if (source.State is not LogSourceState.Gone)
                {
                    StartStream(source);
                }
            }

            SetStatus(null, problem: false);
        }
        else
        {
            StopAllStreams();
            FlushNow();
            foreach (var source in Sources.Where(s => s.State is LogSourceState.Streaming or LogSourceState.Starting))
            {
                source.State = LogSourceState.Ended;
                source.StatusMessage = "Stopped.";
            }

            SetStatus("Log streaming is stopped. Press Follow to start it.", problem: false);
        }
    }

    private void ClearBuffer()
    {
        lock (_pendingLock)
        {
            _pending.Clear();
            _primeUntil = null;
        }

        _allLogLines.Clear();
        LogLines.Clear();
        foreach (var source in Sources)
        {
            source.LineCount = 0;
        }

        UpdateSummary();
        RaisePlaceholder();
    }

    private void SetFollowing(bool value)
    {
        _applyingFollowState = true;
        IsFollowing = value;
        _applyingFollowState = false;
    }

    private void SetStatus(string? status, bool problem)
    {
        LogStatus = status;
        IsLogStatusProblem = problem;
    }

    private void UpdateSummary()
    {
        var included = Sources.Count(s => s.IsIncluded);
        var pods = included == Sources.Count
            ? $"{Sources.Count} pod{(Sources.Count == 1 ? "" : "s")}"
            : $"{included} of {Sources.Count} pods";
        Summary = $"{pods} · {_allLogLines.Count:N0} line{(_allLogLines.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Every state this pane can be in, named (UI rule 9). "Still finding pods", "the
    /// selector matches nothing", "matched pods but none has logged", "every pod is
    /// hidden" and "the filter matched none of what is buffered" all render as the same
    /// empty rectangle otherwise, and each sends the reader somewhere different.
    /// </summary>
    public string? LogPlaceholder
    {
        get
        {
            if (LogLines.Count > 0)
            {
                return null;
            }

            if (IsResolvingPods)
            {
                return $"Finding pods matching {SelectorText}…";
            }

            if (Sources.Count == 0)
            {
                return $"No pods match {SelectorText}"
                    + (_namespace is { Length: > 0 } ns ? $" in namespace {ns}." : ".");
            }

            if (Sources.All(s => !s.IsIncluded))
            {
                return "Every pod is hidden. Click a pod above to show it again.";
            }

            if (_allLogLines.Count > 0)
            {
                var buffered = $"{_allLogLines.Count:N0} line{(_allLogLines.Count == 1 ? "" : "s")} buffered "
                    + $"from {Sources.Count} pod{(Sources.Count == 1 ? "" : "s")}";
                return LogSearchText.Length == 0
                    ? $"The pods still shown have logged nothing — {buffered}."
                    : $"No lines match “{LogSearchText}” — {buffered}.";
            }

            if (LogStatus is { Length: > 0 } status)
            {
                return status;
            }

            return IsFollowing
                ? $"Following {Sources.Count} pod{(Sources.Count == 1 ? "" : "s")} — waiting for output."
                : "Log streaming is stopped. Press Follow to start it.";
        }
    }

    public bool HasLogPlaceholder => LogPlaceholder is not null;

    private void RaisePlaceholder()
    {
        OnPropertyChanged(nameof(LogPlaceholder));
        OnPropertyChanged(nameof(HasLogPlaceholder));
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        if (GetMainWindow()?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(VisibleLogText());
    }

    /// <summary>
    /// What Copy and Download write: the raw server line with its timestamp, prefixed by
    /// the pod it came from. The prefix is added here and not shown by the display toggle
    /// because a merged log pasted into an incident ticket without it is unreadable —
    /// the colour that distinguished the pods on screen does not survive a paste.
    /// </summary>
    private string VisibleLogText() =>
        string.Join(Environment.NewLine, LogLines.Select(l => l.Source is { } s ? $"{s.ShortName} {l.RawLine}" : l.RawLine));

    [RelayCommand]
    private async Task DownloadLogsAsync()
    {
        if (GetMainWindow()?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save aggregated logs",
            SuggestedFileName = $"{WorkloadName}-all-pods.log",
            FileTypeChoices = [new FilePickerFileType("Log file") { Patterns = ["*.log"] }],
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(VisibleLogText());
    }

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null;

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    /// <summary>The workload object the pane was opened on — kept so a future refresh can re-read its selector.</summary>
    internal DynamicResource Workload => _workload;

    public override async Task OnClosingAsync()
    {
        _flushTimer?.Stop();
        StopAllStreams();
        foreach (var source in Sources)
        {
            source.PropertyChanged -= OnSourceChanged;
        }

        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
