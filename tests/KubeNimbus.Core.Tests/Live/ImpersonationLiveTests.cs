using System.Text;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-56 (security audit S1-8): the kubeconfig's <c>as:</c> against a real API server's
/// impersonation authorizer. <c>ImpersonationTests</c> pins the headers on the wire against a
/// stand-in; these check that a server acts on them — the requests are authorized as the
/// identity named, not as the one whose credential is sent.
/// </summary>
/// <remarks>
/// Two ServiceAccounts of the test namespace, each with a real token: <em>narrow</em> may list
/// ConfigMaps and nothing else; <em>base</em> may list Secrets and ConfigMaps, read pods, exec
/// into them, and impersonate <em>narrow</em> — that one ServiceAccount, by
/// <c>resourceNames</c>. A kubeconfig with base's token and <c>as: narrow</c> must then be
/// refused what narrow may not do, over HTTP and over the exec WebSocket alike, while the same
/// token without <c>as:</c> is allowed — the control that shows the 403 comes from the
/// impersonation and not from the token.
/// </remarks>
public class ImpersonationLiveTests
{
    private static string NarrowName => LiveCluster.Named("imp-narrow");

    private static string BaseName => LiveCluster.Named("imp-base");

    private static string NarrowUser => $"system:serviceaccount:{LiveCluster.Namespace}:{NarrowName}";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _pod;

    [Test]
    [Timeout(120_000)]
    public async Task Acting_as_a_narrow_service_account_is_refused_what_it_may_not_do(CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        await PrepareAsync(admin, ct);
        using var direct = await BaseAsync(admin, actAs: null, ct);
        using var acting = await BaseAsync(admin, actAs: NarrowUser, ct);

        // The control: the token itself may list Secrets.
        await direct.Client.ListResourceOnceAsync(ResourceDescriptor.Secrets, LiveCluster.Namespace, cancellationToken: ct);

        // Acting as narrow: who the server says this is, what narrow may do, and the 403 for
        // what it may not — naming narrow, not the identity behind the token.
        await Assert.That(await acting.Client.GetCurrentUsernameAsync(ct)).IsEqualTo(NarrowUser);
        await acting.Client.ListResourceOnceAsync(ResourceDescriptor.ConfigMaps, LiveCluster.Namespace, cancellationToken: ct);
        var refusal = await Assert.ThrowsAsync<Exception>(
            () => acting.Client.ListResourceOnceAsync(ResourceDescriptor.Secrets, LiveCluster.Namespace, cancellationToken: ct));

        var text = Flatten(refusal!);
        await Assert.That(text).Contains("403").Or.Contains("Forbidden").Or.Contains("forbidden");
        await Assert.That(text).Contains(NarrowUser);
        await Assert.That(text).DoesNotContain($"serviceaccount:{LiveCluster.Namespace}:{BaseName}\"");
    }

    /// <summary>The exec WebSocket carries the impersonation too: base may exec, narrow may not.</summary>
    [Test]
    [Timeout(180_000)]
    public async Task Acting_as_a_narrow_service_account_is_refused_an_exec_over_the_websocket(CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        await PrepareAsync(admin, ct);
        var pod = await PodAsync(admin, ct);
        using var direct = await BaseAsync(admin, actAs: null, ct);
        using var acting = await BaseAsync(admin, actAs: NarrowUser, ct);

        using (var session = await direct.Client.ExecAsync(LiveCluster.Namespace, pod, "app", ["sh", "-c", "echo allowed"], tty: false, cancellationToken: ct))
        {
            using var reader = new StreamReader(session.StdOut, Encoding.UTF8);
            await Assert.That((await reader.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct)).Trim()).IsEqualTo("allowed");
        }

        var refusal = await Assert.ThrowsAsync<Exception>(async () =>
        {
            using var session = await acting.Client.ExecAsync(LiveCluster.Namespace, pod, "app", ["sh", "-c", "echo allowed"], tty: false, cancellationToken: ct);
        });

        await Assert.That(Flatten(refusal!)).Contains("403");
    }

    /// <summary>Impersonating someone the token may not impersonate is refused by the server outright.</summary>
    [Test]
    [Timeout(120_000)]
    public async Task Acting_as_someone_the_token_may_not_impersonate_is_refused(CancellationToken ct)
    {
        using var admin = await LiveCluster.ConnectAsync(ct);
        await PrepareAsync(admin, ct);
        var stranger = $"system:serviceaccount:{LiveCluster.Namespace}:default";
        using var acting = await BaseAsync(admin, actAs: stranger, ct);

        var refusal = await Assert.ThrowsAsync<Exception>(
            () => acting.Client.ListResourceOnceAsync(ResourceDescriptor.ConfigMaps, LiveCluster.Namespace, cancellationToken: ct));

        await Assert.That(Flatten(refusal!)).Contains("impersonate");
    }

    // --------------------------------------------------------------------- helpers

    /// <summary>Creates narrow (ConfigMaps only) once per run; base is created by each <see cref="BaseAsync"/>.</summary>
    private static async Task PrepareAsync(ClusterClient admin, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var narrow = await LiveCluster.CreateNarrowUserAsync(admin, NarrowName, """
                - apiGroups: [""]
                  resources: [configmaps]
                  verbs: [list]
                """, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Task<NarrowUser> BaseAsync(ClusterClient admin, string? actAs, CancellationToken ct) =>
        LiveCluster.CreateNarrowUserAsync(admin, BaseName, $$"""
            - apiGroups: [""]
              resources: [serviceaccounts]
              verbs: [impersonate]
              resourceNames: [{{NarrowName}}]
            - apiGroups: [""]
              resources: [secrets, configmaps]
              verbs: [list]
            - apiGroups: [""]
              resources: [pods]
              verbs: [get]
            - apiGroups: [""]
              resources: [pods/exec]
              verbs: [create, get]
            """, ct, actAs);

    private static async Task<string> PodAsync(ClusterClient admin, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_pod is null)
            {
                var name = LiveCluster.Named("imp-shell");
                await LiveCluster.ApplyAsync(admin, LiveCluster.Deployments, name,
                    LiveCluster.DeploymentYaml(name, 1, "trap 'exit 0' TERM; sleep 3600 & wait $!"), ct);
                await LiveCluster.WaitForReadyPodsAsync(admin, name, 1, ct);
                _pod = (await LiveCluster.PodsOfAsync(admin, name, ct)).Single().Name;
            }

            return _pod;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Flatten(Exception exception)
    {
        var text = new StringBuilder();
        for (var e = exception; e is not null; e = e.InnerException)
        {
            text.Append(e.GetType().Name).Append(": ").Append(e.Message).Append(" | ");
            if (e is k8s.Autorest.HttpOperationException { Response: { } response })
            {
                text.Append((int)response.StatusCode).Append(' ').Append(response.Content).Append(" | ");
            }
        }

        return text.ToString();
    }
}
