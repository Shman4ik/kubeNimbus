# Startup performance: the connect path, its trace, and HTTP/2

What a user waits for between starting the app and seeing rows, how to measure it, and why
every request asks for HTTP/2 (2026-11-08).

## What a launch waits for

From process start to the first rows, in order. Each line is a phase `KUBENIMBUS_STARTUP_TRACE`
writes (see below), and the file and member that runs it.

1. `main`: `Program.Main`. Runtime start-up before it is included in the time.
2. `kubeconfig read`: `MainWindowViewModel.InitializeAsync` has read the kubeconfig chain.
   No network.
3. `window opened`: the main window's `Opened`. The connect below has already started; it
   does not wait for the window.
4. `connect`, then `credentials`: `ClusterTabViewModel.ConnectAsync` builds the client.
   This reads the kubeconfig again, loads the CA and runs the exec plugin once (#283). On
   EKS, `aws eks get-token` alone is often most of a second.
5. `version`: `GET /version`, alone. It is the reachability check: a cluster that cannot be
   reached fails here once, with one report. It no longer runs the exec plugin. The comment
   that said so was stale from #283 until this change.
6. Then, together: discovery (`catalog`, written as `discovery cache` or `live discovery`),
   the namespace list (`namespaces`) and the metrics probe, which reads the catalog.
7. `sidebar`: discovery, then the Helm probe (`secrets?limit=1&fieldSelector=type=…`), one
   after the other.
8. `resources list`: the first list of the Resources mode. A restored Pods tab, or a tab with
   nothing restored, starts it as soon as the namespace list has answered, because the
   restored namespace is checked against that list (`ApplyInitialView`). Any other restored
   kind waits for the sidebar.
9. `applications rows` and `applications read`: the Applications mode, the default first
   screen. `ApplicationsViewModel.StartAsync` waits for discovery, then opens seven
   cluster-wide watches: Deployment, StatefulSet, DaemonSet, CronJob, Job, ReplicaSet and
   Pod. It adds Argo CD's `Application` when discovery lists it. The first phase is the
   first rows on screen; the second is every watch listed.

The discovery cache (`DiscoveryCache`, six hours, keyed by server, context, user and
kubeconfig path, checked against the server version) makes step 6 a file read. It is empty
on a first launch, and it has expired by the first launch of a working day.

## The trace: `KUBENIMBUS_STARTUP_TRACE`

`StartupTrace` (Core) writes one line per phase: milliseconds since the process started,
the phase, the subject (a context name) and sometimes a detail, separated by tabs.

```text
KUBENIMBUS_STARTUP_TRACE=C:\tmp\trace.tsv   append to that file
KUBENIMBUS_STARTUP_TRACE=stderr             write to standard error
```

Each phase is written once per subject, the first time it is reached, so the file is the
launch's timeline. A later kind switch or reconnect adds nothing to it. Unset, which is
every ordinary launch, each mark returns at its first check. A file that cannot be opened
turns the trace off rather than failing the launch. It is the one file the app writes
outside its own folders, and only where the variable says. `PRIVACY.md` says so.

Taking a trace means launching the app. Under `CLAUDE.md` that happens in the owner's QA VM
or on a user's own machine, never on the owner's desktop from a session. Ask a user who
reports a slow start for one.

## The benchmark: `ConnectLatencyLiveTests`

`tests/KubeNimbus.Core.Tests/Live/ConnectLatencyLiveTests.cs` runs the connect path's
requests in the app's order. It goes through `DelayingTcpRelay`, a loopback TCP relay to the
sandbox that adds half a round trip in each direction and a whole one per new connection,
standing in for the TCP handshake. The relay passes TLS through without looking at it, so
the client and k3s negotiate HTTP/2 or HTTP/1.1 between themselves as they would across a
network, and the relay counts the connections.

- `A_connect_travels_on_one_connection_under_http2` runs with every live run. It asserts one
  TCP connection for a whole connect under HTTP/2, the seven watches included, and at least
  seven under HTTP/1.1.
- `Connect_phases_by_round_trip_time` is `[Explicit]` and asserts nothing. It prints each
  phase's median over five cold connects, with no discovery cache, for round trips of 0, 50
  and 150 ms and both HTTP versions. Run it by its full name, because a wildcard filter skips
  an explicit test:

```bash
tests/KubeNimbus.Core.Tests/bin/Debug/net10.0/KubeNimbus.Core.Tests.exe --treenode-filter "/*/*/ConnectLatencyLiveTests/Connect_phases_by_round_trip_time" --output Detailed
```

Limits of the model:

- It models delay, not bandwidth.
- On Windows, `Task.Delay` rounds up to the 15.6 ms timer tick, which adds noise. The noise
  is the same for both versions.
- The sandbox is small: 47 pods, whose list is 290 KB of JSON.
- `credentials` is a client certificate here, with no exec plugin.

## The numbers (2026-11-08, k3s v1.33.4, Windows 11)

Milliseconds from before the client is built. Medians of five cold connects.

| RTT | HTTP | connections | credentials | version | namespaces | catalog | sidebar | resources list | applications read |
|---|---|---|---|---|---|---|---|---|---|
| 0 ms | 1.1 | 9 | 39 | 91 | 99 | 96 | 105 | 179 | 166 |
| 0 ms | 2 | 1 | 34 | 62 | 68 | 68 | 83 | 106 | 115 |
| 50 ms | 1.1 | 9 | 36 | 261 | 445 | 378 | 484 | 628 | 619 |
| 50 ms | 2 | 1 | 36 | 236 | 330 | 327 | 397 | 421 | 422 |
| 150 ms | 1.1 | 9 | 37 | 546 | 1039 | 871 | 1070 | 1417 | 1418 |
| 150 ms | 2 | 1 | 31 | 536 | 731 | 731 | 899 | 918 | 917 |

HTTP/2 reads the Applications list about a third sooner at both distances: 1418 to 917 ms at
150 ms, and 619 to 422 ms at 50 ms. The HTTP/2 rows are with the 4 MiB stream window
described below. Before it, the 150 ms row read `resources list` 1109 and
`applications read` 1109.

## Why every request asks for HTTP/2 (`ApiServerHttpVersion`)

kubectl and client-go use HTTP/2. kubeNimbus did not, without anyone deciding so.

- **How it was HTTP/1.1.** Every request is built by `ClusterClient.SendRequestAsync`, and a
  hand-built `HttpRequestMessage` is HTTP/1.1 unless it says otherwise.
  `HttpClient.DefaultRequestVersion` does not apply to one built by hand.
  `KubernetesClient.Aot` sets 2.0 only on its own typed calls, which kubeNimbus does not use.
- **What that cost.** Under HTTP/1.1 each request in flight needs its own connection, so a
  connect opened nine. Each paid a TCP and a TLS handshake, two round trips before its
  request went out. Each open watch then held its connection for good.
- **Version negotiation.** The request version is 2.0 with `RequestVersionOrLower`, chosen
  through TLS ALPN. A server or TLS-terminating proxy that offers only HTTP/1.1 gets
  HTTP/1.1 (`A_server_that_offers_only_http11_is_answered_in_http11`), as does a plain
  `http://` server.
- **`DISABLE_HTTP2`.** Any value asks for HTTP/1.1 everywhere. It is the variable client-go
  reads, so one setting covers kubectl and kubeNimbus for a user whose proxy mishandles
  HTTP/2. It is read when a client is built.
- **Pings, 30 s then 15 s.** One shared connection means a connection that dies silently (an
  expired NAT entry, a VPN changing networks) would stall every watch at once until TCP
  gives up, which can take hours. With requests open, the handler pings after 30 s without a
  frame and drops the connection when no answer comes within 15 s. Every watch then fails
  and reconnects through its usual path, which reports the loss (hard rule 2). These are
  client-go's `ReadIdleTimeout` and `PingTimeout`. HTTP/1.1 had no such check.
- **`EnableMultipleHttp2Connections`.** A server caps the streams on one connection; the Go
  HTTP/2 server's default is 250. Without this setting, a request past the cap would wait
  for a stream to end, and a watch never ends.
- **A 4 MiB stream window.** HTTP/2 lets a server send only as much of a response as the
  client's window for that stream allows. .NET starts it at 64 KiB and grows it with the
  measured round trip, so the 290 KB pod list waited for several window updates, a round
  trip each. That left HTTP/2's first pod list at 150 ms barely ahead of HTTP/1.1.
  `StreamWindow` is the window client-go's transport opens with
  (`transportDefaultStreamFlow`). It caps unread data per stream; it is not memory taken up
  front.

Exec and port-forward open WebSockets through `ApiServerWebSocketBuilder`, an HTTP/1.1
upgrade that this change does not touch.

## What the numbers say next

With HTTP/2 at 150 ms, the critical path is `/version` (about 3.5 round trips with the
handshakes), then the steps below that still run one after another. Each removes a round
trip or more from the path. None of them is built yet, and each needs the trace from a real
launch before and after:

- The Applications mode waits for discovery only for Argo CD. Its seven kinds have stable
  versions, and `WellKnown` already covers a failed discovery. Starting them after
  `/version` saves the catalog's round trip on a cold cache. On a server without aggregated
  discovery, it saves the whole legacy fan-out.
- The Helm probe needs no catalog. Run beside discovery, it saves the sidebar one round trip.
- A restored namespace could start its pod list before the namespace list answers, and move
  to All namespaces if the list shows the namespace gone.
- The discovery cache could serve an entry of any age at once and refresh it behind the
  sidebar, instead of expiring after six hours.
- The namespace list could go out with `/version`. Refresh is already coalesced
  (`RefreshCoalesceWindow`), so several 401s cost one plugin run. The order exists for the
  single failure report, which the fan-out would have to keep.
