namespace KubeNimbus.App.Demo;

/// <summary>
/// Canned log output for the demo cluster's containers. Lines are handed to pod
/// detail one at a time on a timer so the pane behaves like a real follow — the
/// stream fills in, then goes quiet — rather than appearing fully-formed, which is
/// what an evaluator is actually trying to judge.
/// </summary>
/// <remarks>
/// Two things are deliberate and should stay that way:
/// <list type="bullet">
/// <item><b>Most lines carry no severity keyword.</b> Every line in the old fixture
/// set contained one, and that is precisely what hid the
/// <c>LogSeverityToBrushConverter</c> bug where the default case returned a local
/// <c>null</c> <c>Foreground</c> — which beats inheritance, and which Avalonia's
/// glyph-run draw early-returns on, so every plain line rendered <i>invisible</i>.
/// nginx access logs, Go <c>log.Print</c> and anything JSON are all plain, i.e. most
/// real output. If this file ever ends up all-ERROR/WARN/INFO, that regression can
/// come back unseen.</item>
/// <item>Lines carry <b>RFC3339 timestamps</b>, because <c>StreamPodLogsAsync</c>
/// always requests <c>timestamps=true</c> and the display toggle strips them —
/// timestamp-less lines would make the toggle look broken.</item>
/// </list>
/// </remarks>
internal static class DemoLogs
{
    /// <summary>How fast lines arrive. A few per second: visibly streaming, not a firehose.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(280);

    private static readonly string[] Fallback =
    [
        "2026-07-20T08:41:02.114Z Starting up.",
        "2026-07-20T08:41:02.402Z Ready.",
    ];

    /// <summary>
    /// The lines one container emits, in order. Unknown containers get a short
    /// generic stream rather than nothing — a blank log pane in the demo would read
    /// as the feature being broken, which is the opposite of the point.
    /// </summary>
    public static IReadOnlyList<string> For(string podName, string containerName) =>
        (podName, containerName) switch
        {
            // The other two replicas of payment-service-report-generator, and the reason
            // they have streams of their own rather than falling through to the shared
            // ("app") case below: the aggregated log pane's whole claim is that a rolling
            // deployment reads as one stream, and three identical streams would
            // demonstrate the merge machinery while proving nothing about the merge.
            // These interleave with x7k2m's timestamps on purpose — m4v8s is the second
            // pod of the OLD ReplicaSet and is drained by the rollout at 08:45:0x, and
            // tq6rn is the NEW ReplicaSet's first pod coming up at 08:44:5x. Read in one
            // pane, in time order, that is a rollout; read in three panes it is three
            // unrelated logs.
            ("payment-service-report-generator-7f9c8d6bcd-m4v8s", "app") =>
            [
                "2026-07-20T08:41:03.902Z INFO  starting report-generator v2.14.3 (commit 4b1e9ca, go1.23.4)",
                "2026-07-20T08:41:04.180Z INFO  listening on :8080",
                "2026-07-20T08:43:58.114Z GET /api/v1/reports/status 200 3.4ms",
                "2026-07-20T08:44:22.601Z INFO  generated monthly-settlement report for merchant=harbour-foods (611ms)",
                "2026-07-20T08:44:44.209Z uploaded reports/2026-07/harbour-foods.pdf (0.8 MiB)",
                "2026-07-20T08:45:02.004Z received SIGTERM, draining (deployment rollout)",
                "2026-07-20T08:45:02.005Z INFO  refusing new work; 2 in-flight reports remaining",
                "2026-07-20T08:45:04.771Z INFO  in-flight work finished, closing listener",
                "2026-07-20T08:45:04.930Z INFO  shutdown complete",
            ],

            ("payment-service-report-generator-8c1a4f2e91-tq6rn", "app") =>
            [
                "2026-07-20T08:44:52.118Z INFO  starting report-generator v2.15.0 (commit 9de41f7, go1.23.4)",
                "2026-07-20T08:44:52.204Z loading configuration from /etc/report-generator/config.yaml",
                "2026-07-20T08:44:52.410Z INFO  connected to postgres primary (payments-db.internal:5432)",
                "2026-07-20T08:44:52.488Z INFO  listening on :8080",
                """2026-07-20T08:44:57.330Z {"ts":"2026-07-20T08:44:57Z","msg":"scheduler tick","due":1,"lag_ms":8}""",
                "2026-07-20T08:45:00.882Z GET /api/v1/reports/status 200 2.9ms",
                "2026-07-20T08:45:05.117Z INFO  picked up 2 reports drained from a retiring replica",
                "2026-07-20T08:45:08.440Z INFO  generated monthly-settlement report for merchant=harbour-foods (588ms)",
            ],

            (_, "app") =>
            [
                "2026-07-20T08:41:02.114Z INFO  starting report-generator v2.14.3 (commit 4b1e9ca, go1.23.4)",
                "2026-07-20T08:41:02.203Z loading configuration from /etc/report-generator/config.yaml",
                "2026-07-20T08:41:02.331Z INFO  connected to postgres primary (payments-db.internal:5432)",
                "2026-07-20T08:41:02.402Z INFO  listening on :8080",
                """2026-07-20T08:41:07.640Z {"ts":"2026-07-20T08:41:07Z","msg":"scheduler tick","due":3,"lag_ms":12}""",
                "2026-07-20T08:44:17.008Z INFO  generated monthly-settlement report for merchant=acme-retail (842ms)",
                "2026-07-20T08:44:17.009Z uploaded reports/2026-07/acme-retail.pdf (1.9 MiB)",
                "2026-07-20T08:44:41.552Z GET /api/v1/reports/status 200 4.1ms",
                "2026-07-20T08:44:55.771Z WARN  slow query detected: SELECT * FROM settlements WHERE ... (1204ms)",
                "2026-07-20T08:44:58.019Z ERROR failed to upload report to blob storage: connection reset by peer",
                "2026-07-20T08:44:58.020Z retrying upload in 5s (attempt 2 of 5)",
                "2026-07-20T08:45:01.220Z INFO  generated chargeback-summary report for merchant=north-store (391ms)",
                "2026-07-20T08:45:03.118Z upload succeeded after 2 attempts",
                """2026-07-20T08:45:09.771Z {"ts":"2026-07-20T08:45:09Z","msg":"scheduler tick","due":0,"lag_ms":3}""",
            ],

            (_, "envoy-sidecar") =>
            [
                "2026-07-20T08:41:01.004Z [2026-07-20 08:41:01.004][1][info][main] starting main dispatch loop",
                "2026-07-20T08:41:01.219Z [2026-07-20 08:41:01.219][1][info][upstream] cds: add 4 cluster(s), remove 0 cluster(s)",
                "2026-07-20T08:41:01.402Z [2026-07-20 08:41:01.402][1][info][config] all clusters initialized",
                """2026-07-20T08:44:17.011Z [2026-07-20T08:44:17Z] "POST /v1/blob HTTP/1.1" 200 - 1948231 34 12 "-" "report-generator/2.14.3" """,
                """2026-07-20T08:44:58.018Z [2026-07-20T08:44:58Z] "POST /v1/blob HTTP/1.1" 0 UC 0 0 91 "-" "report-generator/2.14.3" """,
                "2026-07-20T08:44:58.018Z [2026-07-20 08:44:58.018][22][warning][upstream] upstream reset: reset reason: connection termination",
                """2026-07-20T08:45:03.117Z [2026-07-20T08:45:03Z] "POST /v1/blob HTTP/1.1" 200 - 1948231 34 9 "-" "report-generator/2.14.3" """,
            ],

            (_, "worker") =>
            [
                "2026-07-20T08:39:11.001Z checkout-worker starting, queue=checkout-events group=workers-a",
                "2026-07-20T08:39:11.310Z subscribed to 12 partitions",
                "2026-07-20T08:44:02.771Z processed order 8812-4471 in 61ms",
                "2026-07-20T08:44:04.118Z processed order 8812-4472 in 44ms",
                "2026-07-20T08:44:09.902Z WARN  payment gateway latency above threshold (912ms, budget 500ms)",
                "2026-07-20T08:44:12.330Z processed order 8812-4473 in 58ms",
                "2026-07-20T08:44:31.007Z committed offsets for 12 partitions",
            ],

            (_, "redis") =>
            [
                "2026-07-20T08:12:44.019Z 1:C 20 Jul 2026 08:12:44.019 * oO0OoO0OoO0Oo Redis is starting oO0OoO0OoO0Oo",
                "2026-07-20T08:12:44.020Z 1:M 20 Jul 2026 08:12:44.020 * monotonic clock: POSIX clock_gettime",
                "2026-07-20T08:12:44.022Z 1:M 20 Jul 2026 08:12:44.022 * Running mode=standalone, port=6379.",
                "2026-07-20T08:12:44.041Z 1:M 20 Jul 2026 08:12:44.041 * Ready to accept connections tcp",
                "2026-07-20T08:42:44.118Z 1:M 20 Jul 2026 08:42:44.118 * 1 changes in 3600 seconds. Saving...",
                "2026-07-20T08:42:44.140Z 1:M 20 Jul 2026 08:42:44.140 * Background saving terminated with success",
            ],

            (_, "dispatcher") =>
            [
                "2026-07-20T08:40:00.114Z dispatcher up, providers=[email sms push]",
                "2026-07-20T08:44:18.220Z sent 41 notifications (email=30 sms=7 push=4)",
                "2026-07-20T08:44:38.771Z ERROR sms provider returned 429 Too Many Requests; backing off 30s",
                "2026-07-20T08:45:08.902Z sms provider recovered",
                "2026-07-20T08:45:19.004Z sent 17 notifications (email=14 sms=3 push=0)",
            ],

            (_, "migrate") =>
            [
                "2026-07-20T07:58:00.004Z migrate: reading migrations from /migrations",
                "2026-07-20T07:58:00.119Z migrate: current version 214, 3 pending",
                "2026-07-20T07:58:00.902Z migrate: applied 215_add_settlement_index.sql (783ms)",
                "2026-07-20T07:58:01.440Z migrate: applied 216_backfill_merchant_region.sql (538ms)",
                "2026-07-20T07:58:01.601Z migrate: applied 217_drop_legacy_reports.sql (161ms)",
                "2026-07-20T07:58:01.604Z migrate: now at version 217",
            ],

            // Pending pod: the image is still being pulled, so the container has never
            // run and the API server has nothing to give. That is a real state and the
            // log pane already has words for it — so this one is deliberately empty.
            (_, "model-server") => [],

            _ => Fallback,
        };

    /// <summary>
    /// The "previous instance" stream — what Previous shows. Only the containers that
    /// have actually restarted have one; everything else gets the same refusal the API
    /// server gives, which is the state that button needs to be honest about.
    /// </summary>
    public static IReadOnlyList<string>? Previous(string podName, string containerName) =>
        containerName switch
        {
            "dispatcher" =>
            [
                "2026-07-20T08:39:41.002Z dispatcher up, providers=[email sms push]",
                "2026-07-20T08:39:48.114Z sent 9 notifications (email=9 sms=0 push=0)",
                "2026-07-20T08:39:52.771Z ERROR sms provider returned 500; 4 consecutive failures",
                "2026-07-20T08:39:53.001Z panic: notifications: provider pool exhausted",
                "2026-07-20T08:39:53.002Z goroutine 1 [running]:",
                "2026-07-20T08:39:53.002Z main.(*Dispatcher).drain(0xc000112000)",
                "2026-07-20T08:39:53.003Z \t/src/dispatcher/drain.go:88 +0x1f4",
                "2026-07-20T08:39:53.010Z exit status 2",
            ],
            _ => null,
        };
}
