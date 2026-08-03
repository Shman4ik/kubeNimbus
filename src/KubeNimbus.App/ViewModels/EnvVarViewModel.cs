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

    /// <summary>
    /// True when <see cref="DirectValue"/> came from resolving the pod object rather
    /// than from a literal in the spec — a Downward-API <c>fieldRef</c>. The value is
    /// real and shown, and <see cref="SourceDescription"/> rides under it as a caption
    /// so it stays clear where it came from.
    /// </summary>
    public bool IsDerivedValue { get; init; }

    /// <summary>
    /// True when the reference is declared <c>optional: true</c>. It matters: an
    /// optional ref whose key is missing is normal — the container simply starts
    /// without the variable — and reporting that as an error, which is what a failed
    /// reveal used to do, sends people hunting a bug that isn't there.
    /// </summary>
    public bool IsOptionalReference { get; init; }

    /// <summary>A reference whose value isn't on screen yet — shown as a reference, with a Reveal button.</summary>
    public bool IsReference => SourceDescription is not null && DirectValue is null;

    /// <summary>The value line: a literal, or a resolved fieldRef.</summary>
    public bool HasDirectValue => DirectValue is not null;

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
