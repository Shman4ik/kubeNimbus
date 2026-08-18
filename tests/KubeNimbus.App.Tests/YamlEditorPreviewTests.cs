using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The apply preview's App-layer half: how a <see cref="ResourceDiff"/> is stated to the
/// reader, and the one invariant that keeps a preview honest — it describes the exact
/// text that produced it, and stops existing the moment that text changes.
///
/// <para>
/// No cluster and no client: every test here runs against the demo shape
/// (<c>client: null</c>), which is also the state in which nothing may reach an API
/// server. What needs a server — the dry-run request itself — is covered over real HTTP
/// by <c>ApplyPreviewHttpTests</c> in the Core suite.
/// </para>
/// </summary>
public class YamlEditorPreviewTests
{
    private static readonly ResourceDescriptor Deployments =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: [], Categories: []);

    private const string Yaml = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: web
        """;

    private static YamlEditorTabViewModel Editor()
    {
        TestObjects.RedirectStores();
        return new YamlEditorTabViewModel(client: null, Deployments, "shop", "web", Yaml);
    }

    private static ResourceDiff Diff(string before, string after) =>
        ResourceDiff.Between(
            JsonDocument.Parse(before).RootElement.Clone(),
            JsonDocument.Parse(after).RootElement.Clone());

    [Test]
    public async Task A_preview_states_how_many_changes_there_are()
    {
        var one = new ApplyPreviewViewModel(Diff("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);
        var two = new ApplyPreviewViewModel(
            Diff("""{"spec":{"replicas":1,"paused":false}}""", """{"spec":{"replicas":2,"paused":true}}"""), isForce: false);

        await Assert.That(one.Headline).IsEqualTo("The server would make 1 change:");
        await Assert.That(two.Headline).IsEqualTo("The server would make 2 changes:");
    }

    /// <summary>
    /// "Nothing would change" is its own sentence and its own layout: it is the answer to
    /// "did my edit actually do anything", and an empty rectangle would read as a bug
    /// (UI rule 9).
    /// </summary>
    [Test]
    public async Task A_preview_that_changes_nothing_says_so_and_has_no_rows()
    {
        var preview = new ApplyPreviewViewModel(Diff("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":1}}"""), isForce: false);

        await Assert.That(preview.IsEmpty).IsTrue();
        await Assert.That(preview.HasRows).IsFalse();
        await Assert.That(preview.Headline).IsEqualTo("The server reports this apply would change nothing.");
    }

    [Test]
    public async Task A_preview_of_a_creation_says_the_object_is_not_there()
    {
        var preview = new ApplyPreviewViewModel(
            ResourceDiff.Between(null, JsonDocument.Parse("""{"kind":"Deployment"}""").RootElement.Clone()),
            isForce: false);

        await Assert.That(preview.IsCreate).IsTrue();
        await Assert.That(preview.Headline).Contains("would create it");
    }

    /// <summary>
    /// What is withheld has to be said out loud. A hidden count that never surfaced would
    /// make the panel quietly incomplete, which is the specific failure this whole feature
    /// exists to prevent one level up.
    /// </summary>
    [Test]
    public async Task Hidden_bookkeeping_and_truncation_are_both_reported()
    {
        var preview = new ApplyPreviewViewModel(
            Diff("""{"metadata":{"generation":1,"name":"web"}}""", """{"metadata":{"generation":2,"name":"api"}}"""),
            isForce: false);

        await Assert.That(preview.Footnote)
            .IsEqualTo("1 server bookkeeping field hidden (managedFields, resourceVersion, generation)");

        var before = "{" + string.Join(",", Enumerable.Range(0, 250).Select(i => $"\"k{i}\":\"a\"")) + "}";
        var after = "{" + string.Join(",", Enumerable.Range(0, 250).Select(i => $"\"k{i}\":\"b\"")) + "}";
        var big = new ApplyPreviewViewModel(Diff(before, after), isForce: false);

        await Assert.That(big.Footnote).IsEqualTo("showing the first 200 of 250");
    }

    /// <summary>
    /// A force-apply confirms under its own word. Taking fields away from another manager
    /// is the more consequential of the two applies and must not be confirmed with the
    /// same button label as an ordinary one.
    /// </summary>
    [Test]
    public async Task A_forced_preview_confirms_under_its_own_label()
    {
        var diff = Diff("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}""");

        await Assert.That(new ApplyPreviewViewModel(diff, isForce: false).ConfirmLabel).IsEqualTo("Apply changes");
        await Assert.That(new ApplyPreviewViewModel(diff, isForce: true).ConfirmLabel).IsEqualTo("Force apply");
    }

    /// <summary>
    /// The direction of a change must be readable without colour — the one place where
    /// "it is green" is not an acceptable way to know what is about to happen.
    /// </summary>
    [Test]
    public async Task Each_row_carries_its_own_marker()
    {
        var preview = new ApplyPreviewViewModel(
            Diff("""{"a":"1","gone":"x"}""", """{"a":"2","added":"y"}"""), isForce: false);

        await Assert.That(string.Join(" ", preview.Rows.Select(r => $"{r.Marker}{r.Path}")))
            .IsEqualTo("~a +added −gone");
        await Assert.That(preview.Rows.Single(r => r.Path == "a").HasBothSides).IsTrue();
        await Assert.That(preview.Rows.Single(r => r.Path == "added").HasBothSides).IsFalse();
    }

    /// <summary>
    /// The invariant. A preview describes one exact document; editing makes it a
    /// description of something no longer on screen, and a stale diff above a live editor
    /// is worse than no diff — it is a wrong answer wearing the server's authority.
    /// </summary>
    [Test]
    public async Task Editing_the_document_discards_an_open_preview()
    {
        var editor = Editor();
        editor.PendingPreview = new ApplyPreviewViewModel(
            Diff("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);
        await Assert.That(editor.HasPendingPreview).IsTrue();

        editor.YamlText = Yaml + "\n  namespace: shop";

        await Assert.That(editor.PendingPreview).IsNull();
        await Assert.That(editor.HasPendingPreview).IsFalse();
    }

    [Test]
    public async Task Cancelling_a_preview_says_nothing_was_applied()
    {
        var editor = Editor();
        editor.PendingPreview = new ApplyPreviewViewModel(
            Diff("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);

        editor.CancelPreviewCommand.Execute(null);

        await Assert.That(editor.PendingPreview).IsNull();
        await Assert.That(editor.StatusMessage).IsEqualTo("Nothing was applied.");
    }

    /// <summary>
    /// The demo cluster has no API server, so there is nothing to dry-run against. Apply
    /// and its preview disable themselves rather than silently doing nothing (demo rule 5).
    /// </summary>
    [Test]
    public async Task The_demo_cluster_can_neither_apply_nor_preview()
    {
        var editor = Editor();

        await Assert.That(editor.IsDemo).IsTrue();
        await Assert.That(editor.ApplyCommand.CanExecute(null)).IsFalse();
        await Assert.That(editor.ConfirmPreviewCommand.CanExecute(null)).IsFalse();
        await Assert.That(editor.ForceApplyCommand.CanExecute(null)).IsFalse();
    }
}
