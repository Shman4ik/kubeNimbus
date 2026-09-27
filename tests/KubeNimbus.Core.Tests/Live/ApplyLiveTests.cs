using System.Net;
using System.Text;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-36 (FEAT-41's strict field validation) and VER-32 (FEAT-5's dry-run preview)
/// against a real API server. <c>ApplyPreviewHttpTests</c> pins the request shape against
/// an <see cref="HttpListener"/> stand-in; what only a real server can say is how it
/// actually refuses a field, and what defaulting and admission do to an object before it
/// is stored — which is the whole argument for a server-side preview.
/// </summary>
public class ApplyLiveTests
{
    /// <summary>
    /// What a real server says about a misspelled field in a server-side apply, observed on
    /// k3s v1.33.4: <b>HTTP 500</b>, reason <c>InternalError</c>, message <c>failed to create
    /// typed patch object (ns/name; group/version, Kind=K): .path: field not declared in
    /// schema</c>. Not the 400/422 the classifier was written against — which is why this is
    /// a regression test for a fix and not only an observation.
    /// </summary>
    [Test]
    [Arguments("configmap")]
    [Arguments("deployment")]
    [Arguments("widget")]
    [Timeout(120_000)]
    public async Task A_misspelled_field_is_refused_by_both_the_preview_and_the_apply(string kind, CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named($"typo-{kind}");
        var (descriptor, yaml, field) = kind switch
        {
            // Top level of a core kind.
            "configmap" => (ResourceDescriptor.ConfigMaps, $$"""
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: {{name}}
                  namespace: {{LiveCluster.Namespace}}
                dta:
                  colour: blue
                """, ".dta"),

            // Nested inside a built-in's spec — the typo that matters, because the field
            // it meant to set silently keeps its old value if the typo is pruned.
            "deployment" => (LiveCluster.Deployments,
                LiveCluster.DeploymentYaml(name, 1, "sleep 3600").Replace("  replicas: 1", "  replicaz: 1"),
                ".spec.replicaz"),

            // A custom resource: the field is refused by the CRD's structural schema.
            _ => (LiveCluster.Widgets, $$"""
                apiVersion: shop.kubenimbus.io/v1
                kind: Widget
                metadata:
                  name: {{name}}
                  namespace: {{LiveCluster.Namespace}}
                spec:
                  sku: WDG-LIVE
                  prize: 10
                """, ".spec.prize"),
        };

        var previewRefusal = await Assert.ThrowsAsync<ServerSideApplyValidationException>(async () =>
            await client.PreviewApplyAsync(descriptor, LiveCluster.Namespace, name, yaml, LiveCluster.FieldManager,
                cancellationToken: ct));
        var applyRefusal = await Assert.ThrowsAsync<ServerSideApplyValidationException>(async () =>
            await client.ApplyYamlAsync(descriptor, LiveCluster.Namespace, name, yaml, LiveCluster.FieldManager,
                cancellationToken: ct));

        foreach (var refusal in new[] { previewRefusal!, applyRefusal! })
        {
            // The server's own sentence, naming the field — and one of the three wordings
            // the classifier keys on.
            await Assert.That(refusal.Message).Contains($"{field}: field not declared in schema");
            await Assert.That(refusal.StatusJson).Contains("\"code\":500");
        }

        // Refused, not retried without the parameter, and nothing was created.
        await Assert.That(client.SupportsFieldValidation).IsTrue();
        await Assert.That(await client.ReadResourceAsync(descriptor, LiveCluster.Namespace, name, ct)).IsNull();
    }

    /// <summary>
    /// The same typo sent in the server's non-strict modes, bypassing the client's own
    /// request building: a server-side apply refuses it anyway. Recorded because the
    /// fallback's premise — "without the parameter an unknown field is pruned and the apply
    /// answers 200" — is true of an update or create, and is <b>not</b> what this server does
    /// for an apply patch. The editor's "an unknown or misspelled field is dropped rather than
    /// refused" note, shown after that fallback, is therefore pessimistic on a server like
    /// this one; whether it is accurate on a pre-1.27 server is still unobserved.
    /// </summary>
    [Test]
    [Arguments("Ignore")]
    [Arguments("Warn")]
    [Timeout(60_000)]
    public async Task A_server_side_apply_refuses_an_unknown_field_even_without_strict_validation(
        string mode, CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named($"loose-{mode.ToLowerInvariant()}");
        using var body = new StringContent(
            $$$"""{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"{{{name}}}","namespace":"{{{LiveCluster.Namespace}}}"},"dta":{"a":"b"}}""",
            Encoding.UTF8, "application/apply-patch+yaml");

        using var response = await client.SendRequestAsync(
            HttpMethod.Patch,
            $"{ResourceDescriptor.ConfigMaps.ItemPath(LiveCluster.Namespace, name)}?fieldManager={LiveCluster.FieldManager}&fieldValidation={mode}&dryRun=All",
            body, HttpCompletionOption.ResponseContentRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(text).Contains(".dta: field not declared in schema");
    }

    /// <summary>
    /// VER-32's headline: the preview shows what the text does not say. The edit adds a
    /// container port and does not name a protocol; the server defaults <c>protocol: TCP</c>,
    /// and that default is in the diff — no local diff of the manifest could contain it.
    /// The live object is unchanged afterwards, because the preview is a dry run.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_field_the_server_defaults_appears_in_the_preview_diff(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("defaulting");
        var original = LiveCluster.DeploymentYaml(name, 1, "sleep 3600");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name, original, ct);

        var edited = original.Replace(
            "      readinessProbe:",
            "      ports:\n            - containerPort: 8080\n              name: http\n          readinessProbe:");
        await Assert.That(edited).DoesNotContain("protocol");
        await Assert.That(edited).Contains("containerPort: 8080");

        var preview = await client.PreviewApplyAsync(
            LiveCluster.Deployments, LiveCluster.Namespace, name, edited, LiveCluster.FieldManager, cancellationToken: ct);

        await Assert.That(preview.Diff.IsCreate).IsFalse();
        var ports = preview.Diff.Changes.Single(c => c.Path == "spec.template.spec.containers[app].ports");
        await Assert.That(ports.Kind).IsEqualTo(ResourceChangeKind.Added);
        await Assert.That(ports.After!).Contains("\"protocol\":\"TCP\"");
        await Assert.That(ResourceDiff.ToDiffableYaml(preview.Previewed.Raw)).Contains("protocol: TCP");

        // A dry run: the object on the server did not move.
        var live = await client.ReadResourceAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, ct);
        await Assert.That(live!.ToYaml()).DoesNotContain("containerPort");
    }

    /// <summary>
    /// A mutating admission plugin's effect in the preview of a create: the ServiceAccount
    /// admission plugin injects the service account and its projected token volume into a
    /// Pod, and the API server defaults a dozen scheduling fields — none of which the
    /// manifest says. The pod is not created.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task Admission_and_defaulting_appear_in_the_preview_of_a_create(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("admitted");
        var yaml = $$"""
            apiVersion: v1
            kind: Pod
            metadata:
              name: {{name}}
              namespace: {{LiveCluster.Namespace}}
            spec:
              containers:
                - name: app
                  image: {{LiveCluster.Image}}
                  command: ["sleep", "3600"]
            """;

        var preview = await client.PreviewApplyAsync(
            ResourceDescriptor.Pods, LiveCluster.Namespace, name, yaml, LiveCluster.FieldManager, cancellationToken: ct);

        await Assert.That(preview.Diff.IsCreate).IsTrue();
        await Assert.That(preview.Live).IsNull();
        var text = ResourceDiff.ToDiffableYaml(preview.Previewed.Raw);

        // Mutating admission (ServiceAccount plugin).
        await Assert.That(text).Contains("serviceAccountName: default");
        await Assert.That(text).Contains("kube-api-access-");

        // Defaulting.
        await Assert.That(text).Contains("imagePullPolicy: IfNotPresent");
        await Assert.That(text).Contains("terminationMessagePath: /dev/termination-log");
        await Assert.That(text).Contains("dnsPolicy: ClusterFirst");
        await Assert.That(text).Contains("tolerationSeconds: 300");

        // …and the field diff of a create is the object's top-level fields, spec among them.
        await Assert.That(preview.Diff.Changes.Any(c => c.Path == "spec" && c.Kind == ResourceChangeKind.Added)).IsTrue();

        await Assert.That(await client.ReadResourceAsync(ResourceDescriptor.Pods, LiveCluster.Namespace, name, ct)).IsNull();
    }

    /// <summary>
    /// An invalid manifest is refused by the dry run with the server's own validation
    /// message (a 422, not a strict-field refusal), and the object is unchanged.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task An_invalid_manifest_is_refused_by_the_dry_run_in_the_servers_words(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("invalid");
        var original = LiveCluster.DeploymentYaml(name, 1, "sleep 3600");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name, original, ct);

        var refusal = await Assert.ThrowsAsync<KubernetesApiException>(async () =>
            await client.PreviewApplyAsync(LiveCluster.Deployments, LiveCluster.Namespace, name,
                original.Replace("  replicas: 1", "  replicas: -1"), LiveCluster.FieldManager, cancellationToken: ct));

        await Assert.That(refusal!.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(refusal.ServerMessage!).Contains("spec.replicas: Invalid value: -1");
        await Assert.That(refusal.ServerMessage!).Contains("must be greater than or equal to 0");

        var after = await client.GetScaleAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, ct);
        await Assert.That(after.Replicas).IsEqualTo(1);
    }

    /// <summary>
    /// A field another manager took since the last apply conflicts during the <b>preview</b>,
    /// not only during the apply: here a scale through the <c>scale</c> subresource (what
    /// <c>kubectl scale</c> does) takes <c>spec.replicas</c>, and a preview that sets it
    /// again is refused with a 409 that names the field and the manager. Forcing the
    /// preview shows the change instead, and still changes nothing.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_field_manager_conflict_is_raised_by_the_preview(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("conflict");
        var original = LiveCluster.DeploymentYaml(name, 1, "sleep 3600");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name, original, ct);
        await client.ScaleAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, 2, ct);

        var edited = original.Replace("  replicas: 1", "  replicas: 3");
        var conflict = await Assert.ThrowsAsync<ServerSideApplyConflictException>(async () =>
            await client.PreviewApplyAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, edited,
                LiveCluster.FieldManager, cancellationToken: ct));

        await Assert.That(conflict!.Message).Contains("conflict");
        await Assert.That(conflict.Message).Contains(".spec.replicas");

        var forced = await client.PreviewApplyAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, edited,
            LiveCluster.FieldManager, force: true, cancellationToken: ct);
        var replicas = forced.Diff.Changes.Single(c => c.Path == "spec.replicas");
        await Assert.That(replicas.Before).IsEqualTo("2");
        await Assert.That(replicas.After).IsEqualTo("3");

        var after = await client.GetScaleAsync(LiveCluster.Deployments, LiveCluster.Namespace, name, ct);
        await Assert.That(after.Replicas).IsEqualTo(2);
    }
}
