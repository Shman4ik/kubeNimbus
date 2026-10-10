# Port-forwards: the window's registry, and forwarding a Service

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

Code: `KubeNimbus.Core/ClusterClient.PortForward.cs` (`PortForwardSession`, the pod and the
Service forward), `KubeNimbus.Core/Networking/ServiceForwards.cs` (which pod a Service port
reaches), and in the App `PortForwardTabViewModel` (the pane, which *is* the forward),
`PortForwardRegistry` (every running forward in the window), `PortForwardsTabViewModel`
(the list) and `ClusterTabViewModel.PortForwards.cs` (the cluster tab's half).

## A forward outlives its tab (FEAT-7)

A forward used to stop when its dock tab closed, and nothing listed what was still
listening on the machine. Now:

1. **The window owns a `PortForwardRegistry`, nothing on disk.** `MainWindowViewModel`
   creates it and stamps it on every cluster tab as it enters the strip
   (`ClusterTabViewModel.PortForwards`). A forward is a socket in this process, so it ends
   with the app; a list of last session's forwards would be a history of sockets that no
   longer exist.
2. **The pane's view model is the forward.** A running pane is in the registry from Start to
   Stop. Closing its dock tab leaves it running (`OnClosingAsync` returns early while a
   registry holds it), and reopening it re-adds the same view model to the dock of the tab
   that started it, so it shows its live state. A pane built with no registry (a fixture)
   stops on close as it always did, since nothing would list it.
3. **Closing a tab keeps the forward; that is said before and after.** Before: the running
   pane says "Closing this tab keeps it forwarding — the status bar counts it, and Stop ends
   it." After: the status bar shows "N port-forwards" for as long as one runs, and its click
   opens the list. Stopping is not confirmed anywhere: it is undone by Start (UI rule 17).
   Asking on close was the alternative and was rejected for the same reason.
4. **The status bar is open while a forward runs** (`PortForwards.HasSomethingToShow`, OR-ed
   with the tab's own `IsStatusWorthShowing`), and the tab's routine "Connected — Kubernetes
   v…" line stays hidden then — it is gated on its own worth, not on the bar being open.
5. **Closing a cluster tab stops its forwards first** (`StopForClusterAsync`, before the
   client is disposed): the ones it started and any that tunnel through its client. A forward
   left on a disposed client would accept connections and fail every one. The status bar
   then says "Stopped N port-forwards on <cluster> — its tab was closed." for 15 seconds, or
   until the list is opened or another forward starts or stops — beside the count when other
   clusters' forwards still run ("1 port-forward · Stopped …"), and in the tooltip. A Start still
   in flight (a Service forward resolving over the network) is cancelled through its token and
   never binds or registers on the disposed client.
6. **The list** (palette "Port-forwards" while one runs, or the status-bar count) shows each
   forward's local address, what it reaches, its cluster and its last refused connection,
   with Open and Stop. It opens in the selected tab's dock and lists every cluster's forwards.

`PortForwardRegistryTests` binds real loopback listeners: a closed pane still accepts a TCP
connection, a stopped one refuses it, and the shell's tab close stops what ran on it.

## Forwarding a Service (FEAT-29)

The API has a port-forward endpoint for a pod only. `kubectl port-forward svc/x` picks a pod
on the client; so does this, and the pane names the pod it picked.

- **The pod comes from the EndpointSlices, not the selector.** A ready, non-terminating
  endpoint that names a pod, found by the `kubernetes.io/service-name` label as the Service
  pane finds them — so a forward goes where the Service would send a request, never to a
  crash-looping pod the selector also matches, and a selector-less service with hand-written
  pod endpoints works too.
- **The pod port is read from the slice**, under the service port's name. A named
  `targetPort` resolves per pod; the EndpointSlice controller has already written the number.
- **Deterministic and sticky:** candidates by pod name, and the previous pick is kept while it
  is still ready.
- **Resolved before binding.** A service with no ready pod refuses to start, with one of seven
  sentences (`ServiceForwardsTests` pins each): deleted, ExternalName, no such port, UDP, no
  endpoints, none ready, endpoints that name no pod.
- **When the pod goes, the next connection moves and says so.** Any failed connection makes
  the next one resolve again; a websocket that cannot be opened at all (the pod is gone) is
  re-resolved at once and retried on the new pick, since nothing was sent. `TargetChanged`
  reaches the pane, which names the new pod and why it moved. It never forwards silently to
  nothing: with no ready pod left, each connection fails with the resolution's sentence.

It is started from the Service pane's **Port-forward** button and from the palette with a
Service row selected. `ServicePortForwardLiveTests` runs it against the sandbox: busybox
`httpd` pods that serve their own name, a named target port, the pod deleted mid-forward,
and the next answer coming from its replacement with the move announced. With the re-resolve
removed that test times out, which is how it was checked.

## Accessible names

Start and Stop are named "Start forwarding" and "Stop forwarding" by hand (#271): on a real
Windows build the Stop button read as `<unnamed>` although its headless peer said "Stop".
`AutomationChecks.RequiredButtonNames` asserts those exact names in the port-forward
scenarios, idle and running, and the list's.
