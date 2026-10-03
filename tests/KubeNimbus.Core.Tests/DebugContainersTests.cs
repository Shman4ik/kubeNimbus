using System.Net;
using System.Text.Json;
using StubApiServer = KubeNimbus.Core.Tests.ApplyPreviewHttpTests.StubApiServer;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The <c>kubectl debug</c> gesture behind the exec pane's "Start debug container": the
/// name, the patch, how the container's status reads, and reuse of one already running.
/// Every mistake here is quiet — a patch without <c>stdin</c> adds a container whose
/// shell exits at once and leaves nothing to exec into, and a reuse check that misses
/// adds a second container the pod then carries until it is recreated.
/// </summary>
public class DebugContainersTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task A_name_is_kubectls_shape()
    {
        var name = DebugContainers.NewName(new Random(7));
        await Assert.That(name).StartsWith("debugger-");
        await Assert.That(name.Length).IsEqualTo("debugger-".Length + 5);
        await Assert.That(name["debugger-".Length..].All(c => "bcdfghjklmnpqrstvwxz2456789".Contains(c))).IsTrue();
    }

    [Test]
    public async Task The_patch_adds_one_interactive_container_targeting_the_container()
    {
        var patch = DebugContainers.Patch("debugger-abcde", " busybox:1.37 ", "web");
        await Assert.That(patch).IsEqualTo(
            """{"spec":{"ephemeralContainers":[{"name":"debugger-abcde","image":"busybox:1.37","imagePullPolicy":"IfNotPresent","targetContainerName":"web","stdin":true,"tty":true,"terminationMessagePolicy":"File"}]}}""");
    }

    [Test]
    public async Task A_container_not_in_the_status_yet_is_starting()
    {
        var pod = Json("""{"status":{"phase":"Running","ephemeralContainerStatuses":[]}}""");
        await Assert.That(DebugContainers.StateOf(pod, "debugger-abcde").Phase).IsEqualTo(DebugContainerPhase.Starting);
    }

    [Test]
    public async Task A_pull_in_progress_is_starting_and_says_so()
    {
        var pod = Json("""
            {"status":{"phase":"Running","ephemeralContainerStatuses":[
              {"name":"debugger-abcde","state":{"waiting":{"reason":"ContainerCreating"}}}]}}
            """);
        var state = DebugContainers.StateOf(pod, "debugger-abcde");
        await Assert.That(state.Phase).IsEqualTo(DebugContainerPhase.Starting);
        await Assert.That(state.Describe()).IsEqualTo("ContainerCreating");
    }

    [Test]
    public async Task A_running_container_is_running()
    {
        var pod = Json("""
            {"status":{"phase":"Running","ephemeralContainerStatuses":[
              {"name":"debugger-other","state":{"waiting":{"reason":"ImagePullBackOff"}}},
              {"name":"debugger-abcde","state":{"running":{"startedAt":"2026-10-03T10:00:00Z"}}}]}}
            """);
        await Assert.That(DebugContainers.StateOf(pod, "debugger-abcde").Phase).IsEqualTo(DebugContainerPhase.Running);
    }

    [Test]
    [Arguments("ErrImagePull")]
    [Arguments("ImagePullBackOff")]
    [Arguments("InvalidImageName")]
    public async Task An_image_that_cannot_be_pulled_fails_with_the_kubelets_words(string reason)
    {
        var pod = Json("""
            {"status":{"phase":"Running","ephemeralContainerStatuses":[
              {"name":"debugger-abcde","state":{"waiting":{"reason":"REASON","message":"Back-off pulling image \"busybx\""}}}]}}
            """.Replace("REASON", reason, StringComparison.Ordinal));
        var state = DebugContainers.StateOf(pod, "debugger-abcde");
        await Assert.That(state.Phase).IsEqualTo(DebugContainerPhase.Failed);
        await Assert.That(state.Describe()).IsEqualTo($"{reason}: Back-off pulling image \"busybx\"");
    }

    [Test]
    public async Task A_container_that_exited_fails_with_its_exit_code()
    {
        var pod = Json("""
            {"status":{"phase":"Running","ephemeralContainerStatuses":[
              {"name":"debugger-abcde","state":{"terminated":{"reason":"Completed","exitCode":0}}}]}}
            """);
        var state = DebugContainers.StateOf(pod, "debugger-abcde");
        await Assert.That(state.Phase).IsEqualTo(DebugContainerPhase.Failed);
        await Assert.That(state.Describe()).IsEqualTo("Completed (exit code 0)");
    }

    [Test]
    public async Task A_pod_that_has_finished_fails()
    {
        var state = DebugContainers.StateOf(Json("""{"status":{"phase":"Succeeded"}}"""), "debugger-abcde");
        await Assert.That(state.Phase).IsEqualTo(DebugContainerPhase.Failed);
    }

    private const string PodWithDebuggers = """
        {"spec":{"containers":[{"name":"web"},{"name":"sidecar"}],"ephemeralContainers":[
           {"name":"debugger-old","image":"busybox:1.37","targetContainerName":"web"},
           {"name":"debugger-side","image":"busybox:1.37","targetContainerName":"sidecar"},
           {"name":"debugger-net","image":"nicolaka/netshoot","targetContainerName":"web"},
           {"name":"debugger-live","image":"busybox:1.37","targetContainerName":"web"}]},
         "status":{"phase":"Running","ephemeralContainerStatuses":[
           {"name":"debugger-old","state":{"terminated":{"reason":"Error","exitCode":1}}},
           {"name":"debugger-side","state":{"running":{}}},
           {"name":"debugger-net","state":{"running":{}}},
           {"name":"debugger-live","state":{"running":{}}}]}}
        """;

    [Test]
    public async Task A_running_debugger_for_the_same_target_and_image_is_reused()
    {
        await Assert.That(DebugContainers.FindReusable(Json(PodWithDebuggers), "web", "busybox:1.37")).IsEqualTo("debugger-live");
        await Assert.That(DebugContainers.FindReusable(Json(PodWithDebuggers), "web", "nicolaka/netshoot")).IsEqualTo("debugger-net");
    }

    [Test]
    public async Task No_debugger_is_reused_for_another_target_image_or_a_dead_one()
    {
        await Assert.That(DebugContainers.FindReusable(Json(PodWithDebuggers), "web", "alpine:3.20")).IsNull();
        await Assert.That(DebugContainers.FindReusable(Json(PodWithDebuggers), "other", "busybox:1.37")).IsNull();
        await Assert.That(DebugContainers.FindReusable(Json("""{"spec":{"containers":[]}}"""), "web", "busybox:1.37")).IsNull();
    }

    // ---- over HTTP ---------------------------------------------------------------

    private const string PodPath = "/api/v1/namespaces/shop/pods/web-0";

    [Test]
    public async Task The_ptrace_patch_asks_for_sys_ptrace_and_nothing_else()
    {
        var patch = DebugContainers.Patch("debugger-abcde", "busybox:1.37", "web", ptrace: true);
        await Assert.That(patch).IsEqualTo(
            """{"spec":{"ephemeralContainers":[{"name":"debugger-abcde","image":"busybox:1.37","imagePullPolicy":"IfNotPresent","targetContainerName":"web","stdin":true,"tty":true,"terminationMessagePolicy":"File","securityContext":{"capabilities":{"add":["SYS_PTRACE"]}}}]}}""");
    }

    [Test]
    public async Task A_pod_security_refusal_is_recognized_and_nothing_else_is()
    {
        await Assert.That(DebugContainers.IsPodSecurityRefusal(
            "pods \"web-0\" is forbidden: violates PodSecurity \"baseline:latest\": non-default capabilities (container \"debugger-abcde\" must not include \"SYS_PTRACE\" in securityContext.capabilities.add) (403 Forbidden)")).IsTrue();
        await Assert.That(DebugContainers.IsPodSecurityRefusal(
            "pods \"web-0\" is forbidden: User \"dev\" cannot patch resource \"pods/ephemeralcontainers\"")).IsFalse();
        await Assert.That(DebugContainers.IsPodSecurityRefusal(null)).IsFalse();
    }

    [Test]
    public async Task Adding_a_debug_container_is_a_strategic_merge_patch_of_the_subresource_with_ptrace()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", PodPath + "/ephemeralcontainers", HttpStatusCode.OK, """{"kind":"Pod"}""");
        using var client = server.Connect();

        var added = await client.AddDebugContainerAsync("shop", "web-0", "web", "busybox:1.37", name: "debugger-abcde");

        await Assert.That(added).IsEqualTo(new DebugContainerAdded("debugger-abcde", CanTrace: true));
        var patch = server.Requests.Single(r => r.Method == "PATCH");
        await Assert.That(patch.Path).IsEqualTo(PodPath + "/ephemeralcontainers");
        await Assert.That(patch.ContentType).StartsWith("application/strategic-merge-patch+json");
        await Assert.That(patch.Body).IsEqualTo(DebugContainers.Patch("debugger-abcde", "busybox:1.37", "web", ptrace: true));
    }

    /// <summary>
    /// Pod Security's baseline level refuses SYS_PTRACE, and admission refuses the request
    /// whole — so the second patch, without the capability, is the only container added.
    /// </summary>
    [Test]
    public async Task A_baseline_namespace_gets_the_container_without_ptrace()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", PodPath + "/ephemeralcontainers", HttpStatusCode.Forbidden,
            """{"kind":"Status","status":"Failure","code":403,"message":"pods \"web-0\" is forbidden: violates PodSecurity \"baseline:latest\": non-default capabilities (container \"debugger-abcde\" must not include \"SYS_PTRACE\" in securityContext.capabilities.add)"}""");
        server.Respond("PATCH", PodPath + "/ephemeralcontainers", HttpStatusCode.OK, """{"kind":"Pod"}""");
        using var client = server.Connect();

        var added = await client.AddDebugContainerAsync("shop", "web-0", "web", "busybox:1.37", name: "debugger-abcde");

        await Assert.That(added).IsEqualTo(new DebugContainerAdded("debugger-abcde", CanTrace: false));
        var patches = server.Requests.Where(r => r.Method == "PATCH").ToList();
        await Assert.That(patches.Count).IsEqualTo(2);
        await Assert.That(patches[0].Body).Contains("SYS_PTRACE");
        await Assert.That(patches[1].Body).IsEqualTo(DebugContainers.Patch("debugger-abcde", "busybox:1.37", "web"));
    }

    [Test]
    public async Task A_refused_patch_carries_the_servers_sentence()
    {
        using var server = new StubApiServer();
        server.Respond("PATCH", PodPath + "/ephemeralcontainers", HttpStatusCode.Forbidden,
            """{"kind":"Status","status":"Failure","code":403,"message":"pods \"web-0\" is forbidden: User \"dev\" cannot patch resource \"pods/ephemeralcontainers\""}""");
        using var client = server.Connect();

        var thrown = await Assert.ThrowsAsync<KubernetesApiException>(
            () => client.AddDebugContainerAsync("shop", "web-0", "web", "busybox:1.37"));
        await Assert.That(thrown!.Message).Contains("cannot patch resource \"pods/ephemeralcontainers\"");

        // An RBAC refusal is not retried without the capability: it would be refused the same way.
        await Assert.That(server.Requests.Count(r => r.Method == "PATCH")).IsEqualTo(1);
    }

    [Test]
    public async Task The_wait_ends_when_the_list_already_shows_it_running()
    {
        using var server = new StubApiServer();
        server.Respond("GET", "/api/v1/namespaces/shop/pods", HttpStatusCode.OK, """
            {"kind":"PodList","apiVersion":"v1","metadata":{"resourceVersion":"9"},"items":[
              {"metadata":{"name":"web-0","namespace":"shop","resourceVersion":"9"},
               "status":{"phase":"Running","ephemeralContainerStatuses":[
                 {"name":"debugger-abcde","state":{"running":{}}}]}}]}
            """.ReplaceLineEndings(""));
        using var client = server.Connect();

        var state = await client.WaitForDebugContainerAsync("shop", "web-0", "debugger-abcde", TimeSpan.FromSeconds(10));

        await Assert.That(state.Phase).IsEqualTo(DebugContainerPhase.Running);
        var list = server.Requests.First(r => r.Method == "GET");
        await Assert.That(Uri.UnescapeDataString(list.Query)).Contains("fieldSelector=metadata.name=web-0");
    }

    [Test]
    public async Task The_os_comes_from_the_node_label_when_the_pod_says_nothing()
    {
        using var server = new StubApiServer();
        server.Respond("GET", PodPath, HttpStatusCode.OK, """{"metadata":{"name":"web-0","namespace":"shop"},"spec":{"nodeName":"win-1"}}""");
        server.Respond("GET", "/api/v1/nodes/win-1", HttpStatusCode.OK, """{"metadata":{"name":"win-1","labels":{"kubernetes.io/os":"windows"}}}""");
        using var client = server.Connect();

        await Assert.That(await client.GetPodOperatingSystemAsync("shop", "web-0")).IsEqualTo(PodOperatingSystem.Windows);
    }

    /// <summary>A namespace-scoped user is refused <c>get nodes</c>; that is "unknown", not a failed exec.</summary>
    [Test]
    public async Task A_node_the_user_may_not_read_leaves_the_os_unknown()
    {
        using var server = new StubApiServer();
        server.Respond("GET", PodPath, HttpStatusCode.OK, """{"metadata":{"name":"web-0","namespace":"shop"},"spec":{"nodeName":"n1"}}""");
        server.Respond("GET", "/api/v1/nodes/n1", HttpStatusCode.Forbidden,
            """{"kind":"Status","status":"Failure","code":403,"message":"nodes \"n1\" is forbidden"}""");
        using var client = server.Connect();

        await Assert.That(await client.GetPodOperatingSystemAsync("shop", "web-0")).IsEqualTo(PodOperatingSystem.Unknown);
    }
}
