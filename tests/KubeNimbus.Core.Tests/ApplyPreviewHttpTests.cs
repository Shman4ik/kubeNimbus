using System.Net;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The apply preview over real HTTP. A stand-in API server (<see cref="HttpListener"/> on
/// loopback) answers a real <see cref="ClusterClient"/> built from a real kubeconfig, so
/// the request this feature sends is observed rather than argued from the wire format.
///
/// <para>
/// That distinction is the whole reason these exist. Every failure in this path is a
/// silent one: a preview that forgot <c>dryRun=All</c> would <em>apply the change</em>
/// while claiming to be showing it, and a real apply that inherited the flag would
/// report success and change nothing. Neither throws, and neither is visible in a unit
/// test over the diff engine. What a stand-in cannot cover is defaulting, admission
/// webhooks and the API server's own validation — those need a cluster, and this suite
/// does not pretend otherwise.
/// </para>
/// </summary>
public class ApplyPreviewHttpTests
{
    private static readonly ResourceDescriptor Deployments = new(
        Group: "apps",
        Version: "v1",
        Kind: "Deployment",
        Plural: "deployments",
        SingularName: "deployment",
        Namespaced: true,
        ShortNames: [],
        Categories: []);

    private const string LiveDeployment = """
        {"apiVersion":"apps/v1","kind":"Deployment",
         "metadata":{"name":"web","namespace":"shop","resourceVersion":"41",
                     "managedFields":[{"manager":"kubectl"}]},
         "spec":{"replicas":2,"template":{"spec":{"containers":[{"name":"web","image":"nginx:1.27"}]}}}}
        """;

    private const string PreviewedDeployment = """
        {"apiVersion":"apps/v1","kind":"Deployment",
         "metadata":{"name":"web","namespace":"shop","resourceVersion":"41",
                     "managedFields":[{"manager":"kubenimbus"}]},
         "spec":{"replicas":5,"template":{"spec":{"containers":[{"name":"web","image":"nginx:1.29"}]}}}}
        """;

    private const string ApplyYaml = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: web
          namespace: shop
        spec:
          replicas: 5
        """;

    [Test]
    public async Task A_preview_reads_the_object_then_applies_with_dry_run()
    {
        using var server = new StubApiServer();
        server.Respond("GET", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.OK, LiveDeployment);
        server.Respond("PATCH", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.OK, PreviewedDeployment);

        using var client = server.Connect();
        var preview = await client.PreviewApplyAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus");

        var patch = server.Requests.Single(r => r.Method == "PATCH");
        await Assert.That(patch.Query).IsEqualTo("?fieldManager=kubenimbus&force=false&dryRun=All&fieldValidation=Strict");
        await Assert.That(patch.ContentType).IsEqualTo("application/apply-patch+yaml; charset=utf-8");
        // The body is the editor's YAML as JSON — valid JSON is valid YAML, so the
        // server's apply decoder takes it either way.
        await Assert.That(JsonDocument.Parse(patch.Body).RootElement.GetProperty("spec").GetProperty("replicas").GetInt32())
            .IsEqualTo(5);

        await Assert.That(server.Requests[0].Method).IsEqualTo("GET");
        await Assert.That(preview.Diff.IsCreate).IsFalse();
        await Assert.That(string.Join(" | ", preview.Diff.Changes.Select(c => $"{c.Path}:{c.Before}→{c.After}")))
            .IsEqualTo("spec.replicas:2→5 | spec.template.spec.containers[web].image:nginx:1.27→nginx:1.29");
        await Assert.That(preview.Diff.HiddenBookkeepingCount).IsEqualTo(1);
    }

    /// <summary>
    /// The other half of the same guard: a real apply must carry no <c>dryRun</c>, or it
    /// would report success and change nothing at all.
    /// </summary>
    [Test]
    public async Task A_real_apply_sends_no_dry_run()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.OK, PreviewedDeployment);

        using var client = server.Connect();
        await client.ApplyYamlAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus");

        await Assert.That(server.Requests.Single().Query).IsEqualTo("?fieldManager=kubenimbus&force=false&fieldValidation=Strict");
    }

    [Test]
    public async Task A_forced_preview_asks_for_force_and_dry_run_together()
    {
        using var server = new StubApiServer();
        server.Respond("GET", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.OK, LiveDeployment);
        server.Respond("PATCH", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.OK, PreviewedDeployment);

        using var client = server.Connect();
        await client.PreviewApplyAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus", force: true);

        await Assert.That(server.Requests.Single(r => r.Method == "PATCH").Query)
            .IsEqualTo("?fieldManager=kubenimbus&force=true&dryRun=All&fieldValidation=Strict");
    }

    /// <summary>
    /// A conflict during the preview is the outcome this feature exists for: the same
    /// exception a real apply raises, learned before the object changes.
    /// </summary>
    [Test]
    public async Task A_conflict_during_the_preview_raises_the_apply_conflict()
    {
        const string conflict = """
            {"kind":"Status","status":"Failure",
             "message":"Apply failed with 1 conflict: conflict with \"kubectl-scale\": .spec.replicas",
             "reason":"Conflict","code":409}
            """;

        using var server = new StubApiServer();
        server.Respond("GET", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.OK, LiveDeployment);
        server.Respond("PATCH", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.Conflict, conflict);

        using var client = server.Connect();

        var thrown = await Assert.ThrowsAsync<ServerSideApplyConflictException>(
            async () => await client.PreviewApplyAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus"));

        await Assert.That(thrown!.Message).Contains("kubectl-scale");
    }

    /// <summary>
    /// A 404 on the live read is not a failure — it says the apply would create the
    /// object, which is a different sentence for the panel to print.
    /// </summary>
    [Test]
    public async Task A_missing_object_previews_as_a_creation()
    {
        using var server = new StubApiServer();
        server.Respond("GET", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.NotFound,
            """{"kind":"Status","code":404,"message":"deployments.apps \"web\" not found"}""");
        server.Respond("PATCH", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.OK, PreviewedDeployment);

        using var client = server.Connect();
        var preview = await client.PreviewApplyAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus");

        await Assert.That(preview.Diff.IsCreate).IsTrue();
        await Assert.That(preview.Diff.Changes.Select(c => c.Path)).Contains("spec");
    }

    /// <summary>
    /// A rejection by the server — the validation or webhook failure this preview exists
    /// to surface *before* the object changes — comes back as the server's own sentence.
    /// </summary>
    [Test]
    public async Task A_rejected_preview_carries_the_servers_own_message()
    {
        using var server = new StubApiServer();
        server.Respond("GET", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.OK, LiveDeployment);
        server.Respond("PATCH", "/apis/apps/v1/namespaces/shop/deployments/web", HttpStatusCode.UnprocessableEntity,
            """{"kind":"Status","code":422,"message":"Deployment.apps \"web\" is invalid: spec.replicas: Invalid value: -1"}""");

        using var client = server.Connect();

        var thrown = await Assert.ThrowsAsync<KubernetesApiException>(
            async () => await client.PreviewApplyAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus"));

        await Assert.That(thrown!.Message).Contains("Invalid value: -1");
    }

    private const string DeploymentPath = "/apis/apps/v1/namespaces/shop/deployments/web";

    /// <summary>What the API server answers a misspelled field with once strict validation is on.</summary>
    private const string UnknownFieldStatus = """
        {"kind":"Status","status":"Failure","reason":"BadRequest","code":400,
         "message":"failed to create typed patch object (shop/web; apps/v1, Kind=Deployment): .spec.replicaz: field not declared in schema"}
        """;

    /// <summary>
    /// What a server too old for the parameter answers: the same 400, about the query
    /// parameter rather than about the document. There is no other signal to tell the two
    /// apart, which is why the message is read.
    /// </summary>
    private const string UnsupportedParameterStatus = """
        {"kind":"Status","status":"Failure","reason":"BadRequest","code":400,
         "message":"Unrecognized query parameter: fieldValidation"}
        """;

    /// <summary>
    /// The acceptance criterion, on the preview path: a field the server does not know is
    /// refused in the server's own words rather than pruned into a clean-looking diff.
    /// </summary>
    [Test]
    public async Task An_unknown_field_is_refused_during_the_preview()
    {
        using var server = new StubApiServer();
        server.Respond("GET", DeploymentPath, HttpStatusCode.OK, LiveDeployment);
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.BadRequest, UnknownFieldStatus);

        using var client = server.Connect();

        var thrown = await Assert.ThrowsAsync<ServerSideApplyValidationException>(
            async () => await client.PreviewApplyAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus"));

        await Assert.That(thrown!.Message).Contains(".spec.replicaz");
        // Refused, not retried: the fallback must not read a rejected *field* as a
        // rejected *parameter*, which would apply the typo the strict request just caught.
        await Assert.That(server.Requests.Count(r => r.Method == "PATCH")).IsEqualTo(1);
        await Assert.That(client.SupportsFieldValidation).IsTrue();
    }

    /// <summary>And on the real apply, which is where the field would otherwise be pruned.</summary>
    [Test]
    public async Task An_unknown_field_is_refused_on_a_real_apply()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.BadRequest, UnknownFieldStatus);

        using var client = server.Connect();

        var thrown = await Assert.ThrowsAsync<ServerSideApplyValidationException>(
            async () => await client.ApplyYamlAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus"));

        await Assert.That(thrown!.Message).Contains("field not declared in schema");
    }

    /// <summary>
    /// A strict decoding error that happens to name a field *called* fieldValidation is
    /// still a rejected field, not a rejected parameter. The classification order is what
    /// keeps those apart, and getting it wrong applies the very document being refused.
    /// </summary>
    [Test]
    public async Task A_rejected_field_named_like_the_parameter_is_not_read_as_an_old_server()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.BadRequest, """
            {"kind":"Status","status":"Failure","reason":"BadRequest","code":400,
             "message":"Deployment in version \"v1\" cannot be handled as a Deployment: strict decoding error: unknown field \"spec.fieldValidation\""}
            """);

        using var client = server.Connect();

        await Assert.ThrowsAsync<ServerSideApplyValidationException>(
            async () => await client.ApplyYamlAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus"));

        await Assert.That(server.Requests.Count).IsEqualTo(1);
        await Assert.That(client.SupportsFieldValidation).IsTrue();
    }

    /// <summary>
    /// The pre-1.27 fallback: a server that refuses the parameter gets the same apply
    /// again without it, so applying stays possible. What it costs is strictness, and
    /// <see cref="ClusterClient.SupportsFieldValidation"/> is what says so out loud.
    /// </summary>
    [Test]
    public async Task A_server_that_rejects_the_parameter_gets_the_apply_again_without_it()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.BadRequest, UnsupportedParameterStatus);
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.OK, PreviewedDeployment);

        using var client = server.Connect();
        await client.ApplyYamlAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus");

        await Assert.That(server.Requests.Select(r => r.Query)).IsEquivalentTo([
            "?fieldManager=kubenimbus&force=false&fieldValidation=Strict",
            "?fieldManager=kubenimbus&force=false",
        ]);
        await Assert.That(client.SupportsFieldValidation).IsFalse();
    }

    /// <summary>
    /// And it is asked only once. A retry per apply would double every request on an old
    /// server, and — worse — would leave the two halves of one apply disagreeing about
    /// whether the preview above it was strict.
    /// </summary>
    [Test]
    public async Task A_server_that_rejected_the_parameter_is_not_asked_again()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.BadRequest, UnsupportedParameterStatus);
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.OK, PreviewedDeployment);

        using var client = server.Connect();
        await client.ApplyYamlAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus");
        await client.ApplyYamlAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus");

        await Assert.That(server.Requests.Count).IsEqualTo(3);
        await Assert.That(server.Requests[2].Query).IsEqualTo("?fieldManager=kubenimbus&force=false");
    }

    /// <summary>
    /// A preview taken on such a server says it was not strict, because in the server's
    /// default Warn mode the unknown field is pruned before the dry run produces the
    /// object — so this diff is clean about exactly what strict validation would refuse.
    /// </summary>
    [Test]
    public async Task A_preview_on_a_server_without_the_parameter_reports_that_it_was_not_strict()
    {
        using var server = new StubApiServer();
        server.Respond("GET", DeploymentPath, HttpStatusCode.OK, LiveDeployment);
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.BadRequest, UnsupportedParameterStatus);
        server.Respond("PATCH", DeploymentPath, HttpStatusCode.OK, PreviewedDeployment);

        using var client = server.Connect();
        var preview = await client.PreviewApplyAsync(Deployments, "shop", "web", ApplyYaml, "kubenimbus");

        await Assert.That(preview.StrictValidation).IsFalse();
        await Assert.That(preview.Diff.IsEmpty).IsFalse();
    }

    private sealed record StubRequest(string Method, string Path, string Query, string? ContentType, string Body);

    /// <summary>
    /// A loopback HTTP stand-in for an API server, plus the kubeconfig that points a real
    /// <see cref="ClusterClient"/> at it. Plain HTTP rather than TLS on purpose: what is
    /// under test is the request this feature builds, and a self-signed certificate would
    /// add a trust dance that tests nothing about it.
    /// </summary>
    private sealed class StubApiServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, List<(HttpStatusCode Status, string Body)>> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _served = new(StringComparer.Ordinal);
        private readonly List<StubRequest> _requests = [];
        private readonly string _directory;
        private readonly Task _pump;

        public StubApiServer()
        {
            var port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _pump = Task.Run(PumpAsync);

            _directory = Directory.CreateTempSubdirectory("kubenimbus-stub-api").FullName;
            File.WriteAllText(Path.Combine(_directory, "kubeconfig.yaml"), $$"""
                apiVersion: v1
                kind: Config
                clusters:
                - name: stub
                  cluster:
                    server: http://127.0.0.1:{{port}}
                contexts:
                - name: stub
                  context:
                    cluster: stub
                    user: stub
                current-context: stub
                users:
                - name: stub
                  user:
                    # Not a credential: the stand-in never checks it. The Kubernetes
                    # client refuses to build a config for a user with no auth at all.
                    token: stub-token
                """);
        }

        public IReadOnlyList<StubRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        /// <summary>
        /// Queues one answer for this method and path. Calling it twice for the same
        /// endpoint answers the first request with the first entry and every request
        /// after that with the last — which is what the strict-validation fallback needs:
        /// the first PATCH is refused for carrying <c>fieldValidation</c>, the retry
        /// without it succeeds, and any later apply gets the same success.
        /// </summary>
        public void Respond(string method, string path, HttpStatusCode status, string body)
        {
            var key = $"{method} {path}";
            if (!_responses.TryGetValue(key, out var queued))
            {
                queued = [];
                _responses[key] = queued;
            }

            queued.Add((status, body));
        }

        public ClusterClient Connect() => ClusterClient.Connect(new ClusterContext(
            Name: "stub",
            ClusterName: "stub",
            Namespace: null,
            UserName: "stub",
            KubeconfigPath: Path.Combine(_directory, "kubeconfig.yaml")));

        private async Task PumpAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var path = context.Request.Url!.AbsolutePath;
                lock (_requests)
                {
                    _requests.Add(new StubRequest(
                        context.Request.HttpMethod, path, context.Request.Url.Query, context.Request.ContentType, body));
                }

                var key = $"{context.Request.HttpMethod} {path}";
                (HttpStatusCode Status, string Body) answer =
                    (HttpStatusCode.NotFound, """{"kind":"Status","code":404,"message":"no stub for this request"}""");
                if (_responses.TryGetValue(key, out var canned))
                {
                    var served = _served.TryGetValue(key, out var n) ? n : 0;
                    answer = canned[Math.Min(served, canned.Count - 1)];
                    _served[key] = served + 1;
                }

                var (status, responseBody) = answer;

                var bytes = Encoding.UTF8.GetBytes(responseBody);
                context.Response.StatusCode = (int)status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _pump.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // The pump ends by the listener being closed under it; that is the shutdown path.
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
