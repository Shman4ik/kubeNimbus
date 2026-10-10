using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-69: the YAML editor's toolbar button and the preview panel's confirm used to both read
/// "Apply", 100px apart and doing different things. The toolbar button now says what it does
/// under the preference it follows — "Review…" while "Preview before applying" is on, "Apply"
/// when it is off and the button applies directly — and an editor already open follows the
/// preference when it changes.
/// </summary>
[NotInParallel]
public class YamlEditorApplyLabelTests
{
    private static readonly ResourceDescriptor ConfigMaps =
        new("", "v1", "ConfigMap", "configmaps", "configmap", Namespaced: true, ShortNames: [], Categories: []);

    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private static YamlEditorTabViewModel Editor() =>
        new(client: null, ConfigMaps, "shop", "settings", "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: settings\n");

    [Test]
    public async Task WithThePreviewOnTheButtonSaysReview()
    {
        App.Update(s => s with { PreviewApplies = true });

        var editor = Editor();

        await Assert.That(editor.ApplyButtonLabel).IsEqualTo("Review…");
        await Assert.That(editor.ApplyButtonTip).Contains("show the difference before anything changes");
    }

    [Test]
    public async Task WithThePreviewOffTheButtonSaysApply()
    {
        App.Update(s => s with { PreviewApplies = false });

        var editor = Editor();

        await Assert.That(editor.ApplyButtonLabel).IsEqualTo("Apply");
        await Assert.That(editor.ApplyButtonTip).Contains("\"Preview before applying\" is off");
    }

    /// <summary>
    /// An editor open while the preference changes on the preferences page relabels itself,
    /// and one that has closed stops listening.
    /// </summary>
    [Test]
    public async Task AnOpenEditorFollowsThePreferenceAndAClosedOneLetsGo()
    {
        App.Update(s => s with { PreviewApplies = true });
        var editor = Editor();
        var closed = Editor();
        await closed.OnClosingAsync();

        // Through the preferences page itself, the way the switch is turned.
        using var shell = new MainWindowViewModel();
        var page = new PreferencesViewModel(shell);
        page.PreviewApplies = false;

        await Assert.That(App.LoadSettings().PreviewApplies).IsFalse();
        await Assert.That(editor.ApplyButtonLabel).IsEqualTo("Apply");
        await Assert.That(closed.ApplyButtonLabel).IsEqualTo("Review…");

        page.PreviewApplies = true;
        await Assert.That(editor.ApplyButtonLabel).IsEqualTo("Review…");
        page.Detach();
        await editor.OnClosingAsync();
    }
}
