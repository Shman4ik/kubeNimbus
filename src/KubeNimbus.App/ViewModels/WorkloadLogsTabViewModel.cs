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
/// How an application page hosts the aggregated pane, as opposed to the inspector dock.
/// </summary>
/// <param name="ExtraSelectors">More selectors in the same namespace — an Argo app of several workloads is one stream.</param>
/// <param name="FocusPod">The pod shown when the pane opens; null for all pods merged.</param>
/// <param name="Previous">Read each pod's run before the current one (never followed).</param>
/// <param name="Embedded">Hosted in the application page: no pod strip, and an Errors-only toggle.</param>
public sealed record WorkloadLogsOptions(
    IReadOnlyList<LabelSelector> ExtraSelectors,
    string? FocusPod = null,
    bool Previous = false,
    bool Embedded = false)
{
    public static readonly WorkloadLogsOptions Default = new([]);
}

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
/// picks, i.e. the pod's <c>kubectl.kubernetes.io/default-container</c> or else its first
/// app container — and the chip names it. Tailing every container
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
/// <b>How much history.</b> Line ranges use <see cref="PerPodTailLines"/>'s
/// shared-buffer budget; time ranges and Everything rely on the visible trim notice.
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
    /// Default line range's ceiling for <see cref="PerPodTailLines"/>.
    /// </summary>
    public const int MaxPerPodTailLines = 200;

    /// <summary>Null on the demo cluster — see <see cref="InspectorTabViewModelBase.IsDemo"/>.</summary>
    private readonly ClusterClient? _client;

    private readonly DynamicResource _workload;
    private readonly IReadOnlyList<LabelSelector> _selectors;
    private readonly WorkloadLogsOptions _options;
    private readonly string? _namespace;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<LogLineViewModel> _allLogLines = [];
    private readonly Dictionary<string, LogSourceViewModel> _sourcesByPod = new(StringComparer.Ordinal);

    // The latest object the pod watch delivered per pod, so a stream knows which run of its
    // container it started on — see LogStreamEnd.
    private readonly Dictionary<string, DynamicResource> _latestPods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _streamsByPod = new(StringComparer.Ordinal);
    private readonly HashSet<string> _respondedPods = new(StringComparer.Ordinal);
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

    /// <summary>
    /// The log search: finds (highlights, next/previous) by default, filters while
    /// <see cref="IsLogFilterMode"/> is on — the same two modes as pod detail's pane.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogSearch), nameof(IsFinding))]
    private string _logSearchText = "";

    /// <summary>Hide non-matching lines rather than highlight matches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinding))]
    private bool _isLogFilterMode;

    public bool HasLogSearch => LogSearchText.Length > 0;

    /// <summary>A search is running in find mode — the box shows its previous/next arrows.</summary>
    public bool IsFinding => HasLogSearch && !IsLogFilterMode;

    /// <summary>The search box's counter: "3 of 17" while finding, "12 lines" while filtering.</summary>
    [ObservableProperty]
    private string _logSearchSummary = "";

    /// <summary>The line next/previous is on. The view scrolls it into sight when it changes.</summary>
    [ObservableProperty]
    private LogLineViewModel? _currentLogMatch;

    /// <summary>Which severities are shown; unclassified lines always are.</summary>
    public LogLevelFilter Levels { get; } = new();

    // Display preferences, shared with pod detail's pane through settings.json (FEAT-37).
    [ObservableProperty]
    private bool _showLogTimestamps;

    [ObservableProperty]
    private bool _wrapLogLines;

    [ObservableProperty]
    private bool _useUtcTimestamps;

    private bool _restoringDisplayPreferences;

    /// <summary>Lines the reader cleared while nothing has arrived since; see pod detail's twin.</summary>
    private int? _clearedLines;

    private readonly LogFind _find = new();

    public IReadOnlyList<LogRange> LogRanges => LogRange.Choices;

    [ObservableProperty]
    private LogRange _selectedLogRange = LogRange.Last200;

    [ObservableProperty]
    private string? _trimNotice;

    private bool _loadingLogRange;
    private int _streamGeneration;

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

    /// <summary>
    /// Whether each line is prefixed with its pod. Only while more than one pod is shown:
    /// with one, every line carries the same name — the application page's "All pods,
    /// merged, 1 pod" printed it down the whole left edge, 160px of every line spent on
    /// nothing. Copy and Save keep the prefix either way.
    /// </summary>
    [ObservableProperty]
    private bool _showSourceColumn;

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
        string clusterName = "",
        WorkloadLogsOptions? options = null)
        : base(
            clusterName.Length == 0 ? $"Logs/{workload.Name}" : $"Logs/{workload.Name} · {clusterName}",
            isDemo: client is null)
    {
        _client = client;
        _workload = workload;
        _options = options ?? WorkloadLogsOptions.Default;
        _selectors = [selector, .. _options.ExtraSelectors];
        _focusPodName = _options.FocusPod;
        _namespace = workload.Namespace;
        ClusterName = clusterName;
        WorkloadKind = descriptor.Kind;
        WorkloadName = workload.Name;
        SelectorText = string.Join(" | ", _selectors.Select(s => s.ToQuery()));
        Key = KeyFor(clusterName, descriptor, _namespace, workload.Name);

        Sources.CollectionChanged += (_, _) => UpdateSummary();
        Levels.Changed += (_, _) => ApplyFilter();
        RestoreDisplayPreferences();
        UpdateSummary();
        Start();
    }

    /// <summary>
    /// How many lines of history to ask each pod for in the default range. The reason
    /// is arithmetic rather than taste: this pane's buffer is shared
    /// by every pod in it, so N replicas × 200 lines is N × 200 lines of backfill
    /// competing for one <c>LogBufferLines</c> cap, and past a handful of replicas the
    /// oldest pods' history is trimmed away before anyone can read it — a pane that
    /// silently drops a whole replica's backfill is worse than one that asks for less of
    /// each. So the pane's own budget is divided by the number of pods it is about to
    /// stream, clamped to <see cref="MinPerPodTailLines"/> so a replica never contributes
    /// nothing, and to <see cref="MaxPerPodTailLines"/> for the default range.
    /// </summary>
    public static int PerPodTailLines(int bufferLines, int podCount) =>
        LogRange.Last200.TailForPod(bufferLines, podCount)!.Value;

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
        foreach (var selector in _selectors)
        {
            WatchPods(_client, selector, token);
        }
    }

    private void WatchPods(ClusterClient client, LabelSelector selector, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(
                    ResourceDescriptor.Pods,
                    _namespace,
                    connectionLost: ex => Dispatcher.UIThread.Post(() => SetStatus(ex.Message, problem: true)),
                    cancellationToken: token,
                    labelSelector: selector))
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
            if ((_namespace is null || pod.Namespace == _namespace) && _selectors.Any(s => s.Matches(pod.Labels)))
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
                //
                // Nor does it end the "resolving" state: a Reset is the *start* of a
                // list, so clearing here renders "no pods match this selector" for as
                // long as the list request takes (UI rule 18). Synced below is the end.
                IsResolvingPods = true;
                break;

            case ResourceEventType.Synced:
                IsResolvingPods = false;
                break;

            case ResourceEventType.Added:
            case ResourceEventType.Modified:
                IsResolvingPods = false;
                if (evt.Resource is { } pod)
                {
                    _latestPods[pod.Name] = pod;
                    AddSource(pod);
                }

                break;

            case ResourceEventType.Deleted:
                if (evt.Resource is { } gone)
                {
                    _latestPods.Remove(gone.Name);
                }

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
            // A pod whose container had not started when the pane opened is picked up once
            // it has: an unscheduled pod's follow request is an immediate 204 No Content, so
            // its stream ends cleanly rather than failing (a scheduled pod still creating its
            // container is a 400 instead, the Failed path), and without this it stayed "not
            // started" after the pod was running. Re-opened only once the pod no longer reads as never
            // started, so a pending pod's status updates do not each cost a request.
            var retry = existing.State switch
            {
                LogSourceState.Failed => true,
                LogSourceState.NotStarted => !LogStreamEnd.DescribePod(pod.Raw, existing.ContainerName, null).NotStarted,
                _ => false,
            };
            if (retry && !_streamsByPod.ContainsKey(pod.Name))
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

        // An application page that opened on one pod shows that pod; the others still
        // stream into the buffer, so choosing "All pods" later loses nothing.
        source.IsIncluded = _focusPodName is null || string.Equals(podName, _focusPodName, StringComparison.Ordinal);
        source.PropertyChanged += OnSourceChanged;
        _sourcesByPod[podName] = source;
        Sources.Add(source);
        return source;
    }

    /// <summary>
    /// The container <c>kubectl logs</c> would pick with no <c>-c</c>: the one named by
    /// <c>kubectl.kubernetes.io/default-container</c>, else the first app container
    /// (<see cref="PodDetails.DefaultContainer"/>, FEAT-38). Init and ephemeral containers
    /// are otherwise not streamed here — an init container has already exited by the time a
    /// workload has running pods, and a debug attachment is not part of the workload's own
    /// output — unless the pod itself names one as its default.
    /// </summary>
    private static string FirstContainerOf(DynamicResource pod) => PodDetails.DefaultContainer(pod.Raw) ?? "";

    private void StartStream(LogSourceViewModel source, bool follow = true)
    {
        // The run before cannot be followed: the API server refuses follow with previous.
        follow &= !_options.Previous;
        StopStream(source.PodName);
        _respondedPods.Remove(source.PodName);
        _streamGeneration++;

        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _streamsByPod[source.PodName] = streamCts;
        var token = streamCts.Token;
        source.State = LogSourceState.Starting;
        source.StatusMessage = null;

        if (_client is not null) _loadingLogRange = true;

        var tail = SelectedLogRange.TailForPod(_maxLogLines, Math.Max(1, _sourcesByPod.Count));

        if (_client is null)
        {
            _ = ReplayDemoAsync(source, token);
            return;
        }

        var podNamespace = _namespace ?? "";
        var client = _client;
        var atStart = _latestPods.TryGetValue(source.PodName, out var latest)
            ? PodDetails.ContainerRunOf(latest.Raw, source.ContainerName)
            : null;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in client.StreamPodLogsAsync(
                    podNamespace, source.PodName, source.ContainerName.Length == 0 ? null : source.ContainerName,
                    follow: follow, tailLines: tail, sinceSeconds: SelectedLogRange.SinceSeconds,
                    previous: _options.Previous,
                    timestamps: true,
                    responseReady: () => OnLogResponseReady(source, token, follow),
                    cancellationToken: token))
                {
                    Enqueue(line, source);
                }

                if (!follow)
                {
                    await EndSourceAsync(source, LogSourceState.Loaded,
                        "Selected range loaded — snapshot, not a live stream.", token);
                    return;
                }

                // Not "exited" on faith: a dropped connection ends a follow the same way.
                await EndSourceAsync(source, LogSourceState.Ended, LogStreamEnd.Checking(source.ContainerName), token);
                var (text, _, notStarted, now) = await LogStreamEnd.ExplainWithRunAsync(
                    client, podNamespace, source.PodName, source.ContainerName, atStart, token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || source.State is not LogSourceState.Ended)
                    {
                        return;
                    }

                    // Asked before the container started, and it has started since: the
                    // stream that ended was never this run's, so follow it now. A new pod in
                    // a rollout lands here routinely (see LogStreamEnd.StartedAfterRequest);
                    // it cannot loop, because the restarted follow starts from a running
                    // container and so never meets this condition again.
                    if (LogStreamEnd.StartedAfterRequest(atStart, now))
                    {
                        StartStream(source);
                        return;
                    }

                    source.StatusMessage = text;
                    if (notStarted)
                    {
                        source.State = LogSourceState.NotStarted;
                    }

                    RaisePlaceholder();
                });
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

    private void OnLogResponseReady(LogSourceViewModel source, CancellationToken token, bool follow) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested
                || !_streamsByPod.TryGetValue(source.PodName, out var current)
                || current.Token != token)
            {
                return;
            }

            _respondedPods.Add(source.PodName);
            if (source.State is LogSourceState.Starting) source.State = LogSourceState.Streaming;
            if (_streamsByPod.Keys.Any(pod => !_respondedPods.Contains(pod)))
            {
                _loadingLogRange = true;
            }
            else if (!follow)
            {
                // Headers alone cannot prove a finite snapshot is empty. Wait for EOF.
                _loadingLogRange = true;
            }
            else if (_allLogLines.Count == 0)
            {
                _ = ShowQuietRangeAfterResponsesAsync(_streamGeneration);
            }
            else
            {
                _loadingLogRange = false;
            }
            RaisePlaceholder();
        });

    private async Task ShowQuietRangeAfterResponsesAsync(int generation)
    {
        try
        {
            await Task.Delay(200, _cts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _streamGeneration || _allLogLines.Count > 0
                    || _streamsByPod.Count == 0
                    || _streamsByPod.Keys.Any(pod => !_respondedPods.Contains(pod))) return;
                _loadingLogRange = false;
                RaisePlaceholder();
            });
        }
        catch (OperationCanceledException) { }
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

        // No lines at all is a container that never ran, and it is said from the pod the
        // way a live stream's ending is — "the sample stream has finished" over zero lines
        // was a chip saying "ended" beside a body that disagreed (ENG-45).
        if (lines.Count == 0 && DemoData.Pods.FirstOrDefault(p => p.Name == source.PodName) is { } pod)
        {
            var (text, _, notStarted) = LogStreamEnd.DescribePod(pod.Raw, source.ContainerName, atStart: null);
            await EndSourceAsync(source, notStarted ? LogSourceState.NotStarted : LogSourceState.Ended, text, token);
            return;
        }

        await EndSourceAsync(source, LogSourceState.Ended, "Demo cluster: the sample stream has finished.", token);
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
            var pending = _streamsByPod.Keys.Any(pod => !_respondedPods.Contains(pod));
            if (_streamsByPod.Count == 0 || (!pending && Sources.Any(s => s.State is LogSourceState.Failed)))
            {
                _loadingLogRange = false;
            }
            else if (pending)
            {
                _loadingLogRange = true;
            }
            RaisePlaceholder();
        });

    private void StopStream(string podName)
    {
        if (_streamsByPod.Remove(podName, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _respondedPods.Remove(podName);
            var pending = _streamsByPod.Keys.Any(pod => !_respondedPods.Contains(pod));
            if (_streamsByPod.Count == 0) _loadingLogRange = false;
            else if (pending) _loadingLogRange = true;
            else if (_allLogLines.Count == 0 && IsFollowing)
                _ = ShowQuietRangeAfterResponsesAsync(_streamGeneration);
            RaisePlaceholder();
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
            built.Add(new LogLineViewModel(raw, ShowLogTimestamps, source, UseUtcTimestamps));
            source.LineCount++;
        }

        _clearedLines = null;

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
        _loadingLogRange = _streamsByPod.Keys.Any(pod => !_respondedPods.Contains(pod));
        UpdateFind(newQuery: false);
        ClearLogsCommand.NotifyCanExecuteChanged();
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

        TrimNotice = $"Older lines were trimmed at the {_maxLogLines:N0}-line scrollback limit.";

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
    /// A line is shown when its pod is included, its level is shown (unclassified lines
    /// always are) <em>and</em>, in filter mode only, the search text matches — in find mode
    /// every line stays and the matches are highlighted instead. All of it is re-evaluated
    /// over the whole buffer rather than applied as lines arrive, so re-including a pod
    /// brings its earlier lines back in place instead of only its future ones.
    /// </summary>
    private bool MatchesFilter(LogLineViewModel line) =>
        (line.Source?.IsIncluded ?? true)
        && (!ShowErrorsOnly || line.IsErrorLine)
        && Levels.Admits(line)
        && (!IsLogFilterMode || LogSearchText.Length == 0 || line.Contains(LogSearchText));

    /// <summary>
    /// The application page's "Errors only": the same class-based severity the lines are
    /// coloured by (<c>log-severity-classes.md</c>), so what it keeps is exactly what is
    /// drawn red. A narrowing of the projection, never of the buffer — turning it off
    /// brings every line back in place.
    /// </summary>
    [ObservableProperty]
    private bool _showErrorsOnly;

    partial void OnShowErrorsOnlyChanged(bool value) => ApplyFilter();

    /// <summary>
    /// True when the pane is hosted inside the application page rather than the inspector
    /// dock: the page's own pod list is the selector there, so the pod strip is hidden, and
    /// the Errors-only toggle is offered. The dock's pane is unchanged.
    /// </summary>
    public bool IsEmbedded => _options.Embedded;

    /// <summary>
    /// A line the embedding page ends the log with — "Container worker exited with code 1
    /// (Error) at 08:54:36". Drawn after the last log line, inside the same scroll, because
    /// it is the end of that run's story rather than a banner about the pane.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    private string? _footer;

    public bool HasFooter => Footer is not null;

    /// <summary>Whether this pane reads each pod's run before the current one (<c>previous=true</c>).</summary>
    public bool IsPreviousRun => _options.Previous;

    private string? _focusPodName;

    /// <summary>
    /// Which pod the pane shows, or null for all of them merged. Setting it re-includes the
    /// buffer's lines rather than re-reading anything: every pod keeps streaming either way.
    /// </summary>
    public string? FocusPod
    {
        get => _focusPodName;
        set
        {
            if (string.Equals(_focusPodName, value, StringComparison.Ordinal))
            {
                return;
            }

            _focusPodName = value;
            foreach (var source in Sources)
            {
                source.IsIncluded = value is null || string.Equals(source.PodName, value, StringComparison.Ordinal);
            }

            OnPropertyChanged();
        }
    }

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

        UpdateFind(newQuery: false);
        RaisePlaceholder();
    }

    partial void OnLogSearchTextChanged(string value)
    {
        if (IsLogFilterMode)
        {
            ApplyFilter();
        }

        UpdateFind(newQuery: true);
        RaisePlaceholder();
    }

    partial void OnIsLogFilterModeChanged(bool value)
    {
        ApplyFilter();
        UpdateFind(newQuery: true);
    }

    /// <summary>Re-reads the search's matches after the shown lines changed — pod detail's <c>UpdateLogFind</c>, line for line.</summary>
    private void UpdateFind(bool newQuery)
    {
        if (IsLogFilterMode || LogSearchText.Length == 0)
        {
            _find.Clear();
            LogSearchSummary = LogSearchText.Length == 0
                ? ""
                : $"{LogLines.Count:N0} line{(LogLines.Count == 1 ? "" : "s")}";
        }
        else
        {
            _find.Update(LogLines, LogSearchText, newQuery);
            LogSearchSummary = _find.Summary(LogSearchText);
        }

        CurrentLogMatch = _find.Current;
        FindNextLogMatchCommand.NotifyCanExecuteChanged();
        FindPreviousLogMatchCommand.NotifyCanExecuteChanged();
    }

    private bool HasLogMatches => _find.Count > 0;

    /// <summary>The next (later) match — Enter in the search box.</summary>
    [RelayCommand(CanExecute = nameof(HasLogMatches))]
    private void FindNextLogMatch()
    {
        _find.Next();
        CurrentLogMatch = _find.Current;
        LogSearchSummary = _find.Summary(LogSearchText);
    }

    /// <summary>The previous (earlier) match — Shift+Enter in the search box.</summary>
    [RelayCommand(CanExecute = nameof(HasLogMatches))]
    private void FindPreviousLogMatch()
    {
        _find.Previous();
        CurrentLogMatch = _find.Current;
        LogSearchSummary = _find.Summary(LogSearchText);
    }

    /// <summary>
    /// Empties the pane and keeps every stream running (FEAT-40) — see pod detail's
    /// <c>ClearLogs</c>. Each pod's line count starts again from nothing, since the chips'
    /// counts are of lines in the pane.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasBufferedLines))]
    private void ClearLogs()
    {
        int pending;
        lock (_pendingLock)
        {
            pending = _pending.Count;
        }

        var cleared = _allLogLines.Count + pending;
        ClearBuffer();
        _clearedLines = cleared;
        RaisePlaceholder();
    }

    private bool HasBufferedLines => _allLogLines.Count > 0;

    private void RestoreDisplayPreferences()
    {
        var settings = App.LoadSettings();
        _restoringDisplayPreferences = true;
        ShowLogTimestamps = settings.LogShowTimestamps;
        WrapLogLines = settings.LogWrapLines;
        UseUtcTimestamps = settings.LogTimestampsUtc;
        _restoringDisplayPreferences = false;
    }

    partial void OnWrapLogLinesChanged(bool value)
    {
        if (!_restoringDisplayPreferences)
        {
            App.Update(s => s with { LogWrapLines = value });
        }
    }

    partial void OnUseUtcTimestampsChanged(bool value)
    {
        foreach (var line in _allLogLines)
        {
            line.UtcTimestamp = value;
        }

        if (!_restoringDisplayPreferences)
        {
            App.Update(s => s with { LogTimestampsUtc = value });
        }
    }

    /// <summary>The UTC chip's tooltip, which names the local offset.</summary>
    public string TimestampZoneTooltip => LogLineViewModel.ZoneTooltip;

    partial void OnSelectedLogRangeChanged(LogRange value)
    {
        if (IsDemo)
        {
            return;
        }

        StopAllStreams();
        ClearBuffer();
        _loadingLogRange = true;
        SetStatus(null, problem: false);
        foreach (var source in Sources.Where(s => s.State is not LogSourceState.Gone))
        {
            StartStream(source, IsFollowing);
        }

        RaisePlaceholder();
    }

    partial void OnShowLogTimestampsChanged(bool value)
    {
        foreach (var line in _allLogLines)
        {
            line.ShowTimestamp = value;
        }

        if (!_restoringDisplayPreferences)
        {
            App.Update(s => s with { LogShowTimestamps = value });
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
        TrimNotice = null;
        _clearedLines = null;
        foreach (var source in Sources)
        {
            source.LineCount = 0;
        }

        UpdateFind(newQuery: false);
        ClearLogsCommand.NotifyCanExecuteChanged();
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
        ShowSourceColumn = included > 1;
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
                return IsLogFilterMode && LogSearchText.Length > 0
                    ? $"No lines match “{LogSearchText}”{(ShowErrorsOnly ? " among the error lines" : "")} — {buffered}."
                    : ShowErrorsOnly
                        ? $"No error lines — {buffered}."
                        : Levels.IsFiltering
                            ? $"Every line from the pods shown is at a hidden level — {buffered}. Levels shows them again."
                            : $"The pods still shown have logged nothing — {buffered}.";
            }

            if (_clearedLines is { } cleared)
            {
                var what = $"Cleared {cleared:N0} line{(cleared == 1 ? "" : "s")}";
                return IsFollowing
                    ? $"{what} — still following {Sources.Count} pod{(Sources.Count == 1 ? "" : "s")}; new lines appear here."
                    : $"{what}. Nothing new arrives until Follow is pressed.";
            }

            if (LogStatus is { Length: > 0 } status)
            {
                return status;
            }

            // Every stream closed without a line, and the pods said why — typically a
            // workload whose pods have never been scheduled. The sentence the chips carry is
            // the body's too, so the two cannot disagree (ENG-45). Only while nothing is
            // still loading: a stream that has not answered yet has not said anything.
            if (!_loadingLogRange
                && Sources.Count > 0
                && Sources.All(s => s.State is LogSourceState.Ended or LogSourceState.NotStarted or LogSourceState.Failed)
                && Sources.Any(s => s.State is LogSourceState.NotStarted)
                && Sources.FirstOrDefault(s => s.StatusMessage is { Length: > 0 }) is { } first)
            {
                return Sources.Count == 1
                    ? first.StatusMessage
                    : Sources.All(s => s.State is LogSourceState.NotStarted)
                        ? $"None of the {Sources.Count} pods has started. {first.ShortName}: {first.StatusMessage}"
                        : $"No lines from {Sources.Count} pods. {first.ShortName}: {first.StatusMessage}";
            }

            if (_loadingLogRange)
            {
                var pending = _streamsByPod.Keys.Count(pod => !_respondedPods.Contains(pod));
                return pending > 0
                    ? $"Waiting for log responses from {pending} pod{(pending == 1 ? "" : "s")} "
                      + $"({SelectedLogRange.Label.ToLowerInvariant()})…"
                    : $"Reading {SelectedLogRange.Label.ToLowerInvariant()} — waiting for opening log lines…";
            }

            if (Sources.All(s => s.State is LogSourceState.Ended or LogSourceState.Loaded)
                && LogSearchText.Length == 0)
            {
                return SelectedLogRange.EmptyMessage;
            }

            if (Sources.Any(s => s.State is LogSourceState.Failed))
            {
                return "Some pod logs could not be loaded. Check the pod chips for details.";
            }

            if (IsFollowing && _streamsByPod.Count > 0 && _streamsByPod.Keys.All(_respondedPods.Contains))
            {
                return SelectedLogRange.WaitingForOutputMessage;
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
