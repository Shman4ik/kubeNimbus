using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One application's page (layout A): the facts the rules found, its pods, what it is wired
/// to, a timeline of the last hour, what changed in the last deploy, and its logs — all from
/// what the cluster holds right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads the list's snapshot, not a second copy.</b> Every rebuild of the Applications
/// list re-reads this page's application from the same snapshot, so the page is live without
/// any watch of its own. What it adds is the handful of things the list deliberately does
/// not read: Warning Events for the app's objects (evidence and timeline), the objects its
/// pods are wired to, and Argo CD's UI URL. Each is one request per namespace, made when the
/// page opens, and each failure is stated where its answer would have been.
/// </para>
/// <para>
/// <b>Logs are the aggregated pane, embedded.</b> One <see cref="WorkloadLogsTabViewModel"/>
/// over the app's selectors, with the page's pod list choosing which pod it shows — no third
/// log pipeline. A crash-looping pod is chosen by default, on the one run the kubelet holds
/// for it, and the page ends that log with how it ended.
/// </para>
/// </remarks>
public sealed partial class ApplicationPageViewModel : ObservableObject
{
    private readonly ApplicationsViewModel _list;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<DynamicResource> _events = [];
    private bool _eventsLoaded;

    public ApplicationPageViewModel(ApplicationsViewModel list, ApplicationRowViewModel row)
    {
        _list = list;
        Key = row.Key;
        Entry = row.Entry;
        IsDemo = list.IsDemo;

        Pods.Add(PagePodViewModel.Merged());
        Refresh(row.Entry);

        // The default the owner asked for: a crash-looping pod, on its last terminated run,
        // because that log is the reason the page was opened. Otherwise every pod, merged.
        var crashing = Assessment.Findings
            .FirstOrDefault(f => f.Rule is "crash-loop" or "oom-killed" && f.Pod is not null && f.Severity == FindingSeverity.Error)?.Pod;
        SelectedPod = Pods.FirstOrDefault(p => !p.IsMerged && p.Name == crashing) ?? Pods[0];
        CreateLogs();

        list.Rebuilt += OnListRebuilt;
        _ = LoadEventsAsync();
        _ = LoadLinkedAsync();
        _ = LoadArgoUiAsync();
    }

    public string Key { get; }

    public bool IsDemo { get; }

    public ApplicationEntry Entry { get; private set; }

    public ApplicationAssessment Assessment { get; private set; } = null!;

    /// <summary>Back to the list — Esc, or the "‹ Applications" link.</summary>
    [RelayCommand]
    private Task BackAsync() => _list.ClosePageCommand.ExecuteAsync(null);

    // --------------------------------------------------------------- header

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private AppStatus _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _syncText = "";
    [ObservableProperty] private bool _isArgo;
    [ObservableProperty] private bool _isSynced;
    [ObservableProperty] private bool _isOutOfSync;

    public bool IsError => Status is AppStatus.Degraded or AppStatus.Missing or AppStatus.SyncFailed;

    public bool IsWarn => Status is AppStatus.Unknown or AppStatus.Stalled or AppStatus.OutOfSync;

    public bool IsProgress => Status == AppStatus.Progressing;

    public bool IsOk => Status == AppStatus.Healthy;

    public bool IsIdle => Status == AppStatus.Suspended;

    partial void OnStatusChanged(AppStatus value)
    {
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsProgress));
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsIdle));
    }

    // ------------------------------------------------------------- findings

    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNothingWrong))]
    private bool _hasFindings;

    public bool IsNothingWrong => !HasFindings;

    /// <summary>Where the Warning Events stand — the evidence behind half the findings, and they expire.</summary>
    [ObservableProperty] private string _eventsNote = "Reading Warning events…";

    [ObservableProperty] private bool _isEventsNoteProblem;

    // ------------------------------------------------------------------ pods

    public ObservableCollection<PagePodViewModel> Pods { get; } = [];

    [ObservableProperty]
    private PagePodViewModel? _selectedPod;

    partial void OnSelectedPodChanged(PagePodViewModel? value)
    {
        foreach (var pod in Pods)
        {
            pod.IsSelected = ReferenceEquals(pod, value);
        }

        if (Logs is { } logs)
        {
            logs.FocusPod = value is { IsMerged: false } pod2 ? pod2.Name : null;
        }

        if (ShowRunBefore && !CanShowRunBefore)
        {
            ShowRunBefore = false;
        }

        UpdateLogState();
        OnPropertyChanged(nameof(CanShowRunBefore));
    }

    // ------------------------------------------------------------------ logs

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogs))]
    private WorkloadLogsTabViewModel? _logs;

    public bool HasLogs => Logs is not null && LogsUnavailableText is null;

    /// <summary>Why there is no log to show (UI rule 9): the waiting reason, the phase, or the scheduling condition.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogs), nameof(IsLogsUnavailable))]
    private string? _logsUnavailableText;

    public bool IsLogsUnavailable => LogsUnavailableText is not null;

    /// <summary>
    /// "Container worker exited with code 1 (Error) at 08:54:36" — the line the log view ends
    /// with when the chosen pod's container is not running. The last thing a crashed process
    /// printed is rarely the reason it stopped; the exit code often is.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExitFooter))]
    private string? _exitFooter;

    public bool HasExitFooter => ExitFooter is not null;

    partial void OnExitFooterChanged(string? value)
    {
        if (Logs is { } logs)
        {
            logs.Footer = value;
        }
    }

    partial void OnLogsChanged(WorkloadLogsTabViewModel? value)
    {
        if (value is not null)
        {
            value.Footer = ExitFooter;
        }
    }

    /// <summary>What the log pane is showing, in words: which pod, and which run.</summary>
    [ObservableProperty] private string _logsCaption = "";

    /// <summary>
    /// Read the run before the current one. Offered only where the kubelet holds a distinct
    /// one — a container that is running (or has itself ended) with a last termination. For a
    /// container waiting in CrashLoopBackOff "current" and "previous" are the same run, so the
    /// choice would show the same bytes twice under two names.
    /// </summary>
    [ObservableProperty]
    private bool _showRunBefore;

    partial void OnShowRunBeforeChanged(bool value) => CreateLogs();

    public bool CanShowRunBefore =>
        SelectedPod is { IsMerged: false, Facts: { } facts }
        && facts.Containers.Any(c => PodLogRuns.For(c).Any(r => r.Previous));

    private (DynamicResource Workload, ResourceDescriptor Descriptor, IReadOnlyList<LabelSelector> Selectors)? LogScope()
    {
        // The pods of every selector-bearing workload in the app's first namespace, as one
        // stream; a CronJob contributes its most recent Job.
        var sources = Entry.Input.Workloads
            .Select(w => w.Kind == "CronJob"
                ? Entry.Input.Jobs.Where(j => ApplicationRules.IsOwnedBy(j, w)).OrderByDescending(j => j.CreationTimestamp).FirstOrDefault()
                : w)
            .Where(w => w is not null)
            .Select(w => (Workload: w!, Selector: LabelSelector.ForPodsOf(w!)))
            .Where(x => x.Selector is not null)
            .ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        var primary = sources[0];
        var sameNamespace = sources.Where(s => s.Workload.Namespace == primary.Workload.Namespace).ToList();
        var descriptor = DescriptorFor(primary.Workload);
        return (primary.Workload, descriptor, [.. sameNamespace.Select(s => s.Selector!)]);
    }

    private ResourceDescriptor DescriptorFor(DynamicResource resource)
    {
        var group = GroupOf(resource);
        return _list.Tab.DescriptorOf(group, resource.Kind)
            ?? new ResourceDescriptor(group, VersionOf(resource), resource.Kind, resource.Kind.ToLowerInvariant() + "s",
                resource.Kind.ToLowerInvariant(), true, [], []);
    }

    private void CreateLogs()
    {
        var old = Logs;
        Logs = null;
        _ = old?.OnClosingAsync();

        if (LogScope() is { } scope && (_list.Tab.Client is not null || IsDemo))
        {
            Logs = new WorkloadLogsTabViewModel(
                _list.Tab.Client,
                scope.Descriptor,
                scope.Workload,
                scope.Selectors[0],
                options: new WorkloadLogsOptions(
                    [.. scope.Selectors.Skip(1)],
                    FocusPod: SelectedPod is { IsMerged: false } pod ? pod.Name : null,
                    Previous: ShowRunBefore,
                    Embedded: true));
        }

        UpdateLogState();
    }

    private void UpdateLogState()
    {
        var live = Pods.Where(p => !p.IsMerged && p.Facts is not null).ToList();
        if (SelectedPod is { IsMerged: false, Facts: { } facts })
        {
            var reason = PodLogRuns.NoLogsReason(facts);
            LogsUnavailableText = Logs is null && reason is null ? NoLogScopeText() : reason;
            var ended = facts.Containers
                .Where(c => c.State != ContainerStateKind.Running)
                .Select(c => (Container: c, Run: PodLogRuns.For(c).FirstOrDefault(r => !r.Previous)))
                .FirstOrDefault(x => x.Run?.EndedWith is not null);
            if (ShowRunBefore)
            {
                var before = facts.Containers.SelectMany(PodLogRuns.For).FirstOrDefault(r => r.Previous);
                ExitFooter = before?.EndedWith is { } b ? ExitLine(before.Container, b) : null;
                LogsCaption = $"{facts.Name} · run before";
            }
            else
            {
                ExitFooter = ended.Run?.EndedWith is { } e ? ExitLine(ended.Container.Name, e) : null;
                LogsCaption = ended.Run is not null ? $"{facts.Name} · last run" : $"{facts.Name} · live";
            }
        }
        else
        {
            ExitFooter = null;
            LogsCaption = live.Count == 1 ? "1 pod" : $"All pods, merged · {live.Count}";
            if (Logs is null)
            {
                LogsUnavailableText = NoLogScopeText();
            }
            else if (live.Count > 0 && live.All(p => PodLogRuns.NoLogsReason(p.Facts!) is not null))
            {
                var first = PodLogRuns.NoLogsReason(live[0].Facts!)!;
                LogsUnavailableText = live.Count == 1
                    ? first
                    : $"No logs: no container of these {live.Count} pods has started — {live[0].Name}: {FirstSentence(StripNoLogs(first))}";
            }
            else if (live.Count == 0)
            {
                LogsUnavailableText = "No logs: the application has no pods right now.";
            }
            else
            {
                LogsUnavailableText = null;
            }
        }

        OnPropertyChanged(nameof(HasLogs));
    }

    private string NoLogScopeText() =>
        Entry.Input.Workloads.Count == 0
            ? "No logs: none of this application's workloads exists in the cluster."
            : "No logs: this application's workloads declare no pod selector to read logs through.";

    private static string ExitLine(string container, TerminatedState ended) =>
        $"Container {container} exited with {(ended.ExitCode is { } code ? $"code {code}" : "no exit code")}"
        + (ended.Reason.Length > 0 ? $" ({ended.Reason})" : "")
        + (ended.FinishedAt is { } at ? $" at {at.ToLocalTime():HH:mm:ss}" : "");

    // ---------------------------------------------------------------- linked

    public ObservableCollection<LinkedResourceViewModel> Linked { get; } = [];

    [ObservableProperty] private string _linkedNote = "Reading Services, Ingresses and the rest…";

    [ObservableProperty] private bool _hasLinked;

    [RelayCommand]
    private void RevealLinked(LinkedResourceViewModel link)
    {
        if (_list.Tab.RevealInResources(link.Resource.Group, link.Resource.Kind, link.Resource.Namespace, link.Resource.Name))
        {
            _list.SwitchToResources?.Invoke();
        }
    }

    // -------------------------------------------------------------- timeline

    [ObservableProperty] private TimelineWindow? _timeline;
    [ObservableProperty] private string _timelineTitle = "Last 15 min";
    [ObservableProperty] private string _timelineBefore = "";
    [ObservableProperty] private string? _timelineEmpty;

    // ---------------------------------------------------------- what changed

    public ObservableCollection<TemplateChangeViewModel> Changes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangeCard))]
    private string? _changeTitle;

    public bool HasChangeCard => ChangeTitle is not null;

    [ObservableProperty] private string _changeEvidence = "";
    [ObservableProperty] private string? _compareText;
    [ObservableProperty] private string? _compareShas;
    private Uri? _compareUrl;

    public bool HasCompareLink => _compareUrl is not null;

    [RelayCommand(CanExecute = nameof(HasCompareLink))]
    private void OpenCompare()
    {
        if (_compareUrl is { } url)
        {
            OpenInBrowser(url);
        }
    }

    // --------------------------------------------------------------- actions

    /// <summary>The armed Restart or Sync strip (UI rule 17), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingAction))]
    private RowActionViewModel? _pendingAction;

    public bool HasPendingAction => PendingAction is not null;

    private DynamicResource? PrimaryWorkload => Entry.Input.Workloads.FirstOrDefault(w => w.Kind != "Job" && w.Kind != "CronJob")
        ?? Entry.Input.Workloads.FirstOrDefault();

    public bool CanRestart =>
        PrimaryWorkload is { } w && WorkloadActions.SupportsRestart(DescriptorFor(w), w)
        && (_list.Tab.Client is not null || IsDemo);

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart()
    {
        if (PrimaryWorkload is { } w)
        {
            Arm(new RowActionViewModel(RowActionKind.Restart, _list.Tab.Client, DescriptorFor(w), w.Namespace, w.Name));
        }
    }

    public bool CanSync => Entry.Argo is not null && ArgoDescriptor() is { } d && ArgoCd.SupportsSync(d);

    [RelayCommand(CanExecute = nameof(CanSync))]
    private void Sync()
    {
        if (Entry.Argo is { } argo && ArgoDescriptor() is { } d)
        {
            Arm(new RowActionViewModel(RowActionKind.ArgoSync, _list.Tab.Client, d, argo.Namespace, argo.Name));
        }
    }

    private ResourceDescriptor? ArgoDescriptor() =>
        _list.Tab.DescriptorOf(ArgoCd.Group, "Application")
        ?? (IsDemo ? ArgoCd.ApplicationDescriptor(DemoData.BuildCatalog()) : null);

    private void Arm(RowActionViewModel action)
    {
        if (PendingAction is { IsBusy: true })
        {
            return;
        }

        action.Dismissed = () =>
        {
            if (ReferenceEquals(PendingAction, action))
            {
                PendingAction = null;
            }
        };
        IsSelfHealWarningOpen = false;
        PendingAction = action;
    }

    public bool CanEditYaml => PrimaryWorkload is not null && (_list.Tab.Client is not null || IsDemo);

    /// <summary>
    /// Edit YAML. When Argo CD tracks the app with self-heal on, a manual edit is reverted on
    /// Argo's next reconcile — so the strip says that, and names where the change belongs,
    /// before the editor opens. Otherwise the editor opens straight away.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditYaml))]
    private void EditYaml()
    {
        if (Entry.Argo is { SelfHeal: true })
        {
            PendingAction = null;
            IsSelfHealWarningOpen = true;
            return;
        }

        OpenEditor();
    }

    [ObservableProperty] private bool _isSelfHealWarningOpen;

    public string SelfHealWarning => Entry.Argo is { } argo
        ? $"Argo CD self-heal is on for {argo.Name}: a manual edit is reverted on its next reconcile. "
          + $"The change belongs in Git — {argo.RepoUrl}, {argo.SourceSummary}."
        : "";

    [RelayCommand]
    private void EditAnyway()
    {
        IsSelfHealWarningOpen = false;
        OpenEditor();
    }

    [RelayCommand]
    private void CancelEdit() => IsSelfHealWarningOpen = false;

    private void OpenEditor()
    {
        if (PrimaryWorkload is { } w)
        {
            _list.Tab.OpenYamlFor(DescriptorFor(w), w);
            _list.SwitchToResources?.Invoke();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArgoUrl))]
    [NotifyCanExecuteChangedFor(nameof(OpenInArgoCommand))]
    private Uri? _argoUrl;

    /// <summary>Shown only when <c>argocd-cm</c>'s <c>data.url</c> was readable; otherwise hidden rather than guessed.</summary>
    public bool HasArgoUrl => ArgoUrl is not null;

    [RelayCommand(CanExecute = nameof(HasArgoUrl))]
    private void OpenInArgo()
    {
        if (ArgoUrl is { } url)
        {
            OpenInBrowser(url);
        }
    }

    private static void OpenInBrowser(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser registered: nothing useful to do from here; the URL is in the tooltip.
        }
    }

    // --------------------------------------------------------------- refresh

    private void OnListRebuilt(object? sender, EventArgs e)
    {
        if (_list.RowFor(Key) is { } row)
        {
            Refresh(row.Entry);
        }
    }

    private void Refresh(ApplicationEntry entry)
    {
        Entry = entry;
        var input = entry.Input with { Events = _events };
        Assessment = ApplicationRules.Evaluate(input);

        Name = entry.Name;
        Status = Assessment.Status;
        StatusText = Assessment.Status.Label();
        IsArgo = entry.IsArgo;
        SyncText = entry.Argo?.Sync.ToString() ?? "";
        IsSynced = entry.Argo?.Sync == ArgoSyncState.Synced;
        IsOutOfSync = entry.Argo?.Sync == ArgoSyncState.OutOfSync;
        Subtitle = BuildSubtitle(entry);

        var findings = Assessment.Findings.Where(f => f.Severity != FindingSeverity.Info || f.Implies != AppStatus.Healthy).ToList();
        ApplicationsViewModel.Sync(Findings, SyncFindings(findings));
        HasFindings = Findings.Count > 0;

        SyncPods(entry);
        BuildTimeline(input);
        BuildChanges(entry);

        OnPropertyChanged(nameof(CanShowRunBefore));
        OnPropertyChanged(nameof(SelfHealWarning));
        RestartCommand.NotifyCanExecuteChanged();
        SyncCommand.NotifyCanExecuteChanged();
        EditYamlCommand.NotifyCanExecuteChanged();
        UpdateLogState();
    }

    private readonly Dictionary<string, FindingViewModel> _findingsByKey = new(StringComparer.Ordinal);

    private List<FindingViewModel> SyncFindings(IReadOnlyList<Finding> findings)
    {
        var result = new List<FindingViewModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            var key = $"{finding.Rule}|{finding.Title}";
            if (!seen.Add(key))
            {
                continue;
            }

            if (_findingsByKey.TryGetValue(key, out var existing))
            {
                existing.Update(finding);
            }
            else
            {
                existing = _findingsByKey[key] = new FindingViewModel(finding);
            }

            result.Add(existing);
        }

        foreach (var gone in _findingsByKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _findingsByKey.Remove(gone);
        }

        return result;
    }

    private string BuildSubtitle(ApplicationEntry entry)
    {
        var parts = new List<string> { $"{_list.Tab.Header} / {(entry.Namespaces.Count == 0 ? "—" : string.Join(", ", entry.Namespaces))}" };
        if (entry.Argo is { } argo)
        {
            parts.Add("Argo CD");
            if (argo.SourcePath.Length > 0)
            {
                parts.Add(argo.SourcePath);
            }

            parts.Add(!argo.AutoSync ? "manual sync" : argo.SelfHeal ? "auto-sync + self-heal" : "auto-sync, no self-heal");
        }
        else
        {
            parts.Add($"{entry.Source}, not in Argo CD");
        }

        return string.Join(" · ", parts);
    }

    private void SyncPods(ApplicationEntry entry)
    {
        var facts = entry.Input.Pods
            .Select(PodFacts.Read)
            .OrderBy(p => p.DisplayState is PodDisplayState.Failing ? 0 : p.DisplayState is PodDisplayState.Pending or PodDisplayState.NotReady ? 1 : 2)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        var target = new List<PagePodViewModel> { Pods[0] };
        foreach (var pod in facts)
        {
            var existing = Pods.FirstOrDefault(p => !p.IsMerged && p.Name == pod.Name);
            if (existing is null)
            {
                existing = new PagePodViewModel(pod, selected: false);
            }
            else
            {
                existing.Update(pod);
            }

            target.Add(existing);
        }

        ApplicationsViewModel.Sync(Pods, target);
        Pods[0].SetMergedCount(facts.Count);
        if (SelectedPod is { IsMerged: false } selected && !Pods.Contains(selected))
        {
            SelectedPod = Pods[0];
        }
    }

    private void BuildTimeline(ApplicationInput input)
    {
        var window = ApplicationTimeline.Build(ApplicationTimeline.Collect(input), input.Now);
        Timeline = window;
        TimelineTitle = $"Last {(int)window.Span.TotalMinutes} min";
        TimelineBefore = window.DeployBefore is { } before
            ? $"Before that: {before.Label} · {AppTime.Ago(input.Now, before.At)}"
            : "No earlier deploy the cluster still records";
        TimelineEmpty = window.Items.Count == 0
            ? _eventsLoaded
                ? "No deploys, dated terminations or Warning events in this window."
                : "No deploys or dated terminations in this window; Warning events are still being read."
            : null;
    }

    private void BuildChanges(ApplicationEntry entry)
    {
        ChangeTitle = null;
        Changes.Clear();
        CompareText = null;
        CompareShas = null;
        _compareUrl = null;

        var deployment = entry.Input.Workloads.FirstOrDefault(w => w.Kind == "Deployment");
        if (deployment is not null && PodTemplateDiff.PickReplicaSets(deployment, entry.Input.ReplicaSets) is { } pair)
        {
            var diff = PodTemplateDiff.Compare(pair.Previous, pair.Current);
            var when = pair.Current.CreationTimestamp is { } at ? $", {AppTime.Ago(entry.Input.Now, at)}" : "";
            // The Argo revision names this deploy only when it is the one that created the
            // ReplicaSet: the newest history entry within a minute before it, or the sync
            // running now. Otherwise the ReplicaSet came from something else (a restart, a
            // manual edit) and its own revision is the honest name.
            var label = $"rev {diff.CurrentRevision}";
            if (entry.Argo is { } argo && pair.Current.CreationTimestamp is { } created)
            {
                if (argo.History.FirstOrDefault(h => h.DeployedAt is { } d
                        && created - d >= TimeSpan.FromSeconds(-5) && created - d <= ApplicationTimeline.DeployMergeWindow) is { } sync)
                {
                    label = ApplicationRules.RevisionLabel(sync.Revision, ApplicationRules.IsChartSource(argo));
                }
                else if (argo.IsOperationRunning && argo.ShortRevision.Length > 0)
                {
                    label = $"{argo.ShortRevision} (sync in progress)";
                }
            }
            ChangeTitle = diff.Changes.Count == 0
                ? $"Revision {diff.CurrentRevision}{when} changed nothing in the pod template the page compares."
                : $"Started with deploy {label}{when}. The pod template changed:";
            foreach (var change in diff.Changes)
            {
                Changes.Add(new TemplateChangeViewModel(change));
            }

            ChangeEvidence = $"ReplicaSet {pair.Previous.Name} (rev {diff.PreviousRevision}) vs {pair.Current.Name} (rev {diff.CurrentRevision})";
        }

        if (entry.Argo is { } app && GitCompareLink.For(app) is { } compare)
        {
            ChangeTitle ??= "The last two deploys Argo CD recorded:";
            _compareUrl = compare.Link;
            CompareText = compare.Link is not null ? $"Compare {compare.FromShort}…{compare.ToShort} in Git" : null;
            CompareShas = compare.Link is null
                ? compare.IsChart
                    ? $"Chart {compare.FromShort} → {compare.ToShort} (a Helm chart source; no Git compare)"
                    : $"{compare.From} → {compare.To} (no compare link for this Git host)"
                : null;
        }

        OnPropertyChanged(nameof(HasCompareLink));
        OpenCompareCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------- page reads

    private async Task LoadEventsAsync()
    {
        try
        {
            IReadOnlyList<DynamicResource> events;
            if (IsDemo)
            {
                events = [.. DemoData.Events.Where(e => e.Type() == "Warning")];
            }
            else if (_list.Tab.Client is { } client)
            {
                var namespaces = Entry.Namespaces.ToList();
                if (Entry.Argo is { } argo && !namespaces.Contains(argo.Namespace))
                {
                    namespaces.Add(argo.Namespace);
                }

                var all = new List<DynamicResource>();
                foreach (var ns in namespaces)
                {
                    all.AddRange(await client.ListResourceOnceAsync(
                        ResourceDescriptor.Events, ns, fieldSelector: "type=Warning", cancellationToken: _cts.Token));
                }

                events = all;
            }
            else
            {
                return;
            }

            var mine = OwnObjects();
            _events = [.. events.Where(e => e.InvolvedObject() is { } io
                && mine.Contains((io.Kind, e.InvolvedObjectNamespace() ?? e.Namespace ?? "", io.Name)))];
            _eventsLoaded = true;
            EventsNote = _events.Count == 0
                ? "No Warning events in the last hour — Events expire after about an hour, so this says nothing older."
                : $"{_events.Count} Warning event{(_events.Count == 1 ? "" : "s")} for this application (Events are kept about an hour).";
            IsEventsNoteProblem = false;
            Refresh(Entry);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _eventsLoaded = true;
            EventsNote = $"Warning events could not be read: {FirstLine(ex.Message)}";
            IsEventsNoteProblem = true;
            BuildTimeline(Entry.Input with { Events = _events });
        }
    }

    private HashSet<(string Kind, string Namespace, string Name)> OwnObjects()
    {
        var result = new HashSet<(string, string, string)>();
        foreach (var o in Entry.Input.Workloads.Concat(Entry.Input.ReplicaSets).Concat(Entry.Input.Jobs).Concat(Entry.Input.Pods))
        {
            result.Add((o.Kind, o.Namespace ?? "", o.Name));
        }

        if (Entry.Argo is { } argo)
        {
            result.Add(("Application", argo.Namespace, argo.Name));
        }

        return result;
    }

    private static readonly (string Group, string Kind)[] LinkedKinds =
    [
        ("", "Service"),
        ("networking.k8s.io", "Ingress"),
        ("gateway.networking.k8s.io", "HTTPRoute"),
        ("autoscaling", "HorizontalPodAutoscaler"),
        ("policy", "PodDisruptionBudget"),
    ];

    private async Task LoadLinkedAsync()
    {
        var candidates = new List<DynamicResource>();
        var refused = new List<string>();
        if (IsDemo)
        {
            foreach (var (_, kind) in LinkedKinds)
            {
                candidates.AddRange(DemoData.OfKind(kind));
            }
        }
        else if (_list.Tab.Client is { } client)
        {
            foreach (var (group, kind) in LinkedKinds)
            {
                if (_list.Tab.DescriptorOf(group, kind) is not { } descriptor)
                {
                    continue;
                }

                foreach (var ns in Entry.Namespaces)
                {
                    try
                    {
                        candidates.AddRange(await client.ListResourceOnceAsync(descriptor, ns, cancellationToken: _cts.Token));
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        refused.Add($"{descriptor.Plural} in {ns}");
                    }
                }
            }
        }

        var links = LinkedResources.Find(Entry.Input.Workloads, Entry.Input.Pods, candidates);
        Linked.Clear();
        foreach (var link in links)
        {
            Linked.Add(new LinkedResourceViewModel(link));
        }

        HasLinked = Linked.Count > 0;
        LinkedNote = refused.Count > 0
            ? $"Not allowed to list {string.Join(", ", refused.Take(3))}{(refused.Count > 3 ? $" and {refused.Count - 3} more" : "")}; what could be read is shown."
            : Linked.Count == 0 ? "Nothing references or is referenced by this application's pods." : "";
    }

    private async Task LoadArgoUiAsync()
    {
        if (Entry.Argo is not { } argo || IsDemo || _list.Tab.Client is not { } client)
        {
            return;
        }

        try
        {
            var cm = await client.ReadResourceAsync(ResourceDescriptor.ConfigMaps, argo.Namespace, ArgoUi.ConfigMapName, _cts.Token);
            if (ArgoUi.BaseUrl(cm) is { } baseUrl)
            {
                ArgoUrl = ArgoUi.ApplicationUrl(baseUrl, argo);
            }
        }
        catch (Exception)
        {
            // Not readable (a 403 is the common case): the action stays hidden, as specified.
        }
    }

    private static string GroupOf(DynamicResource resource)
    {
        var slash = resource.ApiVersion.IndexOf('/');
        return slash < 0 ? "" : resource.ApiVersion[..slash];
    }

    private static string VersionOf(DynamicResource resource)
    {
        var slash = resource.ApiVersion.IndexOf('/');
        return slash < 0 ? resource.ApiVersion : resource.ApiVersion[(slash + 1)..];
    }

    private static string StripNoLogs(string reason)
    {
        foreach (var prefix in (string[])["No logs yet: ", "No logs: "])
        {
            if (reason.StartsWith(prefix, StringComparison.Ordinal))
            {
                return reason[prefix.Length..];
            }
        }

        return reason;
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    public async Task CloseAsync()
    {
        _list.Rebuilt -= OnListRebuilt;
        await _cts.CancelAsync();
        if (Logs is { } logs)
        {
            Logs = null;
            await logs.OnClosingAsync();
        }
    }
}

/// <summary>One finding on the page: severity as classes, evidence as chips.</summary>
public sealed partial class FindingViewModel : ObservableObject
{
    public FindingViewModel(Finding finding) => Update(finding);

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isWarn;
    [ObservableProperty] private bool _isInfo;

    public ObservableCollection<string> Evidence { get; } = [];

    internal void Update(Finding finding)
    {
        Title = finding.Title;
        Detail = finding.Detail;
        IsError = finding.Severity == FindingSeverity.Error;
        IsWarn = finding.Severity == FindingSeverity.Warning;
        IsInfo = finding.Severity == FindingSeverity.Info;
        var texts = finding.Evidence.Where(e => e.Value.Length > 0 || e.Field.Length > 0).Select(e => e.Text).ToList();
        if (!texts.SequenceEqual(Evidence))
        {
            Evidence.Clear();
            foreach (var text in texts)
            {
                Evidence.Add(text);
            }
        }
    }
}

/// <summary>One row of the page's Pods list; the first row is "All pods, merged".</summary>
public sealed partial class PagePodViewModel : ObservableObject
{
    public PagePodViewModel(PodFacts? facts, bool selected)
    {
        _isSelected = selected;
        if (facts is not null)
        {
            Update(facts);
        }
    }

    public static PagePodViewModel Merged() => new(null, selected: false) { IsMerged = true, Name = "All pods, merged" };

    public bool IsMerged { get; private init; }

    public PodFacts? Facts { get; private set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _restartsText = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isWarn;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isGone;

    internal void SetMergedCount(int count) => StateText = count == 1 ? "1 pod" : $"{count} pods";

    internal void Update(PodFacts facts)
    {
        Facts = facts;
        Name = facts.Name;
        StateText = facts.StateText;
        RestartsText = facts.Restarts > 0 ? $"{facts.Restarts} restart{(facts.Restarts == 1 ? "" : "s")}" : "";
        var state = facts.DisplayState;
        IsReady = state is PodDisplayState.Ready or PodDisplayState.Succeeded;
        IsWarn = state is PodDisplayState.NotReady or PodDisplayState.Pending;
        IsError = state is PodDisplayState.Failing or PodDisplayState.Failed;
        IsGone = state is PodDisplayState.Terminating;
    }
}

/// <summary>One linked object; clicking it shows it in Resources mode.</summary>
public sealed class LinkedResourceViewModel(LinkedResource resource)
{
    public LinkedResource Resource { get; } = resource;

    public string Title => $"{Resource.Kind} {Resource.Name}";

    public string Via => Resource.Via;
}

/// <summary>One changed template field: before struck through, after in green; either may be absent.</summary>
public sealed class TemplateChangeViewModel(TemplateChange change)
{
    public string Field => change.Field;

    public string Before => change.Before ?? "";

    public string After => change.After ?? (change.Before is null ? "" : "removed");

    public bool HasBefore => change.Before is not null;

    public bool IsRemoval => change.After is null;

    public bool IsAddition => change.Before is null;
}
