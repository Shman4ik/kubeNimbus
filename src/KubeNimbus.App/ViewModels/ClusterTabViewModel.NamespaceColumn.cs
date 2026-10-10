namespace KubeNimbus.App.ViewModels;

public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// Whether the resource list draws its Namespace column: only where the column tells
    /// rows apart (FEAT-67).
    /// <list type="bullet">
    /// <item>A cluster-scoped kind (a node, a PV, a ClusterRole) has no namespace, and the
    /// column was 130px of blank cells on exactly the kinds that list the most rows.</item>
    /// <item>With exactly one namespace chosen every row is in it, and the column printed the
    /// picker's own value down the list. That holds in a fleet list too: the chosen namespace
    /// is read on every cluster, so its rows are still all in that one namespace, and the
    /// Cluster column is what tells them apart there.</item>
    /// <item>All namespaces or several: the column is what tells the rows apart, which is
    /// what it is for.</item>
    /// </list>
    /// No kind yet keeps it, which is the shape the list opens with. The view re-reads this
    /// when <see cref="SelectedKind"/> or <see cref="SelectedNamespaces"/> changes; a
    /// DataGridColumn is outside the visual tree and cannot bind its own visibility.
    /// </summary>
    public bool IsNamespaceColumnShown =>
        SelectedKind?.Descriptor?.Namespaced != false && SelectedNamespaces.Count != 1;
}
