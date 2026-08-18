# Networking surfaces in Kubernetes desktop clients — demand, competitors, and what it means here

**Date:** 2026-08-18
**Scope:** Services, Ingress, Endpoints/EndpointSlices, NetworkPolicy, Gateway API, and the
"why can't this reach that" debugging workflow.
**Why now:** every earlier report under `docs/research/` covers something else (terminal
libraries, node operations and overview, create/edit, KubeUI positioning, logs, connecting
to a cluster, pod and workload detail). `docs/BACKLOG.md` has exactly two networking rows
in it — FEAT-29 (port-forward a Service) and FEAT-46 (a Service's targeted pods) — and the
app itself lists every networking kind through the generic discovery-driven list and does
nothing kind-specific with any of them beyond two Details strings.

## Method, and what could not be checked

Searched: the issue trackers of `lensapp/lens`, `freelensapp/freelens`,
`aptakube/aptakube`, `kubernetes-sigs/headlamp`, `derailed/k9s`, `IvanJosipovic/KubeUI`,
`getseabird/seabird` and `kubernetes/dashboard`; each project's own source for what it
actually renders (source beats a screenshot and beats a blog post); Kubernetes' own
printers for what `kubectl get` shows; and the Gateway API CRDs for what a client gets for
free from `additionalPrinterColumns`.

Two limits on the evidence below, both stated so the reader can discount accordingly:

- **`api.github.com` is repository-scoped in this environment** (`"sessions are bound to
  their configured repositories"`), exactly as the last three reports recorded. So **no
  row below cites a reaction count.** Ranking is by issue age, number of independent
  filings, whether the filer was a user or a maintainer, and whether a competitor shipped
  it — never by 👍.
- **`aptakube.com`, `lenshq.io` and `docs.k8slens.dev` are blocked by the egress proxy**
  (`EGRESS_BLOCKED`). Aptakube's behaviour below is therefore reconstructed from its
  public issue tracker and its GitHub README, and the Lens Desktop 2026.5 release content
  is quoted from search-engine summaries of <https://lenshq.io/blog/lens-release-may26>
  that I could not open directly. Treat the Lens 2026.5 details as second-hand; the Lens
  *issues* cited are first-hand.

## 1. What kubeNimbus ships today (measured, not remembered)

| Kind | What the app shows now | Where |
|---|---|---|
| Service | No status pill or dot (`StatuslessKinds`); Details column = `type · clusterIP · external · ports` with `<pending>` for an addressless LoadBalancer | `ResourceStatusSummary.DescribeService` |
| Ingress | No status; Details = `hosts · loadBalancer address`. No class, no ports, no rules, no TLS, no clickable URL | `ResourceStatusSummary.IngressHosts` / `LoadBalancerAddress` |
| Endpoints, EndpointSlice | Generic list only — name, namespace, age. Not in `DetailKinds`, so the Details column is empty | `ResourceStatusSummary.DetailKinds` |
| NetworkPolicy | Generic list only. No Details, no structured view anywhere — raw YAML in the editor is the whole surface | as above |
| Gateway API | Falls through to the **CRDs** sidebar section (`SidebarGrouping.NetworkGroups` is `networking.k8s.io` + `discovery.k8s.io` only); list columns come from FEAT-2's printer-column reader | `SidebarGrouping.cs`, `PrinterColumns.cs` |

Two engine capabilities already shipped are load-bearing for everything proposed below:

- **`LabelSelector.ForPodsOf` already reads a Service's plain `spec.selector` string map**
  and refuses an empty selector rather than reading it as "everything" (FEAT-3's rule 2),
  and `WatchResourceAsync`/`ListResourceOnceAsync` already take a `LabelSelector?`. "Which
  pods does this Service select" is therefore a *view*, not new engine work.
- **FEAT-2's printer columns already cover most of Gateway API.** The standard
  `Gateway` CRD declares `Class` (`.spec.gatewayClassName`), `Address`
  (`.status.addresses[*].value`) and `Programmed`
  (`.status.conditions[?(@.type=="Programmed")].status`)
  ([gateways CRD](https://raw.githubusercontent.com/kubernetes-sigs/gateway-api/main/config/crd/standard/gateway.networking.k8s.io_gateways.yaml)),
  and all three are inside `SimpleJsonPath`'s documented subset — dotted paths, `[*]`, and
  the condition filter. A Gateway list in kubeNimbus today should already read the way
  `kubectl get gateways` does. **HTTPRoute does not**, and section 4 explains why.

## 2. What each competitor actually ships

### Lens (Mirantis) — leads with breadth, and has just made Gateway API a headline

Lens's own tracker shows the two classic Ingress asks were filed by users and closed years
ago: [lens#772 *"Status details for Ingress"*](https://github.com/lensapp/lens/issues/772)
(opened 2020-08-31, closed, milestone 4.0.0 — the ask was literally "I can only see the
external IP by running kubectl or opening the YAML"), and
[lens#4626 *"Allow opening or copying ingress URL"*](https://github.com/lensapp/lens/issues/4626)
(opened 2022-01-04 by `dominch`, closed via PR #4630 — *"to copy the link I need to open
developer tools"*).

[lens#6048 *"Support Gateway API"*](https://github.com/lensapp/lens/issues/6048) was opened
**2022-08-18 by `jakolehm`, a Lens maintainer**, the month Gateway API reached beta; it is
now closed. Per search-engine summaries of the (unreachable-from-here)
[Lens Desktop 2026.5 release post](https://lenshq.io/blog/lens-release-may26), that release
ships *"full support for all ten resources, covering Gateway, GatewayClass, HTTPRoute,
GRPCRoute, TCPRoute, UDPRoute, TLSRoute, ReferenceGrant, BackendTLSPolicy, and
ListenerSet"*, with three details worth copying down: *"Services show the Gateway API
routes that target them in a unified Routes tab"*, *"a dedicated Gateway API navigator
group where all ten resources live in their own sidebar group **instead of buried under
Custom Resources**"*, and *"Gateway shows Properties, Listeners, and attached Routes"*.
That is marketing emphasis in its purest form: the *grouping* is the feature being sold.

### FreeLens — the surviving fork, and the clearest read on unmet demand

FreeLens inherits the Lens/OpenLens detail views and has extended them:

- **Service detail** ([`service-details.tsx`](https://raw.githubusercontent.com/freelensapp/freelens/main/packages/core/src/renderer/components/network-services/service-details.tsx))
  renders selector badges, type, session affinity, traffic distribution/policies,
  LoadBalancer status *with its per-port error conditions*, and an **Endpoint Slices**
  table.
- **The endpoint slice rows are clickable and carry readiness**
  ([`endpoint-slice-details.tsx`](https://raw.githubusercontent.com/freelensapp/freelens/main/packages/core/src/renderer/components/network-endpoint-slices/endpoint-slice-details.tsx)):
  per endpoint, a link to its `targetRef` pod, a link to its node, its zone, and
  `Ready` / `Serving` / `Terminating` badges off `endpoint.conditions`.
- That view is **new**, not inherited: [freelens#838](https://github.com/freelensapp/freelens/issues/838)
  (2025-06-09, *"The EndpointSlice API is a replacement for the older Endpoints API and
  should be presented in Freelens instead of Endpoints"*) shipped as
  [PR #846](https://github.com/freelensapp/freelens/pull/846), merged 2025-06-11, milestone
  v1.4.0.
- **NetworkPolicy detail** ([`network-policy-details.tsx`](https://raw.githubusercontent.com/freelensapp/freelens/main/packages/core/src/renderer/components/network-policies/network-policy-details.tsx))
  renders pod selector, then Ingress and Egress sections with ports and each peer's
  `ipBlock` / `namespaceSelector` / `podSelector`. **Not raw YAML.**
- **Ingress detail** ([`ingress-details.tsx`](https://raw.githubusercontent.com/freelensapp/freelens/main/packages/core/src/renderer/components/network-ingresses/ingress-details.tsx))
  renders rules grouped by host, ports, TLS secret names, and a Load-Balancer Ingress
  Points table.
- **Gateway API is deliberately an extension, not core.**
  [freelens#424](https://github.com/freelensapp/freelens/issues/424) (2025-03-14, user
  `acelinkio`: *"GatewayAPI is the successor to Kubernetes ingresses"*, asking for
  GatewayClass/Gateway/HTTPRoute/GRPCRoute/ReferenceGrant under Network) led to
  [PR #1551](https://github.com/freelensapp/freelens/pull/1551), which the maintainers
  converted into a separate `freelens-gateway-api-extension`; a later attempt to put it
  back in core, [PR #2223](https://github.com/freelensapp/freelens/pull/2223)
  (2026-07-18), was declined by maintainer `dex4er` with the reasoning *"Gateway API is not
  finished. Its development is outside the main Kubernetes APIs and it is implemented as
  CRDs. So it is not really something standard yet."* **This is the one place the field
  disagrees with itself, and it is exactly the decision a human here has to make.**
- Still open: [freelens#1678 *"Extend ingress search"*](https://github.com/freelensapp/freelens/issues/1678)
  (2026-03-02) — search should match `rules[].host`, because otherwise finding which
  Ingress owns a URL means `kubectl get ingresses -o wide | grep`.

### Aptakube — the closest positioning competitor, and the thinnest networking story

Its [README](https://raw.githubusercontent.com/aptakube/aptakube/main/README.md) feature
list — multi-cluster, aggregated log viewer, resource diff, multi-namespace selector,
"human-friendly resource view", "**NOT** another Electron app" — contains **nothing
networking-specific at all**. The networking work is visible only in the tracker:

- [aptakube#21 *"Kind: Ingress"*](https://github.com/aptakube/aptakube/issues/21) (closed
  2022-12) — Ingress support was itself a user request.
- [aptakube#64 *"Ingress TLS -> Still show http:// url"*](https://github.com/aptakube/aptakube/issues/64)
  (2023-02-07, user `FilipK-CZ`): Aptakube rendered
  `http://argo-cd.kube1.example.com/→argo-cd-argocd-server:80` for a TLS-terminated
  Ingress, and the report is framed as *"Lens shows it correctly as https"*. So Aptakube
  renders Ingress rules as `protocol://host/path → service:port` in the list, and users
  compare tools on whether the protocol is TLS-aware.
- [aptakube#67 *"Add support for 'Endpoints' resource"*](https://github.com/aptakube/aptakube/issues/67)
  — **open since 2023-02-10**. Filed by `goenning`, i.e. Aptakube's own author, so it is
  maintainer backlog rather than user demand; either way, the closest paid competitor has
  not shipped the Endpoints surface in three and a half years.
- [aptakube#112 *"Support Gateway API"*](https://github.com/aptakube/aptakube/issues/112),
  opened 2023-05-31 by user `oscar-b` (*"It would be nice to proper built in support for
  the Gateway API, displaying for instance `Gateway` and `HTTPRoute`"*), **closed 2026-04**.
  Search summaries of the (unreachable) changelog describe *"improvements to Gateway API UI,
  such as GRPCRoute, HTTP Route Status column, Parent Conditions, and more complete matching
  support with Headers and QueryParams"*, and a *"custom human-friendly UI for Argo
  Applications, Argo Rollout, Cert-Manager and Gateway API"* — i.e. Aptakube built
  **bespoke per-CRD views**, which is a different and much larger bet than generic printer
  columns.

### Headlamp (CNCF) — the most complete, and the most recently moving

- **Service detail** ([`service/Details.tsx`](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/service/Details.tsx))
  ships, with no click required: type, cluster IPs, external IP/name, IP families, session
  affinity, both traffic policies, health-check node port, LB class and source ranges,
  traffic distribution, the selector, a **Targeted Pods** section, a Ports table with a
  per-port port-forward button, an **Endpoints** table and an **Endpoint Slices** table.
- The Targeted Pods half is **three weeks old**:
  [headlamp#6929 *"Show pods targeted by Service"*](https://github.com/kubernetes-sigs/headlamp/issues/6929)
  (2026-08-05, user `jimmyjones2`: *"currently only Endpoint/EndpointSlice information is
  shown, requiring manual conversion to identify actual pods"*) →
  [PR #6972](https://github.com/kubernetes-sigs/headlamp/pull/6972), merged 2026-08-08,
  milestone v0.45.0. The PR states its design choice explicitly: it lists the pods matched
  by `spec.selector` **rather than resolving through EndpointSlices**, *"so a wrong or
  empty selector is obvious at a glance"*, and the section is hidden for Services without a
  selector.
- The same reporter immediately filed the two follow-ups, both open:
  [#6930 *"Show pods targeted by NetworkPolicy"*](https://github.com/kubernetes-sigs/headlamp/issues/6930)
  (2026-08-05, *"help users identify label selector mismatches quickly"*, PR #7021 in
  progress) and [#7028 *"Clicking on pod selector shows matched pods"*](https://github.com/kubernetes-sigs/headlamp/issues/7028)
  (2026-08-09) — generalising the gesture to Service, NetworkPolicy, PodDisruptionBudget
  and every workload.
- **NetworkPolicy** ([`networkpolicy/Details.tsx`](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/networkpolicy/Details.tsx))
  renders pod selector, ingress and egress rules, ports with ranges, `ipBlock` cidr/except,
  namespace and pod selectors — structured, not YAML.
- **Ingress** ([`ingress/Details.tsx`](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/ingress/Details.tsx))
  builds a real `http(s)://host/path` hyperlink per rule, choosing the scheme by checking
  whether the host appears in `spec.tls[].hosts`, prints the `pathType` beside each path,
  and formats backends as `service:port` (or `Kind:name` for a resource backend).
- **Gateway API is fully in-core**: `frontend/src/components/gateway/` holds Details+List
  pairs for Gateway, GatewayClass, HTTPRoute, GRPCRoute, TCPRoute, UDPRoute, L4Route,
  ReferenceGrant, BackendTLSPolicy and BackendTrafficPolicy, with Gateway rendering
  Addresses, Listeners (hostname/port/protocol/conditions) and Conditions. It landed as
  *"Add support (beta) for Gateway API"* in
  [Headlamp v0.28.0, 2025-01-23](https://github.com/kubernetes-sigs/headlamp/releases/tag/v0.28.0).

### k9s — what the keyboard fallback does, and what it refuses

[`internal/view/svc.go`](https://raw.githubusercontent.com/derailed/k9s/master/internal/view/svc.go)
makes **Enter on a Service show the pods its selector matches** (`showPods`), with two
explicit flash messages for the cases that have no answer — *"No matching pods. Service %s
is an external service"* for `ExternalName`, and *"...does not provide any selectors"* —
and wraps the view in a port-forward extender. So the single most common Service gesture in
the TUI everyone falls back to is *selector → pods*, and it treats the two degenerate cases
as first-class states.

What k9s deliberately does **not** do is open a URL:
[k9s#3413 *"Shortcut to Open Ingress or VirtualService URLs in Browser"*](https://github.com/derailed/k9s/issues/3413)
(2025-06-24) was closed `as-designed`. That is a gesture a GUI is entitled to own and a TUI
is not — which makes it differentiation rather than parity.

### KubeUI — the one true peer

`src/KubeUI.Avalonia/Resources/Network/v1/` holds exactly five kinds: **EndpointSlice,
Ingress, IngressClass, NetworkPolicy, Service** — and **no Gateway API**. Its
[`V1ServiceConfig.cs`](https://raw.githubusercontent.com/IvanJosipovic/KubeUI/main/src/KubeUI.Avalonia/Resources/Network/v1/Service/V1ServiceConfig.cs)
ships a per-port **service port-forward** whose `CanExecute` first checks
`CanI<V1Pod>(create, "portforward")` **and** `CanI<V1EndpointSlice>(list/watch)` — because
resolving a Service to a pod is an EndpointSlice read, and it would rather grey the menu
out than fail mid-forward. Its Ingress list carries Load Balancers and Rules columns; its
NetworkPolicy list carries a policy-types column.

### Seabird

`internal/ui/` has no per-kind networking view at all (`common/`, `editor/`, `list/`,
`single/` only) — consistent with `CLAUDE.md`'s note that the project has had no commit
since August 2025. No evidence either way; excluded from the ranking below.

### One outside datapoint worth naming

`kubernetes/dashboard` (archived 2026-01) carries
[#1317 *"Show 'real' Pods running behind a service"*](https://github.com/kubernetes/dashboard/issues/1317),
opened **2016-10-10** and still open at archival: the dashboard showed both selector-matched
pods while the Service was routing to only one, and the reporter asked for the two to be
distinguishable. Ten years later Headlamp shipped the *selector* half (PR #6972) and
FreeLens shipped the *endpoint readiness* half (PR #846) — **and nobody ships both on one
screen.** That gap is the strongest single opportunity in this whole report.

## 3. Demand signals, ranked by evidence strength

1. **A Service's pods, and whether they are actually being served.** Demand: k9s makes it
   the Enter key; [headlamp#6929](https://github.com/kubernetes-sigs/headlamp/issues/6929)
   → merged in three days; [dashboard#1317](https://github.com/kubernetes/dashboard/issues/1317)
   asked for the harder half in 2016; [freelens#838](https://github.com/freelensapp/freelens/issues/838)
   shipped the readiness half in 2025. Marketing: Headlamp and FreeLens both render it
   unprompted; Aptakube ships neither ([#67](https://github.com/aptakube/aptakube/issues/67)
   open since 2023).
2. **Gateway API as a first-class, non-CRD-drawer citizen.** Demand: user-filed in three
   trackers over four years — [lens#6048](https://github.com/lensapp/lens/issues/6048)
   (2022, maintainer), [aptakube#112](https://github.com/aptakube/aptakube/issues/112)
   (2023, user), [freelens#424](https://github.com/freelensapp/freelens/issues/424) (2025,
   user). Marketing: shipped by Headlamp (2025-01), Aptakube (2026-04) and Lens (2026-05),
   with Lens selling the *sidebar grouping* as the feature. Counter-evidence, and it is
   serious: FreeLens **refused** to put it in core as recently as
   [2026-07-18](https://github.com/freelensapp/freelens/pull/2223).
3. **Ingress rules as openable URLs with the right scheme.** Demand:
   [lens#4626](https://github.com/lensapp/lens/issues/4626) (2022, shipped),
   [lens#772](https://github.com/lensapp/lens/issues/772) (2020, shipped),
   [aptakube#64](https://github.com/aptakube/aptakube/issues/64) (2023, a user comparing
   Aptakube's http:// against Lens's https://), [k9s#3413](https://github.com/derailed/k9s/issues/3413)
   (2025, closed as out of scope for a TUI). Marketing: Headlamp computes the scheme from
   `spec.tls` and renders a real link.
4. **Structured NetworkPolicy, and the pods it targets.** Demand:
   [headlamp#6930](https://github.com/kubernetes-sigs/headlamp/issues/6930) and
   [#7028](https://github.com/kubernetes-sigs/headlamp/issues/7028) (both 2026-08, both
   open, one user); the existence of a third-party
   [Lens NetworkPolicy graph extension](https://github.com/artturik/lens-extension-network-policy-viewer)
   whose v3 *"shows pods that match NetworkPolicy"*. Marketing: Lens lineage, Headlamp and
   KubeUI all render policy rules structurally; **kubeNimbus is the only one of the five
   where a NetworkPolicy is raw YAML and nothing else.**
5. **Finding an Ingress by its hostname.** Demand: one open issue,
   [freelens#1678](https://github.com/freelensapp/freelens/issues/1678) (2026-03) — a
   regression report, which is stronger than a bare wish, but it is one filing.
6. **Endpoints/EndpointSlice as a browsable kind with real columns.** Demand is thin and
   mostly maintainer-authored ([aptakube#67](https://github.com/aptakube/aptakube/issues/67),
   [freelens#838](https://github.com/freelensapp/freelens/issues/838)). Marketing: three
   competitors list it. Its real value here is as the *ingredient* of item 1.
7. **A topology map / connectivity tester.** Marketing only, and from one new entrant:
   [Podscape](https://github.com/codingprotocols/podscape) (Electron) leads with a
   force-directed *Network Map* and a *Connectivity Tester* doing "source-to-target DNS/TCP/
   HTTP diagnostics with automated NetworkPolicy and endpoint failure analysis". **No
   filed demand found in any of the six established trackers.** See section 5.

## 4. What this implies for kubeNimbus specifically

**Nothing proposed here needs a new dependency, and nothing needs reflection.** Every item
is JSON already in hand (`DynamicResource`), the existing `labelSelector` list+watch, or a
string. The three constraint notes that do matter:

- **`Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` is already the
  app's way of opening a URL** (`PortForwardTabViewModel:275`, `AboutView.axaml.cs:39`), so
  an openable Ingress link costs no platform work — but it is the one place in this report
  where the app hands a **cluster-controlled string** to the OS shell. An Ingress host comes
  from a manifest somebody else may control; the URL must be built from `scheme://host/path`
  with the scheme chosen by us and the host validated as a hostname, never passed through
  raw. Headlamp hit the same edge and guards it with a `new URL()` parse that falls back to
  plain text (`LinkStringFormat`, *"Ingress' host … is not a valid URL; displaying it
  without a link"*). Copy that behaviour, do not invent one.
- **A found defect in a shipped feature.** `PrinterColumns.Evaluate`'s doc-comment says
  *"The API server skips object/array values outright rather than dumping JSON into a table
  cell, and so does this"*, and `SimpleJsonPath.ScalarText` accordingly returns `null` for
  an array → an empty cell. That is only true for `integer`/`number`/`boolean`/`date`
  columns. For `type: string`, the API server does **not** call `cellForJSONValue` at all —
  it runs the value through the JSONPath printer
  ([tableconvertor.go:123-134](https://raw.githubusercontent.com/kubernetes/apiextensions-apiserver/master/pkg/registry/customresource/tableconvertor/tableconvertor.go)),
  which formats a slice with `fmt.Fprint`
  ([jsonpath.go `evalToText`](https://raw.githubusercontent.com/kubernetes/client-go/master/util/jsonpath/jsonpath.go)).
  So `kubectl get httproute` prints its `HOSTNAMES` column
  ([`.spec.hostnames`, a string array](https://raw.githubusercontent.com/kubernetes-sigs/gateway-api/main/config/crd/standard/gateway.networking.k8s.io_httproutes.yaml))
  and **kubeNimbus renders it blank** — on the single most-installed Gateway API kind, in
  the feature whose acceptance criterion was "the same columns `kubectl get` does". Gateway
  itself is unaffected (all three of its columns are scalars).
- **Sidebar grouping is by API group outside the core group, and that rule is what makes
  the Gateway API question cheap**: `gateway.networking.k8s.io` into
  `SidebarGrouping.NetworkGroups` is one line and stays a group rule, not a Kind allow-list,
  so it cannot regress the property `SidebarGrouping`'s own doc-comment defends. It is also
  the *whole* of what Lens is selling; per-kind Gateway detail views (Aptakube's bespoke
  route UI) are a much larger and much less evidenced bet, and FEAT-2 already gives the
  lists their kubectl columns.

Two existing rows should be read again in this light rather than duplicated:

- **FEAT-46** ("Service detail: a Targeted Pods section") is *right* and now has much
  stronger evidence than the marketing note it was filed with — but it is **half the
  feature**, and Headlamp's own PR says which half it chose and why. Proposal FEAT-59 below
  supersedes it; the human should merge or close FEAT-46 rather than run both.
- **FEAT-29** (port-forward a Service) gains two pieces of evidence it did not have: k9s
  wraps its Service view in a `PortForwardExtender`, and KubeUI's implementation resolves
  the Service through **EndpointSlices** with an RBAC pre-check on `list`/`watch` before it
  will enable the menu item. That is the design answer to FEAT-29's open "which pod did it
  pick" question, and it means FEAT-29 wants FEAT-60's endpoint reader underneath it. No
  new row; the note belongs on FEAT-29.

## 5. What this report refuses to propose

- **A connectivity tester** ("can pod A reach service B on port 443, and if not, which
  NetworkPolicy stopped it"). It is the most attractive idea in the whole networking space,
  Podscape leads its marketing with it, and it **cannot be built without running something
  inside the cluster** — an ephemeral debug pod or a DaemonSet probe. That is
  `CLAUDE.md`'s "in-cluster agents" non-goal, and the evidence does not argue against the
  non-goal: no filed demand for it exists in any of the six established trackers, so this
  is one vendor's marketing, not the market. Recorded as a refusal so it is not
  re-litigated from scratch next quarter. The same applies to `ksniff`-style packet
  capture.
- **A NetworkPolicy or service topology *graph*.** The prior pod/workload-detail report
  already left the relationship-graph shape to a human design decision, on UI rules 1 and
  10. The evidence here does not change that: the one graph implementation found is a
  third-party Lens **extension**, and Headlamp's own answer to the same question
  ([#6930](https://github.com/kubernetes-sigs/headlamp/issues/6930)) is a *list of matched
  pods*, not a diagram. The list is the evidenced feature; the graph is not.
- **Bespoke per-kind Gateway API detail views** (Aptakube's route matchers, parent
  conditions, header/query matching). Real, shipped, and an L-sized bet on a CRD group one
  competitor's maintainers still call unfinished. FEAT-2's printer columns plus FEAT-62's
  one-line grouping get most of the value for an S; revisit only if that lands and people
  still ask.
- **A `kubectl describe`-style raw text pane for networking kinds.** No demand found; every
  competitor renders structured fields instead.

**No evidence found** for, despite looking: dual-stack / IP-family UI complaints,
IngressClass-specific views, `externalTrafficPolicy` confusion, service-mesh CRDs beyond
Aptakube's Istio-adjacent work, or anyone asking a desktop client for EndpointSlice
*editing*.

## Proposed Inbox rows

Formatted for `docs/BACKLOG.md`'s Inbox table (`| ID | Item — *done when* | Signal | Size |
Rec | Prio |`). **Both the Rec and Prio columns are left blank deliberately** — a human sets
priority, and this report does not propose one, matching how FEAT-48 … FEAT-51 were filed.
IDs continue from FEAT-58; **another agent is editing `docs/BACKLOG.md` concurrently in this
session, so renumber if it has taken FEAT-59+.**

| ID | Item — *done when* | Signal | Size | Rec | Prio |
|---|---|---|---|---|---|
| FEAT-59 | Service detail pane: the pods its selector matches **and** the endpoints actually serving, side by side — *done when opening a Service shows its selector, its ports, the pods `spec.selector` matches, and each EndpointSlice endpoint with its ready/serving/terminating condition and a link to its pod; a selector that matches nothing, an `ExternalName` service and a selector-less service are three distinct stated states* | Demand, strongest in this report: [k9s makes Enter→pods the Service gesture](https://raw.githubusercontent.com/derailed/k9s/master/internal/view/svc.go) with named states for the two degenerate cases; [headlamp#6929](https://github.com/kubernetes-sigs/headlamp/issues/6929) (2026-08-05) merged in 3 days as [PR #6972](https://github.com/kubernetes-sigs/headlamp/pull/6972) (v0.45.0) — *"so a wrong or empty selector is obvious at a glance"*; [freelens#838](https://github.com/freelensapp/freelens/issues/838)/[#846](https://github.com/freelensapp/freelens/pull/846) shipped the readiness half in 2025-06; [dashboard#1317](https://github.com/kubernetes/dashboard/issues/1317) asked for both in **2016** and no competitor shows both on one screen. **Notes:** supersedes **FEAT-46** — close or merge that row, don't run both. Engine exists: `LabelSelector.ForPodsOf` already reads a Service's string-map selector and refuses an empty one, and `WatchResourceAsync` takes a `LabelSelector?`. EndpointSlices are found by owner reference, as FreeLens does. Must fit UI rule 10's two chrome rows; would be the app's second non-Pod detail pane after `NodeDetailTabViewModel`, which is the shape to copy. Demo dataset has **no Service/EndpointSlice fixtures at all** — adding them is part of the work (demo rule 4) | M |  |  |
| FEAT-60 | kubectl's own list columns for the four networking kinds that have none — *done when NetworkPolicy shows Pod-Selector, Endpoints shows its endpoint list, EndpointSlice shows AddressType/Ports/Endpoints, and Ingress gains Class and Ports beside the hosts it already shows* | Marketing/table-stakes with an exact specification: [`kubectl`'s own printers](https://raw.githubusercontent.com/kubernetes/kubernetes/master/pkg/printers/internalversion/printers.go) define Ingress = Class/Hosts/Address/Ports, Endpoints = Endpoints, EndpointSlice = AddressType/Ports/Endpoints, NetworkPolicy = Pod-Selector. **Notes:** today `ResourceStatusSummary.DetailKinds` covers only Service and a partial Ingress, so a NetworkPolicy list in kubeNimbus is name + age — less than `kubectl get netpol`. Pure `ResourceStatusSummary` work, no engine change, unit-testable with no cluster. Prerequisite-ish for FEAT-59 (the same endpoint formatting) and cheap enough to land first | S |  |  |
| FEAT-61 | Printer columns: render a string array the way the API server does instead of blanking the cell — *done when a CRD's `type: string` column over an array prints its elements, `kubectl get httproute` and kubeNimbus's HTTPRoute list agree on HOSTNAMES, and non-string column types keep today's skip-the-array behaviour* | A defect in shipped FEAT-2, found by reading upstream: for `type: string` the API server bypasses `cellForJSONValue` and runs the JSONPath printer ([tableconvertor.go:123-134](https://raw.githubusercontent.com/kubernetes/apiextensions-apiserver/master/pkg/registry/customresource/tableconvertor/tableconvertor.go), [jsonpath.go `evalToText`](https://raw.githubusercontent.com/kubernetes/client-go/master/util/jsonpath/jsonpath.go)), so an array prints; `SimpleJsonPath.ScalarText` returns null for arrays and `PrinterColumns.Evaluate`'s doc-comment asserts the API server does the same, which is only true for non-string types. Bites the most-installed Gateway API kind: [HTTPRoute declares `.spec.hostnames`](https://raw.githubusercontent.com/kubernetes-sigs/gateway-api/main/config/crd/standard/gateway.networking.k8s.io_httproutes.yaml) as its only non-Age column. **Notes:** Gateway itself is unaffected (Class/Address/Programmed are all scalars, and `[*]` + the condition filter are already supported). Fully verifiable in this container — `PrinterColumnTests` is the existing suite, and the fix is one branch in `Evaluate` plus a decision on the separator (kubectl's `fmt` gives `[a b]`; a comma-joined list may read better and should be a stated choice, not an accident) | S |  |  |
| FEAT-62 | Put `gateway.networking.k8s.io` in the sidebar's **Network** section instead of CRDs — *done when a cluster with Gateway API installed lists Gateway/GatewayClass/HTTPRoute/GRPCRoute/routes under Network, the section is still built from the API group and not a Kind allow-list, and a cluster without Gateway API is unchanged* | Demand across three trackers over four years: [lens#6048](https://github.com/lensapp/lens/issues/6048) (2022, maintainer-filed), [aptakube#112](https://github.com/aptakube/aptakube/issues/112) (2023, user, closed 2026-04), [freelens#424](https://github.com/freelensapp/freelens/issues/424) (2025, user: *"GatewayAPI is the successor to Kubernetes ingresses"*). Marketing: [Headlamp v0.28.0](https://github.com/kubernetes-sigs/headlamp/releases/tag/v0.28.0) (2025-01-23) shipped it in-core, and Lens Desktop 2026.5 sells the grouping itself — *"their own sidebar group instead of buried under Custom Resources"* (via <https://lenshq.io/blog/lens-release-may26>, second-hand: the site is egress-blocked here). **Notes and the tension to weigh:** [freelens#2223](https://github.com/freelensapp/freelens/pull/2223) was **declined** 2026-07-18 — *"Gateway API is not finished … implemented as CRDs … not really something standard yet"* — so this is the one item where competitors actively disagree. One line in `SidebarGrouping.NetworkGroups`, still a group rule; FEAT-2 already gives the lists their kubectl columns, and FEAT-61 fixes the one that is blank. Bespoke Gateway/HTTPRoute detail views (Aptakube's route matchers, parent conditions) are explicitly **not** in this row | S |  |  |
| FEAT-63 | Ingress detail: rules as `host/path → backend`, TLS state per host, and an **openable** URL — *done when each rule shows its host, path, pathType and backend service (openable as an object), the scheme is `https` exactly when the host appears in `spec.tls[].hosts`, the URL can be opened and copied, and a host that is not a valid hostname renders as plain text rather than a link* | Demand, twice-filed and shipped by the competitor both times: [lens#772](https://github.com/lensapp/lens/issues/772) (2020, *"the only other way to see the external IP is the Edit view or kubectl"*), [lens#4626](https://github.com/lensapp/lens/issues/4626) (2022, *"to copy the link I need to open developer tools"*, closed by PR #4630); [aptakube#64](https://github.com/aptakube/aptakube/issues/64) (2023) is a user reporting Aptakube's `http://` against Lens's `https://`, i.e. users compare tools on exactly this. [k9s#3413](https://github.com/derailed/k9s/issues/3413) asked for it and was closed `as-designed` — a gesture a GUI owns and a TUI does not. Marketing: Headlamp's [`ingress/Details.tsx`](https://raw.githubusercontent.com/kubernetes-sigs/headlamp/main/frontend/src/components/ingress/Details.tsx) computes the scheme from `spec.tls` and links it. **Notes:** `Process.Start(… UseShellExecute = true)` is already the app's URL-opening pattern (`PortForwardTabViewModel:275`), but this is the first time a **cluster-controlled string** reaches it — build the URL from a validated hostname, never pass a manifest value through raw, and fall back to plain text as Headlamp does. Could be a detail pane or an extension of the Details column; the pane is the evidenced version | M |  |  |
| FEAT-64 | NetworkPolicy detail: the rules as rules, and the pods the policy selects — *done when a policy shows its pod selector, its policy types, and its ingress/egress rules with ports and each peer's ipBlock/namespaceSelector/podSelector, plus the pods `spec.podSelector` currently matches; an empty `podSelector` is stated as "all pods in this namespace", not shown as an empty selector* | Demand: [headlamp#6930](https://github.com/kubernetes-sigs/headlamp/issues/6930) (2026-08-05, open, PR #7021 in progress) and [#7028](https://github.com/kubernetes-sigs/headlamp/issues/7028) (2026-08-09, open) — both user-filed, both framed as *"identify label selector mismatches quickly"*; a third-party [Lens NetworkPolicy graph extension](https://github.com/artturik/lens-extension-network-policy-viewer) exists whose v3 headline is "shows pods that match NetworkPolicy". Marketing: the Lens lineage ([`network-policy-details.tsx`](https://raw.githubusercontent.com/freelensapp/freelens/main/packages/core/src/renderer/components/network-policies/network-policy-details.tsx)), Headlamp and KubeUI all render policy rules structurally — **kubeNimbus is the only one of the five where a NetworkPolicy is raw YAML and nothing else**. **Notes:** the empty-selector case is the trap and it is the *opposite* of FEAT-3's rule 2 — for a NetworkPolicy, `podSelector: {}` genuinely does mean every pod in the namespace, so `LabelSelector.ForPodsOf`'s deliberate refusal must not be reused blindly here. Reuses FEAT-59's matched-pods list. **Not** a graph: the evidenced feature is a list of matched pods | M |  |  |
| FEAT-65 | Let the list filter match an Ingress's hosts — *done when typing a hostname in the list search finds the Ingress that serves it, and the rule for which fields a kind contributes to the filter is stated in one place* | Demand, single-filing but a regression report rather than a wish: [freelens#1678](https://github.com/freelensapp/freelens/issues/1678) (2026-03-02, open) — host search was removed and the workaround is `kubectl get ingresses -o wide \| grep`. **Notes:** this **pushes against UI rule 13**, which says the filter matches *what identifies an object* — name, namespace, cluster — and deliberately not status, because "Running" would match a healthy list. A hostname is arguably identity for an Ingress and is nothing like a status, but the rule's boundary would move, so this is a design decision before it is code. Cheapest honest version: match the kind's own Details/printer cells, which generalises rather than special-casing Ingress — and which would also make cert-manager's Ready column searchable, for better or worse. Human call | S |  |  |

**Verifiability in this container (the Ready-table entry test).** All seven are buildable
and checkable here without a live cluster, a Windows/macOS box or a display: each is
view-model or `ResourceStatusSummary`/`PrinterColumns` work, testable by TUnit and
renderable in the headless screenshot harness, and each needs demo fixtures added (the demo
dataset today carries **no** Service, Ingress, Endpoints, EndpointSlice or NetworkPolicy
objects, so demo rule 4 makes fixtures part of every one of these rows). FEAT-61 is the only
one whose correctness can be pinned entirely against upstream sources with no judgement
call. What *cannot* be done here, as in every prior pass, is the live half — a real
EndpointSlice going not-ready mid-watch, a real Gateway API installation's sidebar, and a
real Ingress URL opening in a browser all want a cluster and a desktop, and should be filed
as verification debt in the same cycle the features land.
