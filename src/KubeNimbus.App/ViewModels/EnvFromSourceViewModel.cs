namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One <c>envFrom:</c> entry — a whole Secret or ConfigMap imported into the
/// container's environment.
/// </summary>
/// <remarks>
/// There is deliberately no per-key reveal here, and there can't be: the pod spec
/// doesn't declare which keys such a source contributes, so the only honest answer
/// is the object itself. What it does carry is enough to open that object, which is
/// the whole reason anyone reads this line — before, it was a grey string in a list
/// with no way through to the Secret it named.
/// </remarks>
/// <param name="Kind">"Secret" or "ConfigMap".</param>
/// <param name="Name">The object's name, in the pod's namespace.</param>
/// <param name="Description">The line as rendered ("All keys from Secret/db-creds · optional").</param>
public sealed record EnvFromSourceViewModel(string Kind, string Name, string Description);
