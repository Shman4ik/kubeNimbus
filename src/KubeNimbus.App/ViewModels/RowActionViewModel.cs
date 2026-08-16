using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>Which mutating action the row action strip is armed for.</summary>
public enum RowActionKind
{
    /// <summary>Set a workload's replica count through its <c>scale</c> subresource.</summary>
    Scale,

    /// <summary>Stamp <c>restartedAt</c> on a workload's pod template (<c>kubectl rollout restart</c>).</summary>
    Restart,

    /// <summary>Delete the object — for a controller-owned pod, that is how it is recreated.</summary>
    Delete,
}

/// <summary>
/// The armed state of one mutating action on one object: what it is about to do, the
/// replica count when it needs one, whether it is running, and how it ended. It is the
/// app's confirm step for scale / rollout restart / delete, and it is one view model
/// for all three deliberately — the confirm sentence, the in-flight state, the RBAC
/// 403 and the success line are identical work three times over otherwise, and three
/// near-identical strips is exactly how they drift apart.
///
/// <para>
/// It carries its own target (client, descriptor, namespace, name), captured when the
/// action was armed. In an aggregated fleet list the client is the row's <em>own</em>
/// cluster's, resolved by <c>ClusterTabViewModel.ClientFor</c>, so a confirm can never
/// land on the cluster the tab happens to be pointed at. And because the target is
/// captured, moving the selection while the strip is open cannot silently re-aim it —
/// the strip names what it will act on, and that is what it acts on.
/// </para>
/// </summary>
public sealed partial class RowActionViewModel : ObservableObject
{
    /// <summary>
    /// Upper bound on the replica box. Not a policy — it is a fat-finger guard, since
    /// the difference between 5 and 5000 replicas is one keystroke and one of them
    /// pages somebody. Anything genuinely bigger is a YAML edit.
    /// </summary>
    /// <remarks>
    /// <c>decimal</c> because that is what <c>NumericUpDown.Maximum</c> is, and an int
    /// constant does not convert in XAML — the binding is to an <c>int?</c> either way.
    /// </remarks>
    public const decimal MaxReplicas = 10_000m;

    /// <summary>Null on the demo cluster — the action has no API server to talk to.</summary>
    private readonly ClusterClient? _client;
    private readonly ResourceDescriptor _descriptor;
    private readonly string? _namespace;
    private readonly string _name;

    public RowActionViewModel(
        RowActionKind kind,
        ClusterClient? client,
        ResourceDescriptor descriptor,
        string? @namespace,
        string name,
        string clusterName = "",
        int? replicas = null)
    {
        Kind = kind;
        _client = client;
        _descriptor = descriptor;
        _namespace = @namespace;
        _name = name;
        _replicas = replicas;

        var where = @namespace is null ? "" : $" in {@namespace}";
        var cluster = clusterName.Length > 0 ? $" · {clusterName}" : "";
        Target = $"{descriptor.Kind}/{name}{where}{cluster}";
    }

    public RowActionKind Kind { get; }

    /// <summary>What this action will act on, spelled out — a confirm that doesn't name its object isn't one.</summary>
    public string Target { get; }

    public bool IsScale => Kind == RowActionKind.Scale;

    public bool IsDelete => Kind == RowActionKind.Delete;

    /// <summary>The sentence above the controls. States the consequence, not the API call.</summary>
    public string Question => Kind switch
    {
        RowActionKind.Scale => $"Scale {Target}",
        RowActionKind.Restart =>
            $"Restart {Target}? Its pods roll under the controller's own update strategy — surge, "
            + "maxUnavailable and PodDisruptionBudgets are all honored.",
        _ => $"Delete {Target}? This cannot be undone.",
    };

    public string ConfirmLabel => Kind switch
    {
        RowActionKind.Scale => "Scale",
        RowActionKind.Restart => "Restart",
        _ => "Delete",
    };

    /// <summary>
    /// True for the demo cluster. All three actions need a real API server, so the strip
    /// says so in place and the confirm button is disabled — never a spinner that hangs
    /// and never a silent no-op (CLAUDE.md's demo rule 5, UI rule 9).
    /// </summary>
    public bool IsDemo => _client is null;

    public const string DemoNotice =
        "Scale, restart and delete change objects on a live API server — the demo cluster has none. "
        + "Everything else about this step is exactly what a real cluster shows.";

    /// <summary>
    /// The target replica count. Null while the authoritative read of the <c>scale</c>
    /// subresource is still in flight, or if it failed — the confirm stays disabled
    /// until there is a number, rather than defaulting to one nobody chose.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(IsScalingToZero))]
    private int? _replicas;

    /// <summary>"currently 3 set · 2 running" — read from the scale subresource, which is
    /// authoritative where the object's own spec.replicas may not be (a CRD can declare a
    /// different specReplicasPath).</summary>
    [ObservableProperty]
    private string? _currentScale;

    /// <summary>Scaling to zero stops every pod. Legitimate and common — and worth saying out loud.</summary>
    public bool IsScalingToZero => IsScale && Replicas == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isBusy;

    /// <summary>True once the action has succeeded: the strip stops being a prompt and
    /// becomes its own result, with one button left (Close).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isDone;

    /// <summary>Inputs and the confirm are live only while the action is neither running nor finished.</summary>
    public bool IsEditable => !IsBusy && !IsDone;

    /// <summary>What happened — in flight, succeeded, or the server's own refusal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isSuccess;

    /// <summary>Called when the strip should go away; set by the owning cluster tab.</summary>
    public Action? Dismissed { get; set; }

    private bool CanConfirm => IsEditable && !IsDemo && (!IsScale || Replicas is not null);

    /// <summary>
    /// Reads the current scale before the user picks a new one. Deliberately the
    /// subresource rather than the row's <c>spec.replicas</c>: that is what the patch
    /// will hit, and for a custom resource it is the only field guaranteed to mean
    /// "replicas". A failure here (RBAC on the subresource, typically) is stated and
    /// the box falls back to whatever the object declared, rather than blocking the
    /// action on a read it doesn't strictly need.
    /// </summary>
    public async Task LoadCurrentScaleAsync()
    {
        if (_client is null || !IsScale)
        {
            return;
        }

        IsBusy = true;
        Message = "Reading the current scale…";
        try
        {
            var scale = await _client.GetScaleAsync(_descriptor, _namespace, _name);
            Replicas = scale.Replicas;
            CurrentScale = scale.CurrentReplicas is { } running
                ? $"currently {scale.Replicas} set · {running} running"
                : $"currently {scale.Replicas}";
            Message = null;
        }
        catch (Exception ex)
        {
            IsError = true;
            Message = $"Could not read the current scale: {FirstLine(ex.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        IsBusy = true;
        IsError = false;
        IsSuccess = false;
        Message = Kind switch
        {
            RowActionKind.Scale => $"Scaling to {Replicas}…",
            RowActionKind.Restart => "Restarting…",
            _ => "Deleting…",
        };

        try
        {
            switch (Kind)
            {
                case RowActionKind.Scale:
                    // Replicas is non-null here — CanConfirm gates on it.
                    var result = await client.ScaleAsync(_descriptor, _namespace, _name, Replicas!.Value);
                    Message = $"Scaled to {result.Replicas}. The list follows the rollout as the watch reports it.";
                    break;

                case RowActionKind.Restart:
                    var at = DateTimeOffset.UtcNow;
                    await client.RestartWorkloadAsync(_descriptor, _namespace, _name, at);
                    Message = $"Restart requested — pod template stamped {WorkloadActions.FormatRestartedAt(at)}.";
                    break;

                default:
                    await client.DeleteResourceAsync(_descriptor, _namespace, _name);
                    Message = "Deleted.";
                    break;
            }

            IsSuccess = true;
            IsDone = true;
        }
        catch (Exception ex)
        {
            // The API server's own sentence, which on the common failure here (a 403)
            // names the subject, the verb and the resource — i.e. the whole diagnosis.
            IsError = true;
            Message = $"{ConfirmLabel} failed: {FirstLine(ex.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Dismisses the strip — the cancel before the action, and the close after it.</summary>
    [RelayCommand]
    private void Dismiss() => Dismissed?.Invoke();

    /// <summary>API-server messages run to several lines; an inline strip gets the first one.</summary>
    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}
