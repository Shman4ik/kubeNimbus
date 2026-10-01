using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The accessibility tree has to be readable. Windows UI Automation asks every control's
/// peer for its name, type and children; an exception in any of them reaches the client as
/// "Unexpected HRESULT has been returned from a call to a COM component" and the whole
/// subtree under it is unreadable to a screen reader and to <c>scripts/qa-ui.ps1</c>. The
/// 0.5.1 release pass found that on the application page. Nothing about it shows in a
/// screenshot, so this walks the peer tree of every scenario window the way a client does
/// and reports the first exception with the path to the control that raised it.
/// </summary>
internal static class AutomationChecks
{
    private static readonly List<string> Failures = [];
    private static int _visited;
    private static readonly Dictionary<string, (int Count, string Example)> Unnamed = [];

    /// <summary>The control types a person or a screen reader operates, and so must be able to name.</summary>
    private static readonly HashSet<AutomationControlType> Operable =
    [
        AutomationControlType.Button, AutomationControlType.CheckBox, AutomationControlType.ComboBox,
        AutomationControlType.Edit, AutomationControlType.ListItem, AutomationControlType.MenuItem,
        AutomationControlType.RadioButton, AutomationControlType.Slider, AutomationControlType.Spinner,
        AutomationControlType.TabItem,
    ];

    internal static void Walk(Window window, string name)
    {
        try
        {
            Visit(ControlAutomationPeer.CreatePeerForElement(window), name, "Window");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: creating the window's peer threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Visit(AutomationPeer peer, string scenario, string path)
    {
        _visited++;
        IReadOnlyList<AutomationPeer> children;
        try
        {
            // What a UIA client asks of every element it passes through.
            var accessibleName = peer.GetName();
            var controlType = peer.GetAutomationControlType();
            if (Environment.GetEnvironmentVariable("KN_PEERDEBUG") == "1" && controlType is AutomationControlType.ListItem or AutomationControlType.Button)
                Console.WriteLine($"PEER {controlType} '{accessibleName}' [{peer.GetAutomationId()}] {peer.GetType().Name}");
            // The text box inside a number box is a template part of that control, which is named.
            if (Operable.Contains(controlType) && IsUnreadable(accessibleName) && peer.GetAutomationId() != "PART_TextBox")
            {
                var id = peer.GetAutomationId();
                var key = $"{controlType} [{(string.IsNullOrEmpty(id) ? peer.GetClassName() : id)}]";
                Unnamed[key] = Unnamed.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.Example) : (1, $"{scenario}: {path}");
            }

            _ = peer.GetClassName();
            _ = peer.GetBoundingRectangle();
            children = peer.GetChildren();
        }
        catch (Exception ex)
        {
            Failures.Add($"{scenario}: {path} threw {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            Visit(children[i], scenario, $"{path}/{children[i].GetClassName()}[{i}]");
        }
    }

    /// <summary>
    /// Empty, or a type name: a control whose content is not text reports its content's ToString(),
    /// which a screen reader reads out as "Avalonia.Controls.PathIcon" or a record's property dump.
    /// </summary>
    private static bool IsUnreadable(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.StartsWith("Avalonia.", StringComparison.Ordinal)
        || name.StartsWith("KubeNimbus.", StringComparison.Ordinal) || name.Contains(" { ", StringComparison.Ordinal);

    /// <summary>Called once after every scenario has rendered.</summary>
    internal static void ThrowIfAnyFailed(bool filtered)
    {
        Console.WriteLine($"Unnamed operable controls: {Unnamed.Count} kinds.");
        // Not a gate on what the Windows provider reports (a headless peer is not that), but on the names the
        // app sets: a new icon button without a tooltip or a name shows up here the day it is added.
        foreach (var (key, (count, example)) in Unnamed.OrderByDescending(u => u.Value.Count).Take(80))
            Console.WriteLine($"  UNNAMED {count,4}x {key}  e.g. {example[..Math.Min(example.IndexOf(':') + 1, example.Length)]}...{example[Math.Max(0, example.Length - 150)..]}");
        Console.WriteLine($"Automation peers visited: {_visited}, failures: {Failures.Count}.");
        if (Unnamed.Count > 0)
        {
            throw new InvalidOperationException(
                "Operable controls with no accessible name (a tooltip, AutomationProperties.Name or LabeledBy; see docs/engineering/accessible-names.md):"
                + Environment.NewLine + string.Join(Environment.NewLine, Unnamed.Select(u => $"  {u.Key}: {u.Value.Example}")));
        }

        if (Failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Controls whose automation peer throws:" + Environment.NewLine + string.Join(Environment.NewLine, Failures.Distinct()));
        }

        if (!filtered && _visited < 5000)
        {
            throw new InvalidOperationException($"Only {_visited} automation peers were visited; the walk is not finding them.");
        }
    }
}
