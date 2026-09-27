using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The networking panes (Bundle A: FEAT-59/46, 60, 62, 63, 64), all on the demo cluster,
/// opened through the list's real double-click path — so what renders is the routing, the
/// pane and the demo dataset together, not a hand-built view model.
/// </summary>
internal static class NetworkingScenarios
{
    private static ClusterTabViewModel DemoTab(string group, string kind)
    {
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectedNamespace = "payments";
        var entry = tab.SidebarSections.SelectMany(s => s.Kinds)
            .First(k => k.Descriptor.Group == group && k.Descriptor.Kind == kind);
        tab.SelectKindCommand.Execute(entry);
        return tab;
    }

    private static ClusterTabViewModel Open(string group, string kind, string name, Action<InspectorTabViewModelBase>? arrange = null)
    {
        var tab = DemoTab(group, kind);
        tab.SelectedRow = tab.Rows.First(r => r.Name == name);
        tab.OpenSelectedCommand.Execute(null);
        if (tab.SelectedInspectorTab is { } pane)
        {
            arrange?.Invoke(pane);
        }

        return tab;
    }

    /// <summary>The Services list with kubectl's Details (type · cluster IP · ports) — the entry point.</summary>
    public static ClusterTabViewModel ServiceList() => DemoTab("", "Service");

    /// <summary>
    /// The headline: <c>checkout</c> has two matching pods, one serving and one crash-looping
    /// behind a not-ready endpoint. The verdict says "1 of 2", and the row says why.
    /// </summary>
    public static ClusterTabViewModel ServiceDetail() =>
        Open("", "Service", "checkout", pane =>
        {
            if (pane is ServiceDetailTabViewModel detail)
            {
                detail.SelectedBackend = detail.Backends.FirstOrDefault(b => b.StateText == "Serving");
            }
        });

    /// <summary>Selector matches nothing: its own stated state, not an empty grid.</summary>
    public static ClusterTabViewModel ServiceNoMatch() => Open("", "Service", "checkout-canary");

    /// <summary>Two pods match, both Pending with no IP: nothing serving, and each row says why.</summary>
    public static ClusterTabViewModel ServiceNothingServing() => Open("", "Service", "fraud-detector");

    /// <summary>A selector-less service: the endpoints someone else wrote are the whole truth.</summary>
    public static ClusterTabViewModel ServiceNoSelector() => Open("", "Service", "legacy-billing");

    /// <summary>ExternalName: a CNAME, no pods and no endpoints, and nothing to be ready.</summary>
    public static ClusterTabViewModel ServiceExternalName() => Open("", "Service", "payments-db");

    /// <summary>The Overview tab: addressing, selector, and port → target.</summary>
    public static ClusterTabViewModel ServiceOverview() =>
        Open("", "Service", "checkout", pane =>
        {
            if (pane is ServiceDetailTabViewModel detail)
            {
                detail.SelectedTabIndex = ServiceDetailTabViewModel.OverviewTabIndex;
            }
        });

    /// <summary>FEAT-60: the Ingress list's Class · Hosts · Address · Ports, kubectl's own order.</summary>
    public static ClusterTabViewModel IngressList() => DemoTab("networking.k8s.io", "Ingress");

    /// <summary>
    /// FEAT-63: routes with TLS per host, an openable https URL, a regex path linked to the
    /// host root, and a wildcard host shown as text with Open and Copy disabled.
    /// </summary>
    public static ClusterTabViewModel IngressDetail() => Open("networking.k8s.io", "Ingress", "payments-public");

    /// <summary>FEAT-60: NetworkPolicy's Pod-Selector column, with "all pods" for the empty selector.</summary>
    public static ClusterTabViewModel NetworkPolicyList() => DemoTab("networking.k8s.io", "NetworkPolicy");

    /// <summary>FEAT-64: the rules in words, both directions, peers and ports.</summary>
    public static ClusterTabViewModel NetworkPolicyDetail() =>
        Open("networking.k8s.io", "NetworkPolicy", "checkout-worker", _ => { });

    /// <summary>The Pods tab of a default deny: an empty selector is every pod in the namespace.</summary>
    public static ClusterTabViewModel NetworkPolicyDefaultDenyPods() =>
        Open("networking.k8s.io", "NetworkPolicy", "default-deny-ingress", pane =>
        {
            if (pane is NetworkPolicyDetailTabViewModel detail)
            {
                detail.SelectedTabIndex = NetworkPolicyDetailTabViewModel.PodsTabIndex;
            }
        });

    /// <summary>FEAT-60: EndpointSlices' AddressType · Ports · Endpoints, "&lt;unset&gt;" for an empty slice.</summary>
    public static ClusterTabViewModel EndpointSliceList() => DemoTab("discovery.k8s.io", "EndpointSlice");
}
