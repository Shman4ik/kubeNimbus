using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One row of the Applications list. The object survives rebuilds (it is updated in place
/// by key), so the list's selection and scroll position survive a watch event — a row that
/// was replaced by a new instance on every pod restart would lose the highlight the reader
/// was about to press Enter on.
/// </summary>
public sealed partial class ApplicationRowViewModel : ObservableObject
{
    public ApplicationRowViewModel(ApplicationEntry entry, ApplicationAssessment assessment, DateTimeOffset now)
    {
        Key = entry.Key;
        Update(entry, assessment, now);
    }

    public string Key { get; }

    public ApplicationEntry Entry { get; private set; } = null!;

    public ApplicationAssessment Assessment { get; private set; } = null!;

    [ObservableProperty] private string _name = "";

    /// <summary>"Argo CD", or "no Argo" — the second line of the source column is a fact about GitOps, not about the kind.</summary>
    [ObservableProperty] private string _sourceText = "";

    [ObservableProperty] private string _namespaceText = "";

    [ObservableProperty] private AppStatus _status;

    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private string _reason = "";

    [ObservableProperty] private string _podsText = "";

    [ObservableProperty] private string _podsTip = "";

    [ObservableProperty] private string _restartsText = "";

    [ObservableProperty] private string _restartsTip = "";

    [ObservableProperty] private bool _hasRestarts;

    /// <summary>A restart ended within the last hour — the only restarts the column draws in red; a count from last week is history.</summary>
    [ObservableProperty] private bool _hasRecentRestart;

    [ObservableProperty] private string _lastDeployText = "";

    [ObservableProperty] private string _lastDeployTip = "";

    [ObservableProperty] private bool _isRecentDeploy;

    [ObservableProperty] private string _syncText = "";

    [ObservableProperty] private bool _isSynced;

    [ObservableProperty] private bool _isOutOfSync;

    [ObservableProperty] private bool _isArgo;

    [ObservableProperty] private bool _isSystem;

    /// <summary>The group caption this row carries when it is the first of its group ("NEEDS ATTENTION · 6").</summary>
    [ObservableProperty] private string? _groupHeader;

    public bool HasGroupHeader => GroupHeader is not null;

    partial void OnGroupHeaderChanged(string? value) => OnPropertyChanged(nameof(HasGroupHeader));

    public ObservableCollection<PodMarkViewModel> Marks { get; } = [];

    [ObservableProperty] private string _marksOverflow = "";

    public bool NeedsAttention => Status.NeedsAttention();

    // Pill and reason colouring, as bound classes (text is always printed; colour never carries it alone).
    public bool IsError => Status is AppStatus.Degraded or AppStatus.Missing or AppStatus.SyncFailed;

    public bool IsWarn => Status is AppStatus.Unknown or AppStatus.Stalled or AppStatus.OutOfSync;

    public bool IsProgress => Status == AppStatus.Progressing;

    public bool IsOk => Status == AppStatus.Healthy;

    public bool IsIdle => Status == AppStatus.Suspended;

    public IReadOnlyList<string> Namespaces => Entry.Namespaces;

    partial void OnStatusChanged(AppStatus value)
    {
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsProgress));
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsIdle));
    }

    /// <summary>
    /// The search box's predicate: name and namespace, never status — UI rule 13's
    /// reason, unchanged: "Degraded" would match whatever is broken, and that question
    /// already has a chip.
    /// </summary>
    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Entry.Namespaces.Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase));

    internal void Update(ApplicationEntry entry, ApplicationAssessment assessment, DateTimeOffset now)
    {
        Entry = entry;
        Assessment = assessment;
        Name = entry.Name;
        IsArgo = entry.IsArgo;
        SourceText = entry.IsArgo ? "Argo CD" : $"{entry.Source}, no Argo";
        NamespaceText = entry.Namespaces.Count switch
        {
            0 => "—",
            1 => entry.Namespaces[0],
            _ => $"{entry.Namespaces[0]} +{entry.Namespaces.Count - 1}",
        };
        IsSystem = entry.Namespaces.Count > 0 && entry.Namespaces.All(ApplicationScope.IsSystemNamespace);
        Status = assessment.Status;
        StatusText = assessment.Status.Label();
        Reason = assessment.Reason;
        PodsText = assessment.PodsText;
        PodsTip = assessment.Desired < 0
            ? "Nothing here runs a fixed number of replicas."
            : $"{assessment.Ready} of {assessment.Desired} desired pods are Ready.";

        HasRestarts = assessment.Restarts > 0;
        HasRecentRestart = assessment.LastRestartAt is { } lastRestart && now - lastRestart < ApplicationRules.RecentWindow;
        RestartsText = assessment.Restarts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RestartsTip = assessment.LastRestartAt is { } restarted
            ? $"Container restarts across the current pods. The most recent recorded one ended {AppTime.Ago(now, restarted)} — the kubelet keeps only the last termination per container, so earlier restarts have no time."
            : "Container restarts across the current pods (restartCount).";

        if (assessment.LastDeploy is { } deploy)
        {
            var revision = deploy.Revision.Length > 0 ? deploy.Revision : deploy.Source;
            LastDeployText = deploy.At is { } at ? $"{revision} · {AppTime.Ago(now, at)}" : revision;
            IsRecentDeploy = deploy.At is { } t && now - t < TimeSpan.FromHours(1);
            LastDeployTip = deploy.At is { } when
                ? $"{deploy.Source}: {deploy.Revision} at {when.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : $"{deploy.Source}: {deploy.Revision}";
        }
        else
        {
            LastDeployText = entry.IsArgo ? "never synced" : "—";
            IsRecentDeploy = false;
            LastDeployTip = entry.IsArgo
                ? "Argo CD's status.history is empty: this app has never completed a sync."
                : entry.Source is "Deployment"
                    ? "No ReplicaSet of this Deployment could be read."
                    : $"A {entry.Source} records no deploy time the list can read.";
        }

        if (entry.Argo is { } argo)
        {
            SyncText = argo.Sync.ToString();
            IsSynced = argo.Sync == ArgoSyncState.Synced;
            IsOutOfSync = argo.Sync == ArgoSyncState.OutOfSync;
        }
        else
        {
            SyncText = "—";
            IsSynced = false;
            IsOutOfSync = false;
        }

        SyncMarks(assessment.Pods);
    }

    private const int VisibleMarks = 12;

    private void SyncMarks(IReadOnlyList<PodMark> marks)
    {
        var shown = marks.Take(VisibleMarks).ToList();
        while (Marks.Count > shown.Count)
        {
            Marks.RemoveAt(Marks.Count - 1);
        }

        for (var i = 0; i < shown.Count; i++)
        {
            if (i < Marks.Count)
            {
                Marks[i].Update(shown[i]);
            }
            else
            {
                Marks.Add(new PodMarkViewModel(shown[i]));
            }
        }

        MarksOverflow = marks.Count > VisibleMarks ? $"+{marks.Count - VisibleMarks}" : "";
    }
}

/// <summary>One square in the Pods column. States are classes so the view styles them (never a brush binding).</summary>
public sealed partial class PodMarkViewModel : ObservableObject
{
    public PodMarkViewModel(PodMark mark) => Update(mark);

    [ObservableProperty] private string _tip = "";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isWarn;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isGone;
    [ObservableProperty] private bool _isMissing;

    internal void Update(PodMark mark)
    {
        Tip = mark.Text;
        IsMissing = mark.IsMissing;
        IsReady = mark.State is PodDisplayState.Ready;
        IsWarn = mark.State is PodDisplayState.NotReady or PodDisplayState.Pending;
        IsError = mark.State is PodDisplayState.Failing or PodDisplayState.Failed;
        IsDone = mark.State is PodDisplayState.Succeeded;
        IsGone = mark.State is PodDisplayState.Terminating;
    }
}
