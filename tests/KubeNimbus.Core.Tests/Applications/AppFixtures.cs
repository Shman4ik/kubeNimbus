using System.Text.Json;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.Core.Tests.Applications;

/// <summary>
/// Small, hand-shaped Kubernetes objects for the Applications rules tests. Each builder
/// takes the few fields a rule reads and nothing more, so a test reads as the state it
/// pins; <see cref="R"/> is the escape hatch for a shape no builder covers.
/// </summary>
/// <remarks>
/// Templates use <c>%name%</c> placeholders rather than interpolated raw strings: JSON's
/// runs of closing braces and C#'s interpolation braces do not mix legibly. Values passed
/// through <see cref="Q"/> are JSON-escaped, so a message may carry quotes.
/// </remarks>
internal static class AppFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 7, 30, 8, 56, 0, TimeSpan.Zero);

    public static DynamicResource R(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    public static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    public static string Ago(int minutes) => Iso(Now.AddMinutes(-minutes));

    /// <summary>A JSON string literal, quotes included.</summary>
    public static string Q(string value) => JsonSerializer.Serialize(value, JsonStringContext.Default.String);

    /// <summary>Fills <c>%key%</c> placeholders.</summary>
    public static string T(string template, params (string Key, object Value)[] values)
    {
        foreach (var (key, value) in values)
        {
            template = template.Replace($"%{key}%", value switch
            {
                bool b => b ? "true" : "false",
                _ => value.ToString(),
            });
        }

        return template;
    }

    public static DynamicResource Deployment(
        string name, int replicas = 2, int ready = 2, int updated = -1, int total = -1,
        string conditions = "[]", string ns = "shop", string labels = "{}", string annotations = "{}",
        bool paused = false) =>
        R(T("""
            {"apiVersion":"apps/v1","kind":"Deployment",
             "metadata":{"name":"%name%","namespace":"%ns%","uid":"uid-%name%","generation":3,"labels":%labels%,"annotations":%annotations%},
             "spec":{"replicas":%replicas%,"paused":%paused%,"selector":{"matchLabels":{"app":"%name%"}},
                     "template":{"metadata":{"labels":{"app":"%name%"}},"spec":{"containers":[{"name":"app","image":"%name%:1"}]}}},
             "status":{"observedGeneration":3,"replicas":%total%,"readyReplicas":%ready%,"updatedReplicas":%updated%,"conditions":%conditions%}}
            """,
            ("name", name), ("ns", ns), ("labels", labels), ("annotations", annotations), ("replicas", replicas),
            ("paused", paused), ("total", total < 0 ? replicas : total), ("ready", ready),
            ("updated", updated < 0 ? replicas : updated), ("conditions", conditions)));

    public static DynamicResource ReplicaSet(
        string deployment, string hash, long revision, int ready, string created, string ns = "shop", string template = "") =>
        R(T("""
            {"apiVersion":"apps/v1","kind":"ReplicaSet",
             "metadata":{"name":"%deployment%-%hash%","namespace":"%ns%","creationTimestamp":"%created%",
                         "annotations":{"deployment.kubernetes.io/revision":"%revision%"},
                         "ownerReferences":[{"apiVersion":"apps/v1","kind":"Deployment","name":"%deployment%","uid":"uid-%deployment%","controller":true}]},
             "spec":{"template":%template%},
             "status":{"readyReplicas":%ready%}}
            """,
            ("deployment", deployment), ("hash", hash), ("ns", ns), ("created", created), ("revision", revision),
            ("ready", ready),
            ("template", template.Length > 0 ? template
                : T("""{"spec":{"containers":[{"name":"app","image":"%d%:%r%"}]}}""", ("d", deployment), ("r", revision)))));

    /// <summary>A pod of <paramref name="app"/>; <paramref name="containerStatus"/> is one containerStatuses entry.</summary>
    public static DynamicResource Pod(
        string name, string app, string phase = "Running", bool ready = true, string containerStatus = "",
        string conditions = "", string ns = "shop", string spec = "", string owner = "", string deletion = "") =>
        R(T("""
            {"apiVersion":"v1","kind":"Pod",
             "metadata":{"name":"%name%","namespace":"%ns%","labels":{"app":"%app%"},"creationTimestamp":"%created%"%owner%%deletion%},
             "spec":%spec%,
             "status":{"phase":"%phase%","conditions":%conditions%,"containerStatuses":[%status%]}}
            """,
            ("name", name), ("ns", ns), ("app", app), ("created", Ago(30)),
            ("owner", owner.Length > 0 ? $",\"ownerReferences\":[{owner}]" : ""),
            ("deletion", deletion.Length > 0 ? $",\"deletionTimestamp\":\"{deletion}\"" : ""),
            ("spec", spec.Length > 0 ? spec : """{"containers":[{"name":"app","image":"app:1"}]}"""),
            ("phase", phase),
            ("conditions", conditions.Length > 0 ? conditions
                : T("""[{"type":"PodScheduled","status":"True"},{"type":"Ready","status":"%r%","lastTransitionTime":"%t%"}]""",
                    ("r", ready ? "True" : "False"), ("t", Ago(20)))),
            ("status", containerStatus.Length > 0 ? containerStatus : Running(ready: ready))));

    public static string Running(string name = "app", bool ready = true, int restarts = 0, string last = "") =>
        T("""{"name":"%name%","ready":%ready%,"restartCount":%restarts%,"state":{"running":{"startedAt":"%t%"}}%last%}""",
            ("name", name), ("ready", ready), ("restarts", restarts), ("t", Ago(25)), ("last", LastState(last)));

    public static string Waiting(string reason, string message = "", string name = "app", int restarts = 0, string last = "") =>
        T("""{"name":"%name%","ready":false,"restartCount":%restarts%,"state":{"waiting":{"reason":"%reason%","message":%message%}}%last%}""",
            ("name", name), ("restarts", restarts), ("reason", reason), ("message", Q(message)), ("last", LastState(last)));

    private static string LastState(string last) => last.Length > 0 ? $",\"lastState\":{{\"terminated\":{last}}}" : "";

    public static string Terminated(int exitCode, string reason, int minutesAgo, string id = "containerd://abc") =>
        T("""{"exitCode":%code%,"reason":"%reason%","startedAt":"%s%","finishedAt":"%f%","containerID":"%id%"}""",
            ("code", exitCode), ("reason", reason), ("s", Ago(minutesAgo + 1)), ("f", Ago(minutesAgo)), ("id", id));

    public static DynamicResource Event(string reason, string kind, string name, string message, int count = 1,
        int minutesAgo = 2, string type = "Warning", string ns = "shop") =>
        R(T("""
            {"apiVersion":"v1","kind":"Event","type":"%type%","reason":"%reason%","message":%message%,"count":%count%,
             "lastTimestamp":"%t%",
             "metadata":{"name":"%name%.%reason%","namespace":"%ns%"},
             "involvedObject":{"kind":"%kind%","name":"%name%","namespace":"%ns%"}}
            """,
            ("type", type), ("reason", reason), ("message", Q(message)), ("count", count), ("t", Ago(minutesAgo)),
            ("name", name), ("ns", ns), ("kind", kind)));

    public static ArgoApplication Argo(
        string name, string sync = "Synced", string health = "Healthy", string resources = "[]",
        string operation = "", string conditions = "[]", string history = "[]", string source = "",
        bool automated = true, string destinationNamespace = "shop") =>
        ArgoCd.ReadApplication(R(T("""
            {"apiVersion":"argoproj.io/v1alpha1","kind":"Application",
             "metadata":{"name":"%name%","namespace":"argocd"},
             "spec":{"source":%source%,
                     "destination":{"server":"https://kubernetes.default.svc","namespace":"%dest%"},
                     "syncPolicy":%policy%},
             "status":{"sync":{"status":"%sync%","revision":"8f3c1d94b27ae5106f4e2c0b9a7d3e51c8b6042f"},"health":{"status":"%health%"},
                       %operation%"conditions":%conditions%,"resources":%resources%,"history":%history%}}
            """,
            ("name", name), ("dest", destinationNamespace), ("sync", sync), ("health", health),
            ("source", source.Length > 0 ? source : """{"repoURL":"https://github.com/acme/deploy.git","path":"apps/x","targetRevision":"main"}"""),
            ("policy", automated ? """{"automated":{"selfHeal":true}}""" : "{}"),
            ("operation", operation.Length > 0 ? $"\"operationState\":{operation}," : ""),
            ("conditions", conditions), ("resources", resources), ("history", history))));

    public static ApplicationInput Input(
        IReadOnlyList<DynamicResource>? workloads = null,
        IReadOnlyList<DynamicResource>? pods = null,
        IReadOnlyList<DynamicResource>? replicaSets = null,
        IReadOnlyList<DynamicResource>? jobs = null,
        IReadOnlyList<DynamicResource>? events = null,
        ArgoApplication? argo = null) =>
        new(argo, workloads ?? [], replicaSets ?? [], jobs ?? [], pods ?? [], events ?? [], Now);

    public static ApplicationAssessment Evaluate(
        IReadOnlyList<DynamicResource>? workloads = null,
        IReadOnlyList<DynamicResource>? pods = null,
        IReadOnlyList<DynamicResource>? replicaSets = null,
        IReadOnlyList<DynamicResource>? jobs = null,
        IReadOnlyList<DynamicResource>? events = null,
        ArgoApplication? argo = null) =>
        ApplicationRules.Evaluate(Input(workloads, pods, replicaSets, jobs, events, argo));
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class JsonStringContext : System.Text.Json.Serialization.JsonSerializerContext;
