namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The name UI Automation reads for a grid row (#236, accessible-names rule 11): the row's
/// name, then whatever tells two same-named rows apart (namespace, cluster), with the empty
/// parts left out. <c>AutomationNames</c> names a <c>DataGridRow</c> from its item's
/// <c>ToString()</c>, so a row type without one read as an empty <c>DataItem</c>.
/// </summary>
internal static class RowAutomationName
{
    public static string Of(string name, params string?[] qualifiers)
    {
        var where = string.Join(", ", qualifiers.Where(s => !string.IsNullOrEmpty(s)));
        return where.Length == 0 ? name : $"{name}, {where}";
    }
}
