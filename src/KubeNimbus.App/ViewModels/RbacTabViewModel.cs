using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Access review, in all three directions: what the current user may do in a namespace
/// (answered by the API server's own SelfSubjectRulesReview); which bindings and roles
/// grant a given ServiceAccount its access; and who can perform a given verb on a given
/// resource. Read-only; nothing here changes RBAC.
/// </summary>
public sealed partial class RbacTabViewModel : InspectorTabViewModelBase
{
    /// <summary>Tab index of the "Who can…" section, for opening the tab straight onto it.</summary>
    public const int WhoCanTabIndex = 2;

    private readonly ClusterClient _client;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<ResourceDescriptor>? _catalog;

    public override string Key { get; }

    public string ReviewNamespace { get; }

    /// <summary>Non-null when the tab was opened for a specific subject (a ServiceAccount row).</summary>
    public SubjectRef? Subject { get; }

    public bool HasSubject => Subject is not null;

    public string SubjectText => Subject is { } s
        ? s.Namespace is null ? $"{s.Kind}/{s.Name}" : $"{s.Kind}/{s.Namespace}:{s.Name}"
        : "";

    public ObservableCollection<PolicyRule> MyRules { get; } = [];

    public ObservableCollection<SubjectBinding> Bindings { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// The API server reporting that it could not enumerate every rule (a webhook
    /// authorizer, typically). Surfaced rather than hidden — a permissions list
    /// silently missing entries is worse than no list.
    /// </summary>
    [ObservableProperty]
    private bool _isIncomplete;

    [ObservableProperty]
    private string? _evaluationError;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Which section is showing; the palette opens the tab straight onto "Who can…".</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    // ---- "Who can do X?" ----------------------------------------------------------

    /// <summary>The RBAC verbs, in the order the API docs list them, plus the wildcard.</summary>
    public IReadOnlyList<string> VerbOptions { get; } =
        ["get", "list", "watch", "create", "update", "patch", "delete", "deletecollection", "impersonate", "*"];

    [ObservableProperty]
    private string _whoCanVerb = "delete";

    /// <summary>Resource as typed: "pods", "deployments.apps", "pods/log", "widgets.example.com".</summary>
    [ObservableProperty]
    private string _whoCanResource = "pods";

    /// <summary>Optional single object name, when the question is about one object.</summary>
    [ObservableProperty]
    private string _whoCanResourceName = "";

    /// <summary>Ask across every namespace rather than only this tab's namespace.</summary>
    [ObservableProperty]
    private bool _whoCanAllNamespaces;

    public ObservableCollection<WhoCanRowViewModel> WhoCanResults { get; } = [];

    [ObservableProperty]
    private bool _isWhoCanRunning;

    /// <summary>False until the first search — the difference between "nobody" and "not asked yet".</summary>
    [ObservableProperty]
    private bool _hasWhoCanRun;

    [ObservableProperty]
    private bool _isWhoCanEmpty;

    /// <summary>A query that could not be resolved (unknown or ambiguous resource), or a failed scan.</summary>
    [ObservableProperty]
    private string? _whoCanError;

    /// <summary>Part of the RBAC surface was unreadable, so the answer may be short.</summary>
    [ObservableProperty]
    private string? _whoCanWarning;

    /// <summary>The question the displayed results actually answer, echoed back above them.</summary>
    [ObservableProperty]
    private string? _whoCanQueryText;

    public RbacTabViewModel(ClusterClient client, string @namespace, SubjectRef? subject = null)
        : base(subject is null ? $"Access/{@namespace}" : $"Access/{subject.Name}")
    {
        _client = client;
        ReviewNamespace = @namespace;
        Subject = subject;
        Key = subject is null ? $"rbac:{@namespace}" : $"rbac:{subject.Kind}/{subject.Namespace}/{subject.Name}";

        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var rules = await _client.GetSelfSubjectRulesAsync(ReviewNamespace, _cts.Token);
            MyRules.Clear();
            foreach (var rule in rules.ResourceRules.Concat(rules.NonResourceRules))
            {
                MyRules.Add(rule);
            }

            IsIncomplete = rules.Incomplete;
            EvaluationError = rules.EvaluationError;

            Bindings.Clear();
            if (Subject is { } subject)
            {
                foreach (var binding in await _client.GetBindingsForSubjectAsync(subject, _cts.Token))
                {
                    Bindings.Add(binding);
                }
            }

            IsEmpty = MyRules.Count == 0 && Bindings.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // tab closed mid-load
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read access rules: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Runs the "who can do X?" scan. Unlike the other two directions this one has no
    /// server endpoint behind it — it reads RBAC objects and matches their rules — so the
    /// view states that plainly and offers a per-subject SubjectAccessReview to confirm.
    /// </summary>
    [RelayCommand]
    private async Task RunWhoCanAsync()
    {
        IsWhoCanRunning = true;
        HasWhoCanRun = true;
        WhoCanError = null;
        WhoCanWarning = null;

        // Cleared up front so a re-run shows only the "Scanning…" state rather than the
        // previous answer sitting under it, looking current.
        WhoCanResults.Clear();
        IsWhoCanEmpty = false;
        try
        {
            var query = await BuildQueryAsync(_cts.Token);
            if (query is null)
            {
                WhoCanQueryText = null;
                return;
            }

            var result = await _client.WhoCanAsync(query, _cts.Token);

            foreach (var subject in result.Subjects)
            {
                WhoCanResults.Add(new WhoCanRowViewModel(_client, query, subject, _cts.Token));
            }

            WhoCanQueryText = query.Text;

            // BuildQueryAsync may already have left a warning (a resource this cluster
            // doesn't serve); both halves matter, so they're joined rather than replaced.
            var warnings = result.Warnings.Prepend(WhoCanWarning ?? "").Where(w => w.Length > 0).ToArray();
            WhoCanWarning = warnings.Length == 0 ? null : string.Join(" · ", warnings);
            IsWhoCanEmpty = WhoCanResults.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // tab closed mid-scan
        }
        catch (Exception ex)
        {
            WhoCanError = $"Could not scan RBAC: {ex.Message}";
        }
        finally
        {
            IsWhoCanRunning = false;
        }
    }

    /// <summary>
    /// Turns what was typed into an <see cref="AccessQuery"/>, resolving the resource
    /// against the cluster's own discovery catalog so "svc", "deploy" or "deployments"
    /// all land on the right plural and API group — and so the namespaced/cluster-scoped
    /// distinction (which decides whether RoleBindings can grant it at all) is read from
    /// the server rather than guessed. Returns null after setting
    /// <see cref="WhoCanError"/> when the text can't be resolved.
    /// </summary>
    private async Task<AccessQuery?> BuildQueryAsync(CancellationToken ct)
    {
        var text = WhoCanResource.Trim();
        if (text.Length == 0)
        {
            WhoCanError = "Name a resource to ask about — for example pods, deployments.apps or pods/log.";
            return null;
        }

        // Subresource first ("deployments.apps/scale"), then the kubectl-style group suffix.
        var baseName = text;
        var subresource = "";
        var slash = baseName.IndexOf('/');
        if (slash >= 0)
        {
            subresource = baseName[(slash + 1)..];
            baseName = baseName[..slash];
        }

        string? explicitGroup = null;
        var dot = baseName.IndexOf('.');
        if (dot >= 0)
        {
            explicitGroup = NormalizeGroup(baseName[(dot + 1)..]);
            baseName = baseName[..dot];
        }

        var catalog = _catalog ??= await _client.GetResourceCatalogAsync(ct);
        var matches = catalog
            .Where(d => explicitGroup is null || string.Equals(d.Group, explicitGroup, StringComparison.OrdinalIgnoreCase))
            .Where(d => Equal(d.Plural, baseName) || Equal(d.SingularName, baseName)
                || d.ShortNames.Any(s => Equal(s, baseName)))
            .ToArray();

        if (matches.Length > 1)
        {
            // The same plural in two API groups is routine (Widget in two CRD groups is
            // in the sandbox for exactly this reason) — asking without the group would
            // silently answer for whichever one sorted first.
            var groups = string.Join(", ", matches.Select(m => m.Group.Length == 0 ? "core" : m.Group).Distinct());
            WhoCanError = $"\"{baseName}\" is served by several API groups ({groups}). Qualify it, e.g. {baseName}.{matches[0].Group}.";
            return null;
        }

        var verb = WhoCanVerb.Trim();
        if (verb.Length == 0)
        {
            WhoCanError = "Pick a verb.";
            return null;
        }

        var name = WhoCanResourceName.Trim();
        var resourceName = name.Length == 0 ? null : name;

        if (matches.Length == 0)
        {
            if (explicitGroup is null)
            {
                WhoCanError = $"This cluster serves no resource called \"{baseName}\". Qualify it with its API group to ask anyway, e.g. {baseName}.example.com.";
                return null;
            }

            // Fully qualified but not served here: still a legitimate question (rules for
            // a CRD that has been uninstalled are exactly what you'd go looking for), so
            // ask it as typed and say that's what happened.
            WhoCanWarning = $"\"{text}\" isn't served by this cluster — asking as typed.";
            return new AccessQuery(
                verb,
                subresource.Length == 0 ? baseName : $"{baseName}/{subresource}",
                explicitGroup,
                resourceName,
                WhoCanAllNamespaces ? null : ReviewNamespace);
        }

        var descriptor = matches[0];
        return new AccessQuery(
            Verb: verb,
            Resource: subresource.Length == 0 ? descriptor.Plural : $"{descriptor.Plural}/{subresource}",
            ApiGroup: descriptor.Group,
            ResourceName: resourceName,
            // A cluster-scoped kind has no namespace to ask about, whatever the toggle says.
            Namespace: !descriptor.Namespaced || WhoCanAllNamespaces ? null : ReviewNamespace,
            ClusterScopedResource: !descriptor.Namespaced);
    }

    /// <summary>"core" is how people say it; the wire (and RBAC) spell the core group "".</summary>
    private static string NormalizeGroup(string group) =>
        string.Equals(group, "core", StringComparison.OrdinalIgnoreCase) ? "" : group;

    private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public override async Task OnClosingAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
