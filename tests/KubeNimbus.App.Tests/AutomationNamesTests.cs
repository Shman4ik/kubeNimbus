namespace KubeNimbus.App.Tests;

/// <summary>
/// An accessible name is a tooltip's first clause, without the shortcut that ends many of them
/// (see docs/engineering/accessible-names.md).
/// </summary>
public class AutomationNamesTests
{
    [Test]
    [Arguments("Show or hide the resource sidebar (Ctrl+B)", "Show or hide the resource sidebar")]
    [Arguments("Preferences… (Ctrl+,)", "Preferences…")]
    [Arguments("Keyboard shortcuts (F1)", "Keyboard shortcuts")]
    [Arguments("Applications: every app's health and why (Ctrl+Shift+A)", "Applications")]
    [Arguments("Deployed in the last hour — the “I just deployed, is everything green?” question", "Deployed in the last hour")]
    [Arguments("Toggle light/dark theme", "Toggle light/dark theme")]
    [Arguments("  Filter by name or namespace  ", "Filter by name or namespace")]
    public async Task A_tooltip_becomes_its_first_clause_without_the_shortcut(string tooltip, string expected)
    {
        await Assert.That(AutomationNames.NameFromTooltip(tooltip)).IsEqualTo(expected);
    }
}
