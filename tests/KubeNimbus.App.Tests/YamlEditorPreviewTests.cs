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

    /// <summary>
    /// A dry-run answer as the client builds one: the live object, the object the server
    /// says it would end up with, and the field diff of the two. The panel renders all
    /// three — the line diff comes from the documents, the field list from the diff — so a
    /// test that handed it only the diff would be testing half the thing it renders.
    /// </summary>
    private static ApplyPreview Preview(string? before, string after)
    {
        var previewed = JsonDocument.Parse(after).RootElement.Clone();
        JsonElement? live = before is null ? null : JsonDocument.Parse(before).RootElement.Clone();
        return new ApplyPreview(
            ResourceDiff.Between(live, previewed),
            DynamicResource.FromListItem(previewed, Deployments),
            live is { } element ? DynamicResource.FromListItem(element, Deployments) : null);
    }

    [Test]
    public async Task A_preview_states_how_many_changes_there_are()
    {
        var one = new ApplyPreviewViewModel(Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);
        var two = new ApplyPreviewViewModel(
            Preview("""{"spec":{"replicas":1,"paused":false}}""", """{"spec":{"replicas":2,"paused":true}}"""), isForce: false);

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
        var preview = new ApplyPreviewViewModel(Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":1}}"""), isForce: false);

        await Assert.That(preview.IsEmpty).IsTrue();
        await Assert.That(preview.HasRows).IsFalse();
        await Assert.That(preview.Headline).IsEqualTo("The server reports this apply would change nothing.");
    }

    [Test]
    public async Task A_preview_of_a_creation_says_the_object_is_not_there()
    {
        var preview = new ApplyPreviewViewModel(Preview(before: null, """{"kind":"Deployment"}"""), isForce: false);

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
            Preview("""{"metadata":{"generation":1,"name":"web"}}""", """{"metadata":{"generation":2,"name":"api"}}"""),
            isForce: false);

        await Assert.That(preview.Footnote)
            .IsEqualTo("1 server bookkeeping field hidden (managedFields, resourceVersion, generation)");

        var before = "{" + string.Join(",", Enumerable.Range(0, 250).Select(i => $"\"k{i}\":\"a\"")) + "}";
        var after = "{" + string.Join(",", Enumerable.Range(0, 250).Select(i => $"\"k{i}\":\"b\"")) + "}";
        var big = new ApplyPreviewViewModel(Preview(before, after), isForce: false);

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
        var preview = Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}""");

        await Assert.That(new ApplyPreviewViewModel(preview, isForce: false).ConfirmLabel).IsEqualTo("Apply changes");
        await Assert.That(new ApplyPreviewViewModel(preview, isForce: true).ConfirmLabel).IsEqualTo("Force apply");
    }

    /// <summary>
    /// The direction of a change must be readable without colour — the one place where
    /// "it is green" is not an acceptable way to know what is about to happen.
    /// </summary>
    [Test]
    public async Task Each_row_carries_its_own_marker()
    {
        var preview = new ApplyPreviewViewModel(
            Preview("""{"a":"1","gone":"x"}""", """{"a":"2","added":"y"}"""), isForce: false);

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
            Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);
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
            Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);

        editor.CancelPreviewCommand.Execute(null);

        await Assert.That(editor.PendingPreview).IsNull();
        await Assert.That(editor.StatusMessage).IsEqualTo("Nothing was applied.");
    }

    /// <summary>
    /// The panel's body is the manifest itself, with its changed lines in place — the
    /// shape kubectl diff, git and VS Code all show, and the reason this replaced a list
    /// of field paths. Both line numbers are carried, because a diff read without them
    /// cannot be matched against the document it is about.
    /// </summary>
    [Test]
    public async Task The_panel_renders_the_manifest_as_a_line_diff()
    {
        var preview = new ApplyPreviewViewModel(
            Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);

        await Assert.That(preview.HasBody).IsTrue();
        await Assert.That(preview.LineSummary).IsEqualTo("+1 −1");

        var removed = preview.Lines.Single(l => l.IsRemoved);
        var added = preview.Lines.Single(l => l.IsAdded);

        await Assert.That(removed.Text.Trim()).IsEqualTo("replicas: 1");
        await Assert.That(added.Text.Trim()).IsEqualTo("replicas: 2");
        await Assert.That(removed.Marker).IsEqualTo("−");
        await Assert.That(added.Marker).IsEqualTo("+");
        await Assert.That(removed.LeftNumber).IsNotEqualTo("");
        await Assert.That(removed.RightNumber).IsEqualTo("");
        await Assert.That(added.LeftNumber).IsEqualTo("");
        await Assert.That(added.RightNumber).IsNotEqualTo("");
    }

    /// <summary>
    /// The two modes are two renderings of one row list, so they cannot disagree about
    /// what changed — and the side-by-side one carries the fillers a two-column layout
    /// needs: a removed line has to face a blank on the right.
    /// </summary>
    [Test]
    public async Task The_split_view_aligns_the_same_rows_and_fills_the_missing_halves()
    {
        var preview = new ApplyPreviewViewModel(
            Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2,"paused":true}}"""), isForce: false);

        var changed = preview.SplitLines.Where(p => !p.IsSkipped && (p.Left?.IsRemoved == true || p.Right?.IsAdded == true)).ToArray();

        await Assert.That(changed.Length).IsEqualTo(2);
        await Assert.That(changed[0].HasLeft).IsTrue();
        await Assert.That(changed[0].HasRight).IsTrue();

        // The added line has nothing on the old side, which is the filler.
        await Assert.That(changed[1].HasLeft).IsFalse();
        await Assert.That(changed[1].Right!.Text.Trim()).IsEqualTo("paused: true");
        await Assert.That(preview.SplitLines.Count(p => p.Left?.IsRemoved == true))
            .IsEqualTo(preview.Lines.Count(l => l.IsRemoved));
    }

    /// <summary>
    /// A collapsed run states its own size. A gap that does not is a document with silent
    /// holes in it, which is the failure this whole panel exists to prevent one level up.
    /// </summary>
    [Test]
    public async Task A_collapsed_run_says_how_many_lines_it_stands_for()
    {
        var keys = string.Join(",", Enumerable.Range(0, 30).Select(i => $"\"k{i:00}\":\"same\""));
        var preview = new ApplyPreviewViewModel(
            Preview($"{{\"data\":{{{keys},\"z\":\"before\"}}}}", $"{{\"data\":{{{keys},\"z\":\"after\"}}}}"),
            isForce: false);

        var gap = preview.Lines.First(l => l.IsSkipped);

        await Assert.That(gap.SkippedCount).IsGreaterThan(1);
        await Assert.That(gap.SkippedText).IsEqualTo($"{gap.SkippedCount} unchanged lines");
        await Assert.That(gap.IsLine).IsFalse();
    }

    /// <summary>
    /// The bookkeeping fields are stripped from the documents, not merely from the field
    /// list: managedFields alone is routinely a third of a real object, so a line diff
    /// over the raw documents would open on the one section nobody wants to read.
    /// </summary>
    [Test]
    public async Task The_line_diff_never_shows_server_bookkeeping()
    {
        var preview = new ApplyPreviewViewModel(
            Preview(
                """{"metadata":{"name":"web","resourceVersion":"1"},"spec":{"replicas":1}}""",
                """{"metadata":{"name":"web","resourceVersion":"2","managedFields":[{"manager":"kubenimbus"}]},"spec":{"replicas":2}}"""),
            isForce: false);

        await Assert.That(preview.Lines.Any(l => l.Text.Contains("managedFields", StringComparison.Ordinal))).IsFalse();
        await Assert.That(preview.Lines.Any(l => l.Text.Contains("resourceVersion", StringComparison.Ordinal))).IsFalse();
        await Assert.That(preview.Footnote).Contains("server bookkeeping");
    }

    /// <summary>
    /// A preview that changes nothing has no body at all — which is what keeps its row
    /// Auto-sized rather than star-sized, so the editor above it does not lose half the
    /// dock to a card of blank space.
    /// </summary>
    [Test]
    public async Task A_preview_that_changes_nothing_has_no_diff_body()
    {
        var preview = new ApplyPreviewViewModel(Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":1}}"""), isForce: false);

        await Assert.That(preview.HasBody).IsFalse();
        await Assert.That(preview.Lines.Count).IsEqualTo(0);
        await Assert.That(preview.SplitLines.Count).IsEqualTo(0);
        await Assert.That(preview.LineSummary).IsEqualTo("");
    }

    /// <summary>
    /// Field values equal, document order different. Saying "would change nothing" over a
    /// panel showing moved lines would read as a bug in the panel, so it says which of the
    /// two it is.
    /// </summary>
    [Test]
    public async Task A_reordered_document_with_the_same_values_says_so()
    {
        var preview = new ApplyPreviewViewModel(Preview("""{"a":"1","b":"2"}""", """{"b":"2","a":"1"}"""), isForce: false);

        await Assert.That(preview.HasRows).IsFalse();
        await Assert.That(preview.Headline).Contains("differ only in how the document is ordered");
        await Assert.That(preview.HasBody).IsTrue();
    }

    /// <summary>
    /// The view mode is a view toggle inside a pane, like the log pane's timestamps and
    /// wrap toggles — session-scoped, never a preference, and it survives the next apply
    /// so that choosing side-by-side once does not have to be chosen again.
    /// </summary>
    [Test]
    public async Task The_view_mode_defaults_to_the_line_diff_and_survives_a_new_preview()
    {
        var editor = Editor();

        await Assert.That(editor.PreviewViewMode).IsEqualTo(YamlEditorTabViewModel.PreviewViewModeInline);

        editor.PreviewViewMode = YamlEditorTabViewModel.PreviewViewModeSplit;
        editor.PendingPreview = new ApplyPreviewViewModel(
            Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);

        await Assert.That(editor.PreviewViewMode).IsEqualTo(YamlEditorTabViewModel.PreviewViewModeSplit);
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

    /// <summary>
    /// A preview taken on a server that refused <c>fieldValidation=Strict</c> has to say
    /// so, because in the API server's default <c>Warn</c> mode an unknown field is pruned
    /// before the dry run produces the object — so the diff on screen is clean about
    /// exactly the typo strict validation exists to refuse. A footnote is the same place
    /// the panel already admits what it is not showing.
    /// </summary>
    [Test]
    public async Task A_preview_that_could_not_be_strict_says_so()
    {
        var loose = Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}""") with { StrictValidation = false };

        var panel = new ApplyPreviewViewModel(loose, isForce: false);

        await Assert.That(panel.HasFootnote).IsTrue();
        await Assert.That(panel.Footnote!).Contains("rejected strict field validation");
    }

    /// <summary>
    /// And the ordinary case says nothing extra: a caveat printed under every preview is
    /// one nobody reads when it matters.
    /// </summary>
    [Test]
    public async Task A_strict_preview_carries_no_validation_caveat()
    {
        var panel = new ApplyPreviewViewModel(
            Preview("""{"spec":{"replicas":1}}""", """{"spec":{"replicas":2}}"""), isForce: false);

        await Assert.That(panel.Footnote ?? "").DoesNotContain("strict field validation");
    }
}
