using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Access review: what the current user may do in a namespace (answered by the
/// API server's own SelfSubjectRulesReview), and — when opened for a
/// ServiceAccount — which bindings and roles grant that subject its access.
/// Read-only; nothing here changes RBAC.
/// </summary>
public sealed partial class RbacTabViewModel : InspectorTabViewModelBase
{
    private readonly ClusterClient _client;
    private readonly CancellationTokenSource _cts = new();

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

    public override async Task OnClosingAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
