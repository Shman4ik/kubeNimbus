using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.App.Demo;
using KubeNimbus.Core;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// NetworkPolicy detail: the policy as rules — which pods it selects, which directions it
/// restricts, who may reach them on which ports — and the pods it selects right now.
/// </summary>
/// <remarks>
/// <para>
/// Not a graph. The feature every competitor that has this actually ships is a list of the
/// matched pods, framed as "spot a selector mismatch quickly"; a topology drawing of a
/// namespace's policies is a different product.
/// </para>
/// <para>
/// The rules follow the list's own watch of the policy. The pods are a one-shot read with
/// Refresh, like node detail's: which pods a policy selects changes as fast as pods are
/// created, which is minutes, and a second watch per open pane is the wrong trade for it.
/// The read is capped, because an empty selector is every pod in the namespace.
/// </para>
/// </remarks>
public sealed partial class NetworkPolicyDetailTabViewModel : InspectorTabViewModelBase
{
    public const int RulesTabIndex = 0;

    public const int PodsTabIndex = 1;

    /// <summary>
    /// The most pods the Pods tab lists. An empty selector selects the whole namespace; the
    /// cap keeps that one bounded request, and a truncated list says so.
    /// </summary>
    internal const int PodCap = 500;

    private readonly ClusterClient? _client;
    private readonly ResourceRowViewModel _row;
    private readonly Func<OwnerRef, string?, Task>? _openPod;
    private readonly OpenNamedLogs? _openLogs;
    private readonly CancellationTokenSource _cts = new();
    private string? _podSelectorQuery;
    private bool _podsRead;

    public NetworkPolicyDetailTabViewModel(
        ClusterClient? client,
        ResourceRowViewModel row,
        Func<OwnerRef, string?, Task>? openPod = null,
        string clusterName = "",
        OpenNamedLogs? openLogs = null)
        : base(clusterName.Length == 0 ? $"NetworkPolicy/{row.Name}" : $"NetworkPolicy/{row.Name} · {clusterName}", isDemo: client is null)
    {
        ArgumentNullException.ThrowIfNull(row);

        _client = client;
        _row = row;
        _openPod = openPod;
        _openLogs = openLogs;
        Key = KeyFor(clusterName, row.Namespace, row.Name);

        _row.PropertyChanged += OnRowChanged;
        RefreshFromRow();
    }

    public static string KeyFor(string clusterName, string @namespace, string name) =>
        $"netpol:{clusterName}/{@namespace}/{name}";

    public override string Key { get; }

    public string Namespace => _row.Namespace;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private NetworkPolicyView? _view;

    /// <summary>"Selects all pods in payments" / "Selects app=checkout-worker" — the chrome row's one line.</summary>
    [ObservableProperty]
    private string _selectsText = "";

    [ObservableProperty]
    private string _policyTypesText = "";

    [ObservableProperty]
    private string _ingressSummary = "";

    [ObservableProperty]
    private string _egressSummary = "";

    [ObservableProperty]
    private IReadOnlyList<PolicyRuleViewModel> _ingressRules = [];

    [ObservableProperty]
    private IReadOnlyList<PolicyRuleViewModel> _egressRules = [];

    /// <summary>Set when policyTypes was not declared and the types were derived the way the API server defaults them.</summary>
    [ObservableProperty]
    private string _policyTypesNote = "";

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResourceRowViewModel.Resource))
        {
            RefreshFromRow();
        }
    }

    private void RefreshFromRow()
    {
        var view = NetworkPolicyRules.Read(_row.Resource);
        View = view;
        SelectsText = view.PodSelector switch
        {
            { IsEmpty: true } => $"Selects all pods in {Namespace}",
            { IsUnreadable: true } => "Selects pods by a selector this app cannot read",
            var s => $"Selects {s.Text}",
        };
        PolicyTypesText = view.PolicyTypesText;
        PolicyTypesNote = view.PolicyTypesDeclared
            ? ""
            : "policyTypes is not set, so it is read the way the API server defaults it: Ingress always, Egress when there are egress rules.";
        IngressSummary = NetworkPolicyRules.DirectionSummary(view, ingress: true);
        EgressSummary = NetworkPolicyRules.DirectionSummary(view, ingress: false);
        IngressRules = [.. view.Ingress.Select((r, i) => new PolicyRuleViewModel(r, i + 1, ingress: true))];
        EgressRules = [.. view.Egress.Select((r, i) => new PolicyRuleViewModel(r, i + 1, ingress: false))];

        // The selected pods depend on the selector only; re-read them when it changes, not on
        // every tick of an unrelated field.
        var query = view.PodSelector.IsEmpty ? "" : view.PodSelector.IsUnreadable ? null : view.PodSelector.Text;
        if (!_podsRead || !string.Equals(query, _podSelectorQuery, StringComparison.Ordinal))
        {
            _podSelectorQuery = query;
            _podsRead = true;
            _ = RefreshPodsAsync();
        }
    }

    // --------------------------------------------------------------------- the pods

    public ObservableCollection<MatchedPodViewModel> Pods { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodsCaption))]
    private bool _isLoadingPods;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPodsError))]
    [NotifyPropertyChangedFor(nameof(PodsCaption))]
    private string? _podsError;

    public bool HasPodsError => !string.IsNullOrEmpty(PodsError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodsCaption))]
    private bool _isPodListTruncated;

    /// <summary>Loading / none / counted / capped — never a blank list (UI rule 9).</summary>
    public string PodsCaption => IsLoadingPods
        ? "Reading the pods this policy selects…"
        : HasPodsError
            ? "Could not read the pods"
            : Pods.Count switch
            {
                0 => $"This policy selects no pod in {Namespace} right now.",
                _ when IsPodListTruncated => $"The first {Pods.Count} pods it selects — there are more.",
                1 => "1 pod selected",
                var n => $"{n} pods selected",
            };

    public bool HasNoPods => !IsLoadingPods && !HasPodsError && Pods.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenPodCommand))]
    [NotifyCanExecuteChangedFor(nameof(PodLogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PodLogsMaximizedCommand))]
    private MatchedPodViewModel? _selectedPod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogsNotice))]
    private string? _logsNotice;

    public bool HasLogsNotice => !string.IsNullOrEmpty(LogsNotice);

    /// <summary>
    /// Reads the pods the policy selects. The empty selector is listed as the whole
    /// namespace on purpose — for a NetworkPolicy that is what it means — and an unreadable
    /// one lists nothing and says why, rather than guessing a wider set.
    /// </summary>
    [RelayCommand]
    private async Task RefreshPodsAsync()
    {
        IsLoadingPods = true;
        PodsError = null;
        LogsNotice = null;
        IsPodListTruncated = false;
        try
        {
            var selector = View?.PodSelector ?? new PolicySelector(true, null, false);
            if (selector.IsUnreadable)
            {
                Pods.Clear();
                PodsError = "The pod selector uses something this app cannot evaluate faithfully (an unknown operator, "
                            + "or an In/NotIn with no values), so it lists no pods rather than guess a wider set.";
                return;
            }

            IReadOnlyList<DynamicResource> pods;
            if (_client is { } client)
            {
                var list = await client.ListResourceCappedAsync(
                    ResourceDescriptor.Pods, Namespace, PodCap, selector.Selector, _cts.Token);
                pods = list.Items;
                IsPodListTruncated = list.IsTruncated;
            }
            else
            {
                pods = [.. DemoData.Pods.Where(p =>
                    string.Equals(p.Namespace, Namespace, StringComparison.Ordinal) && selector.Matches(p.Labels))];
            }

            Pods.Clear();
            SelectedPod = null;
            foreach (var pod in pods.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                Pods.Add(new MatchedPodViewModel(pod));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PodsError = ex.Message;
        }
        finally
        {
            IsLoadingPods = false;
            OnPropertyChanged(nameof(PodsCaption));
            OnPropertyChanged(nameof(HasNoPods));
        }
    }

    private bool CanOpenPod => SelectedPod is not null && _openPod is not null;

    private bool CanOpenPodLogs => SelectedPod is not null && _openLogs is not null;

    [RelayCommand(CanExecute = nameof(CanOpenPod))]
    private async Task OpenPodAsync()
    {
        if (SelectedPod is { } pod && _openPod is not null)
        {
            await _openPod(new OwnerRef("v1", "Pod", pod.Name, pod.Uid, false), pod.Namespace);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenPodLogs))]
    private Task PodLogsAsync() => OpenPodLogsAsync(SelectedPod, maximized: false);

    [RelayCommand(CanExecute = nameof(CanOpenPodLogs))]
    private Task PodLogsMaximizedAsync() => OpenPodLogsAsync(SelectedPod, maximized: true);

    /// <summary>One pod's logs, through the cluster tab's one open-logs path (L3).</summary>
    public async Task OpenPodLogsAsync(MatchedPodViewModel? pod, bool maximized)
    {
        if (pod is null || _openLogs is null)
        {
            return;
        }

        SelectedPod = pod;
        LogsNotice = await _openLogs(new OwnerRef("v1", "Pod", pod.Name, pod.Uid, false), pod.Namespace, maximized, _cts.Token);
    }

    public override async Task OnClosingAsync()
    {
        _row.PropertyChanged -= OnRowChanged;
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}

/// <summary>One ingress or egress rule, as the pane prints it.</summary>
public sealed class PolicyRuleViewModel
{
    public PolicyRuleViewModel(NetworkPolicyRule rule, int number, bool ingress)
    {
        ArgumentNullException.ThrowIfNull(rule);

        Title = $"Rule {number}";
        PeersLabel = ingress ? "From" : "To";
        PeersText = rule.AllPeers
            ? ingress ? "anyone — this rule names no sources" : "anywhere — this rule names no destinations"
            : string.Join("\n", rule.Peers);
        PortsText = rule.AllPorts ? "every port and protocol" : string.Join(", ", rule.Ports);
    }

    public string Title { get; }

    public string PeersLabel { get; }

    public string PeersText { get; }

    public string PortsText { get; }
}

/// <summary>A pod a selector matched, as a detail pane lists it.</summary>
public sealed class MatchedPodViewModel
{
    public MatchedPodViewModel(DynamicResource pod)
    {
        ArgumentNullException.ThrowIfNull(pod);

        Name = pod.Name;
        Namespace = pod.Namespace ?? "";
        Uid = pod.Uid;
        var summary = ResourceStatusSummary.Summarize(pod);
        Status = summary.Status;
        StatusHealth = summary.Health;
        ReadyText = summary.Ready;
        NodeName = pod.Raw.TryGetProperty("spec", out var spec) && spec.TryGetProperty("nodeName", out var node)
            ? node.GetString() ?? ""
            : "";
        AgeText = pod.CreationTimestamp is { } created ? RelativeTime.Compact(DateTimeOffset.UtcNow - created) : "";
    }

    public string Name { get; }

    public string Namespace { get; }

    public string? Uid { get; }

    public string Status { get; }

    public string StatusHealth { get; }

    public string ReadyText { get; }

    public string NodeName { get; }

    public string AgeText { get; }
}
