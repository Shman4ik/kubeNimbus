using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One subject the RBAC scan says can perform the query, with the bindings that grant it
/// and an on-demand confirmation from the API server itself.
/// </summary>
/// <remarks>
/// The scan reads RBAC objects; <see cref="ClusterClient.CheckAccessAsync"/>
/// (SubjectAccessReview) asks every authorizer. They can legitimately disagree — a webhook
/// authorizer the scan cannot see, for instance — so the verdict is offered per row rather
/// than folded into the list, and a disagreement is shown, not smoothed over.
/// </remarks>
public sealed partial class WhoCanRowViewModel : ObservableObject
{
    private readonly ClusterClient _client;
    private readonly AccessQuery _query;
    private readonly CancellationToken _token;

    public SubjectAccess Access { get; }

    public WhoCanRowViewModel(ClusterClient client, AccessQuery query, SubjectAccess access, CancellationToken token)
    {
        _client = client;
        _query = query;
        _token = token;
        Access = access;
    }

    public string DisplayName => Access.DisplayName;

    public string ScopeText => Access.ScopeText;

    public bool IsClusterWide => Access.IsClusterWide;

    /// <summary>Granted by a rule that hands out everything — worth flagging over a targeted grant.</summary>
    public bool ViaWildcard => Access.ViaWildcard;

    public IReadOnlyList<SubjectBinding> Bindings => Access.Bindings;

    [ObservableProperty]
    private bool _isVerifying;

    /// <summary>The API server's verdict once asked: "allowed", "denied" or "not allowed".</summary>
    [ObservableProperty]
    private string? _verdict;

    [ObservableProperty]
    private string? _verdictDetail;

    /// <summary>
    /// True when the verdict contradicts the scan (the server does not allow what RBAC
    /// appears to grant) or the review could not be run at all.
    /// </summary>
    [ObservableProperty]
    private bool _isVerdictUnexpected;

    [RelayCommand]
    private async Task VerifyAsync()
    {
        IsVerifying = true;
        try
        {
            var decision = await _client.CheckAccessAsync(Access.Subject, _query, _token);
            Verdict = $"API server: {decision.Text}";
            VerdictDetail = decision.EvaluationError ?? decision.Reason;
            IsVerdictUnexpected = !decision.Allowed;
        }
        catch (OperationCanceledException)
        {
            // tab closed mid-check
        }
        catch (Exception ex)
        {
            // Creating a SubjectAccessReview is itself privileged; not being allowed to
            // is a normal outcome here, and says nothing about the subject's access.
            Verdict = "Could not ask the API server";
            VerdictDetail = ex.Message;
            IsVerdictUnexpected = true;
        }
        finally
        {
            IsVerifying = false;
        }
    }
}
