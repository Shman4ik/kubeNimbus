using System.Net;
using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #316 (QA Y-8): a Review that ends in a field-manager conflict still shows what the edit
/// would change — the forced dry run's diff, labelled as a force apply — beside the conflict,
/// so the choice to take the fields is made seeing the change. <c>[NotInParallel]</c>: the
/// editor reads "Preview before applying" from <c>settings.json</c>.
/// </summary>
[NotInParallel]
public class ApplyConflictPreviewTests
{
    [Before(Test)]
    public void Redirect() => TestObjects.RedirectStores();

    private const string Path = "/api/v1/namespaces/team-a/configmaps/settings";

    private const string Live = """
        {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"settings","namespace":"team-a","resourceVersion":"7"},"data":{"mode":"slow"}}
        """;

    private const string Previewed = """
        {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"settings","namespace":"team-a","resourceVersion":"7"},"data":{"mode":"fast"}}
        """;

    private const string Conflict = """
        {"kind":"Status","apiVersion":"v1","status":"Failure","message":"Apply failed with 1 conflict: conflict with \"kubectl-client-side-apply\": .data.mode","reason":"Conflict","code":409}
        """;

    private const string EditedYaml = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: settings
          namespace: team-a
        data:
          mode: fast
        """;

    /// <summary>Mutation-checked by dropping the forced dry run: the panel is then absent.</summary>
    [Test]
    public async Task A_conflicting_review_shows_the_force_apply_diff_beside_the_conflict()
    {
        App.Update(s => s with { PreviewApplies = true });
        using var server = new StubApiServer();
        server.Respond("GET", Path, HttpStatusCode.OK, Live);
        server.RespondWhenQueryContains("PATCH", Path, "force=true", HttpStatusCode.OK, Previewed);
        server.Respond("PATCH", Path, HttpStatusCode.Conflict, Conflict);
        var editor = new YamlEditorTabViewModel(server.Connect(), TestObjects.ConfigMapDescriptor, "team-a", "settings", EditedYaml);

        await editor.ApplyCommand.ExecuteAsync(null);

        await Assert.That(editor.ConflictDetails).Contains(".data.mode");
        var preview = editor.PendingPreview;
        await Assert.That(preview).IsNotNull();
        await Assert.That(preview!.IsForce).IsTrue();
        await Assert.That(preview.IsAfterConflict).IsTrue();
        await Assert.That(preview.ConfirmLabel).IsEqualTo("Force apply");
        await Assert.That(preview.Headline).IsEqualTo("Apply conflict. A force apply would take these fields and make 1 change:");
        await Assert.That(preview.Conflict).IsEqualTo(editor.ConflictDetails);
        await Assert.That(preview.HasBody).IsTrue();

        // The panel carries the conflict and the force apply, so the separate card steps aside;
        // Cancel brings it back with its own Force apply.
        await Assert.That(editor.ShowsConflictPanel).IsFalse();
        editor.CancelPreviewCommand.Execute(null);
        await Assert.That(editor.ShowsConflictPanel).IsTrue();

        // Both PATCHes were dry runs: nothing changed on the server.
        var patches = server.Requests.Where(r => r.Method == "PATCH").ToList();
        await Assert.That(patches.Count).IsEqualTo(2);
        await Assert.That(patches.All(r => r.Query.Contains("dryRun=All", StringComparison.Ordinal))).IsTrue();
        await Assert.That(patches[1].Query).Contains("force=true");
    }

    [Test]
    public async Task When_the_forced_dry_run_fails_too_the_conflict_stands_alone()
    {
        App.Update(s => s with { PreviewApplies = true });
        using var server = new StubApiServer();
        server.Respond("GET", Path, HttpStatusCode.OK, Live);
        server.Respond("PATCH", Path, HttpStatusCode.Conflict, Conflict);
        var editor = new YamlEditorTabViewModel(server.Connect(), TestObjects.ConfigMapDescriptor, "team-a", "settings", EditedYaml);

        await editor.ApplyCommand.ExecuteAsync(null);

        await Assert.That(editor.ConflictDetails).IsNotNull();
        await Assert.That(editor.PendingPreview).IsNull();
        await Assert.That(editor.ShowsConflictPanel).IsTrue();
    }
}
