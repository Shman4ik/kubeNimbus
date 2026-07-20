using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One entry from a container's <c>env:</c> list. A literal <see cref="DirectValue"/>
/// is shown as-is; a <c>valueFrom.secretKeyRef</c>/<c>configMapKeyRef</c> is shown
/// only as a reference (<see cref="SourceDescription"/>) until the user explicitly
/// reveals it — resolving eagerly would mean a GET per env var on every pod-detail
/// open, and the identity reading the kubeconfig may not have RBAC to read Secrets
/// even when it can read Pods.
/// </summary>
public sealed partial class EnvVarViewModel(
    string name,
    string? directValue,
    string? sourceDescription,
    string? secretOrConfigMapKind,
    string? secretOrConfigMapName,
    string? key) : ObservableObject
{
    public string Name { get; } = name;

    public string? DirectValue { get; } = directValue;

    /// <summary>e.g. "Secret/db-creds · key=password" — null for a literal value.</summary>
    public string? SourceDescription { get; } = sourceDescription;

    public bool IsReference => SourceDescription is not null;

    public bool CanReveal => SecretOrConfigMapKind is not null && SecretOrConfigMapName is not null;

    internal string? SecretOrConfigMapKind { get; } = secretOrConfigMapKind;

    internal string? SecretOrConfigMapName { get; } = secretOrConfigMapName;

    internal string? Key { get; } = key;

    [ObservableProperty]
    private bool _isRevealing;

    [ObservableProperty]
    private string? _revealedValue;

    [ObservableProperty]
    private string? _revealError;
}
