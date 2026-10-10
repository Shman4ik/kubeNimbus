using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-54: a dock tab is a list item now, and a list item names itself through its content's
/// <c>ToString()</c> (docs/engineering/accessible-names.md, rule 3). Without the override a
/// screen reader, and <c>qa-ui.ps1 dump</c>, read every dock tab as its view model's type name.
/// The tab's title changes in place (a pod detail renames itself when its pod is replaced), so
/// the name is read on demand rather than captured.
/// </summary>
public class DockTabNameTests
{
    private sealed class Tab(string title) : InspectorTabViewModelBase(title)
    {
        public override string Key => Title;
    }

    [Test]
    public async Task A_dock_tab_is_named_by_its_title()
    {
        var tab = new Tab("Pod/payment-service-7f9c8d6bcd-x7k2m");
        await Assert.That(tab.ToString()).IsEqualTo("Pod/payment-service-7f9c8d6bcd-x7k2m");

        tab.Title = "YAML/payment-service";
        await Assert.That(tab.ToString()).IsEqualTo("YAML/payment-service");
    }
}
