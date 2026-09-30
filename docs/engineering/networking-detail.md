# Networking: Service, Ingress and NetworkPolicy panes, and the list columns

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

Until this bundle every networking kind rendered through the generic list and the YAML
editor and nothing else: a Service was a manifest, a NetworkPolicy list was a name and an
age (less than `kubectl get netpol`), and HTTPRoute's one column was blank. The question
this surface answers is the one people open a Kubernetes client with in a hurry — *"why is
traffic not reaching my pods?"* — and it is answered by what a Service selects and routes
to, not by its YAML. Six backlog rows landed together (FEAT-59 with FEAT-46 folded in,
FEAT-60, 61, 62, 63, 64).

Code: `KubeNimbus.Core/Networking/` (`ServiceBackends`, `IngressRules`,
`NetworkPolicyRules` — pure functions over JSON, tested with no cluster) and the three
panes `ServiceDetailTabViewModel`, `IngressDetailTabViewModel`,
`NetworkPolicyDetailTabViewModel` with their views. Double-click routes to them from
`ClusterTabViewModel.Networking.cs`, matched on group **and** kind so a CRD that happens to
be called `Service` keeps its YAML. The YAML stays one context-menu item (E) away, as it is
for a pod.

## The Service pane (FEAT-59, FEAT-46)

One list, not two side by side: every pod the selector matches **joined** to the
EndpointSlice endpoints that carry it, then every endpoint no matched pod accounts for. The
finding the pane exists for is a pod that matches but does not serve, and a join states it
in one row ("CrashLoopBackOff · Not ready · listed but not ready — its readiness probe is
failing") where two lists would leave the reader to diff them. Above the rows sits one
verdict (`ServiceBackends.Verdict`), and the three degenerate shapes are three different
sentences because each one's empty list means something different:

| Shape | What the pane says | Why it is its own state |
| --- | --- | --- |
| Selector matches no pod | error: "The selector matches no pod" + the selector | the most common Service bug there is — a typo in a label |
| No selector | the endpoints someone else wrote are the whole truth, or none and "traffic goes nowhere" | Kubernetes does not manage its endpoints, so an empty list is not a controller lag |
| ExternalName | neutral: "ExternalName → *name*", a CNAME and nothing to be ready | no pods and no endpoints are *expected* |

Eight things are load-bearing:

1. **EndpointSlices are found by the `kubernetes.io/service-name` label, not by owner
   reference.** The backlog row suggested owner references, as FreeLens does; the label is
   the contract. It is what kube-proxy reads to program a service, it is on the slices the
   controller writes, on the ones mirrored from a legacy Endpoints object, and on the ones a
   person or an operator writes for a selector-less service — which carry no owner
   reference at all, so matching by owner would show such a service as having no endpoints
   while kube-proxy routes to them. It is also a server-side `labelSelector`, so the watch
   is exactly this service's slices. `ServiceBackendsTests.Sandbox_service_backends_join_on_a_real_cluster`
   checks it against the sandbox's `demo-shop/shop-api`.
2. **Two label-selected watches, not a one-shot read.** Node detail's pod list is a snapshot
   because a node's pods move slowly; a service's endpoints are what changes *while someone
   is looking* — a rollout, a probe flipping, a crash loop — and "is it serving now" is the
   question. Both watches end with the pane, and both are the watch engine's own
   (`WatchResourceAsync` with a `LabelSelector`), so reconnect and 410 relist behave as the
   list's do.
3. **No verdict before both lists have synced (UI rule 18).** "The selector matches no pod"
   said while the pod list is still in flight is exactly the premature verdict that rule
   forbids. A `Reset` of either watch puts the pane back to "Reading…" with an indeterminate
   bar; only `Synced` ends it; a watch that throws ends it too, with the server's sentence as
   the error. `NetworkingDetailTests.No_verdict_is_given_between_a_reset_and_its_sync` pins
   the order, and was confirmed red with the gate removed. The same gate keeps the initial
   list linear: objects that arrive before both lists have synced go into the stores without
   re-joining, and the join runs on `Reset` and on `Synced`. Re-joining per object had made
   every new pod change the grid's key sequence and clear and refill it — about 45,000
   notifications for a 300-pod service — and the stress mode's `service-backends` check
   (1,000 pods) is what holds it now.
4. **Endpoint conditions take the API's defaults.** `ready` unset is true, `serving` unset
   defers to `ready`, `terminating` unset is false — a hand-written slice often sets none.
   Terminating splits on serving: *Draining* (still taking its existing connections) is not
   *Terminating*.
5. **A matched pod with no endpoint says which side of the window it is on**: not scheduled,
   no IP yet, finished (a Succeeded pod is never an endpoint), being deleted, or "the
   controller may still be catching up". That sentence is the diagnosis.
6. **Rows are ordered by name, never by state**, and a watch tick updates a row in place by
   key, so the selected row stays selected. A list that re-sorted every time a probe flipped
   could not be read.
7. **An edited selector restarts the pod watch.** The pane tracks the list's own row, like
   node detail, and re-derives the selector on every change to the Service object.
8. **A dual-stack pod is one row with two addresses**, not two backends.

The backends list has L3's logs gestures (L, Shift+L, the row icon, "Logs" in the menu) on
rows that name a pod, and Enter / double-click opens the pod — see
[row-logs-and-maximized](row-logs-and-maximized.md). The Overview tab carries addressing
(cluster IPs, load balancer, external IPs), the selector, the non-default traffic settings
and port → target; defaults are left out rather than printed as noise.

## The Ingress pane (FEAT-63)

Every path as a row: host, path (with its `pathType` as the tooltip), backend, TLS state,
and the URL with Open and Copy. The backend's chevron opens that Service's own pane, so
"the site is down" walks Ingress → Service → the pods in three clicks.

**This is the first place a cluster-controlled string reaches the operating system's "open
this", so the URL is built, never copied** (`IngressRules.BuildUrl`). The host must be a
DNS-1123 name (lower-case labels of letters, digits and hyphens — what the API server
validates an Ingress host against); the path goes in only when it is plain (letters,
digits, `-._~/` and well-formed `%XX`); and the result is parsed back and checked to be
http or https on exactly that host, with no port and no user info. Anything else — a
wildcard, an empty host, `evil@good`, `file:`, a regex path — has no URL: the row says why
("wildcard host — no single URL") in the URL column and Open / Copy are *disabled*, not
hidden, so the reader can see there is nothing to open. A regex path (ingress-nginx's
`/api(/|$)(.*)`) links to the host's root rather than to a URL that would 404.
`IngressRulesTests.A_host_that_is_not_a_hostname_gets_no_url` pins the refusals.

**https exactly when the host is in `spec.tls[].hosts`, compared literally.** A wildcard
TLS entry is not expanded over a concrete rule host: whether the controller serves that
host with the wildcard certificate is its business, and guessing https for a host served
plain would be the one wrong link on the pane. A TLS host no rule serves gets a warning bar
— the two places a host has to be spelled disagree.

The routes follow the list's own watch of the Ingress; the pane's only request is Events.

## The NetworkPolicy pane (FEAT-64)

The rules as rules — one card per direction, each opening with the sentence the YAML hides:

- a direction in `policyTypes` with **no rules denies** everything that way;
- a rule with **no peers allows anyone**, and one with **no ports allows every port**;
- a direction **not** in `policyTypes` is not restricted by this policy, and its rules (if
  any) are ignored;
- missing `policyTypes` is read the way the API server defaults it (Ingress always, Egress
  when there are egress rules), and the pane says it was derived.

Peers read as phrases ("pods with app=traefik in namespace ingress-nginx", "IP block
10.0.0.0/8 except 10.43.0.0/16"); `kubernetes.io/metadata.name in (x)` reads as "namespace
x", because that label is how a namespace is named in a peer.

**The empty selector is the trap, and it is the opposite of `LabelSelector`'s rule.**
`LabelSelector.Parse` returns null for an empty selector on purpose — for a workload's
logs, "every pod" is a failure (aptakube#227). For a NetworkPolicy, `podSelector: {}` means
every pod in the namespace; it is how "default deny" is written. So `NetworkPolicyRules`
reads emptiness itself and never lets that null stand for it, and the Pods tab of a default
deny lists the whole namespace. It also refuses a selector it cannot evaluate faithfully —
an unknown operator, an `In` with no values — by **counting**: every `matchLabels` and
`matchExpressions` entry must come back as a requirement, because `LabelSelector.Parse`
skips an entry it does not understand, and a skipped requirement is a *wider* selector. An
unreadable selector lists no pods and says so.

The Pods tab is one capped read with Refresh (500, stated when truncated), like node
detail's: which pods a policy selects changes at the speed pods are created, and an empty
selector is the whole namespace. Not a graph — the feature competitors actually ship, and
the one users file for, is the list of matched pods.

## The list columns (FEAT-60)

`ResourceStatusSummary.Describe` now prints kubectl's own columns for the four networking
kinds that had none or half of one, joined with " · " in the Details cell like every other
kind, from `pkg/printers/internalversion/printers.go`:

| Kind | Columns | Notes |
| --- | --- | --- |
| Ingress | Class · Hosts · Address · Ports | three hosts then "+ N more...", `*` for none; Ports is `80`, or `80, 443` with any TLS entry; an absent class is omitted rather than printed as `<none>` |
| Endpoints | Endpoints | ready addresses only, each with every port, three then "+ N more...", `<none>` for no subsets |
| EndpointSlice | AddressType · Ports · Endpoints | every address regardless of readiness, `<unset>` for none |
| NetworkPolicy | Pod-Selector · policy types | **"all pods"** for an empty selector where kubectl prints `<none>` — which reads as "selects nothing" and means the opposite; the policy types are added because deny-ingress and deny-egress policies otherwise look identical |

`NetworkingListColumnsTests` compares against what `kubectl get` printed for the sandbox's
own objects.

## Gateway API in the Network section (FEAT-62)

`gateway.networking.k8s.io` is in `SidebarGrouping.NetworkGroups`. It is installed as CRDs,
but it is the Kubernetes project's own successor to Ingress (SIG Network, a `k8s.io` group),
and on a cluster routing through it the Gateway/HTTPRoute kinds were five or more rows of an
already hundred-row CRDs section while their Ingress-shaped twin sat in Network. It is still
a **group** rule, so a route kind this code has never heard of lands there too, and a
cluster without Gateway API is unchanged. Two deliberate limits: the experimental channel's
`gateway.networking.x-k8s.io` stays in CRDs (it is experimental by name), and none of the
Gateway kinds joins the basic view's allow-list — like EndpointSlices, they wait for the
advanced view and are one filter keystroke away. FreeLens declined the same move in
2026-07 on the grounds that the API is CRD-installed; Headlamp and Lens ship it; Gateway,
GatewayClass and HTTPRoute have been GA since v1.0, which is the side of the argument this
takes.

## The demo dataset

`Demo/Fixtures/networking.json` holds one object per state the panes render: EndpointSlices
behind the three existing Services (one pod serving and one crash-looping behind
`checkout`, two unscheduled pods and an empty slice behind `fraud-detector`, both pods
serving behind `ledger-api`), the three degenerate shapes (`payments-db` ExternalName,
`legacy-billing` with no selector and hand-written endpoints one of which is not ready,
`checkout-canary` whose selector matches nothing), two legacy Endpoints objects, an Ingress
with TLS, a regex path and a wildcard host, and three NetworkPolicies including a default
deny. It is read through `DemoData.OfKind`, so the Applications mode's linked-resources
panel sees the same objects the Resources mode lists (one dataset, demo rule 3). The
`net-*` screenshot scenarios render each state through the real double-click path.

The sandbox has the same states for real, in `scripts/manifests/40-broken.yaml`'s
`demo-broken` namespace: `never-ready` (a not-ready endpoint), `typo-selector` (matches
nothing), `external-db` (ExternalName), `manual-endpoints` (no selector, one hand-written
EndpointSlice), and two NetworkPolicies, one a default deny. The policies are harmless
there because nothing in that namespace serves traffic — its readiness probes are `exec`.
