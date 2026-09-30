# Changelog

All notable changes to kubeNimbus are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While kubeNimbus is pre-1.0, minor versions may contain breaking changes.

The release workflow reads the section matching a tag out of this file and uses
it as the GitHub Release body, so headings must match tags exactly
(`## [0.1.0] - …` ↔ `v0.1.0`).

## [Unreleased]

### Fixed

- **Logs no longer freeze the window on "Everything".** A pod with a long history used to
  hang the app while its log loaded, and switching the theme with a full log open took a
  second. The log panes now draw only the lines in sight and drop what the scrollback would
  trim before reading it.
- **Large clusters stay responsive** in the Argo CD Resources tab (thousands of managed
  resources), the YAML apply preview (large ConfigMaps), the Service pane (hundreds of
  backing pods), the list search, and a list sorted by CPU or memory, which used to stall
  for about two seconds on every metrics poll with 5,000 pods.
- Web and e-mail addresses in the YAML editor and in a Helm release's values and manifest are
  now plain text in the YAML colours. They used to be drawn in bright blue over the
  highlighting, like links, and a Ctrl+click opened them in the browser or the mail client.
  The values and the manifest also stop scrolling at their last line, so text that fits no
  longer gets a scroll bar.

## [0.5.0] - 2026-09-29

### Added

- **Applications mode.** A cluster now opens on Applications: every Argo CD Application, and
  every workload no Application tracks, with its health and a one-line reason such as
  "Crash-looping (exit 1) · 2 pods not created: namespace quota", sorted so what needs
  attention comes first. Chips narrow it to what needs attention, what was deployed in the
  last hour, or what is not in Argo CD, and the search box finds an app by name or
  namespace. The explorer is still there as **Resources**, one click (or Ctrl/Cmd+Shift+R)
  away, and the choice is remembered.
- **The application page.** Enter on an application shows what the cluster reports is
  wrong, each fact with the field it was read from; its pods; the Services, Ingresses,
  ConfigMaps, Secrets, HPA and PDB it is wired to; a timeline of deploys, container exits
  and warning events over the last hour; what the last deploy changed in the pod template,
  with a link to compare the two commits on GitHub, GitLab, Azure DevOps or Bitbucket; and
  the logs, opened on a crash-looping pod's last run and ending with its exit code. Restart,
  Sync and Edit YAML are on the page, and Edit YAML warns first when Argo CD would revert a
  manual edit.
- Works with narrow permissions: when listing across the cluster is refused, Applications
  reads the namespaces it knows and says which it covers and which were refused. The
  namespace picker opens a namespace by name (type it, press Enter) where listing
  namespaces is not allowed.
- **Service detail.** Double-click a Service to see the pods its selector matches next to
  the endpoints actually serving, with one sentence saying whether traffic can reach them.
  A selector that matches no pod, a selector-less Service and an ExternalName are each
  stated plainly.
- **Ingress detail.** Every route as host/path to backend, TLS per host, and a URL you can
  open or copy. The backend opens its Service.
- **NetworkPolicy detail.** The rules in words (who may reach the selected pods, on which
  ports) and the pods the policy selects. An empty selector means all pods, and the pane
  says so.
- Ingress, Endpoints, EndpointSlice and NetworkPolicy lists show kubectl's own columns.
- **CronJobs:** run one now, suspend it or resume it from the list's menu or the palette,
  with a confirm naming what happens. After a run, "Open Job" shows the Job's pods.
- Jobs open in the workload pane, with their pods, completions and failures against the
  backoff limit.
- A Secret carrying a certificate shows whose it is and when it expires, coloured as expiry
  nears, plus the whole chain (subject, SANs, issuer, validity). This works without
  revealing values, and the key is never read.
- PersistentVolumeClaims and PersistentVolumes name the other end of their binding and open
  it. An environment variable taken from a ConfigMap or Secret key opens that object.
- **Log search, rebuilt.** Matches are highlighted in place with "n of m", and Enter /
  Shift+Enter step between them; the funnel switches to filtering. Regular expressions
  (Alt+R) and match case (Alt+C) are supported, `!word` hides the lines that contain it,
  and while filtering the Context chip keeps 2 to 25 lines around each match, like
  `grep -C`. Double-click a filtered line to see it in the full log.
- A search can be pinned as a coloured highlight (Alt+P, up to five), which stays marked
  while you search for something else.
- A Levels filter (Error / Warn / Info) in the log panes. Lines with no level are always
  shown.
- Log panes count their error and warning lines. A click on the count, or Alt+↑ / Alt+↓,
  jumps between errors without hiding anything, and a strip beside the scrollbar marks
  every error, warning and match.
- A JSON log line opens into its fields with the chevron beside it.
- Clear in the log panes, without restarting the stream. Timestamps show in local time,
  with UTC one click away, and the panes remember timestamps, UTC and wrap across restarts.
- When a followed log stream ends, the pane says why: the connection closed while the
  container is still running, the container restarted, it exited (with its exit code), or
  it has not started yet.
- **Connection failures are explained.** A connect that fails says, in place of the list,
  which step failed, why, and with what (kubeconfig, context, server, user, sign-in method),
  with Retry and a terminal on the cluster.
- Reconnect re-reads the kubeconfig and re-runs its credential plugin without closing the
  tab, and an expired credential (HTTP 401) is re-resolved automatically.
- The cluster's `proxy-url` is honoured (HTTP, HTTPS, SOCKS5), including for exec and
  port-forward.
- Kubeconfig folders: pick a folder and every kubeconfig in it is read, including files
  added later. The kubeconfig is rescanned when the window regains focus.
- The sidebar's Recent kinds are remembered per cluster across restarts.
- A privacy policy, linked from About.

### Changed

- The Resources mode now looks like the Applications mode: the list sits in a card on the
  window's own tone instead of a black panel, and every table uses the same type, with
  small semibold headers, 12px rows with the name in semibold, and fainter row rules.
- Log panes colour the level word instead of the whole line, so a log where every line is
  `info:` no longer turns blue. Error rows carry a red bar and a faint wash, warning rows an
  amber bar, and timestamps are dimmed. The level is read from the first level word, from a
  JSON or `level=` field, or from klog's `E0928` header, and an indented stack trace takes
  the level of the line that threw it.
- Log panes no longer print terminal colour codes. Applications that colour their console
  output (ASP.NET, zap, Rails and most CLI tools) used to show `[40m[32minfo[39m` at the
  start of every line. .NET's `fail:` and `crit:` lines are coloured as errors.
- The log toolbar is shorter: Timestamps, UTC, Wrap, Clear and Save are in a `⋯` menu. A
  merged log with a single pod no longer repeats the pod's name on every line.
- Logs and shells open on the container named by
  `kubectl.kubernetes.io/default-container`, as kubectl does. Logs have one icon
  everywhere.
- Primary buttons, checked boxes and switched-on toggles are the app's own blue on every
  machine instead of the Windows accent colour, and red buttons stay red under the pointer.
- The command palette, the cluster switcher and the Preferences, About and shortcut panels
  sit on a grey card in the dark theme instead of black.
- The cluster switcher is a `+` after the last cluster tab, so the cluster in front is no
  longer named twice in the top bar.
- The status bar appears only when there is something to report; the server version moved
  to the tab's tooltip.
- Cluster-scoped kinds (Nodes, PersistentVolumes, ClusterRoles) say "Cluster-wide" in place
  of a greyed-out namespace picker, and no longer show an empty Namespace column.
- Gateway API kinds (Gateway, HTTPRoute, GRPCRoute and the rest) are listed under Network
  instead of CRDs.
- Pods in workload and node detail show their status as the same pill as the main list.
  Node detail's pod list is live, opens pods on double-click and Enter, and a drain watches
  for evictions instead of re-listing every two seconds.
- On Applications, the search box sits at the right of the title row, and the Sync column,
  the "Not in Argo CD" chip and group headings appear only when they have something to say.
- The YAML editor has a light-theme palette, and its Delete button is drawn as a
  destructive button, away from Apply.
- A credential plugin named bare in the kubeconfig (`command: aws`) is found where a login
  shell would find it, so it works when the app is launched from Finder, the Dock or a
  desktop launcher.
- kubeNimbus reads kubeconfig files itself. An empty kubeconfig is read as one with no
  contexts instead of an error, a key written twice keeps its last value as kubectl does,
  and a file that does not parse is reported with its line and column. One such file no
  longer hides the contexts in the other files.
- Updated Avalonia to 12.1.3, the exec terminal to SvcSystems.UI.Terminal 2.0.0, and
  YamlDotNet to 18.1.0.

### Fixed

- A crash-looping application no longer flickers between Degraded and Healthy between two
  back-offs.
- Esc returns from an application page to the list when the page was opened by
  double-click.
- A switched-on chip toggle (Wrap and the like) keeps its text readable.
- A misspelled field in a YAML apply shows as a refused field on real clusters, which
  answer it with HTTP 500 rather than 400.
- CRD list columns show an array or object value as kubectl does (for example Gateway API
  HTTPRoute hostnames), and resolve backslash-escaped dotted keys such as Crossplane's
  `crossplane\.io/external-name`.
- The multi-pod log pane no longer leaves a freshly started pod without lines, picks up
  pods that had not started yet, and marks them "not started" until they do.
- Workload and node detail no longer lose the selected pod when you switch inspector tabs.
- Refreshing a workload's detail updates the "Unhealthy only" list immediately.
- A Job is no longer offered a rollout restart, and a PersistentVolumeClaim or
  ServiceMonitor no longer offers "Logs (all pods)".
- Only one sidebar row is highlighted at a time.
- The list header, the fleet list, the Events list and the command palette fit narrow
  windows.
- The access review's "Loading…" no longer shows on the "Who can…" tab.
- On Linux with a fresh home directory, the discovery cache and settings are no longer
  written into the current directory.
- The no-kubeconfig screen no longer prints the same sentence twice.
- The demo cluster shows usage for every running pod.
- Discovery no longer lists a resource whose server reports `"verbs": []`.

## [0.4.0] - 2026-09-24

- A cluster that cannot be reached now says why. When the kubeconfig's credential plugin
  (`aws eks get-token`, `kubelogin`, `gke-gcloud-auth-plugin`, …) fails, the tab shows what
  the plugin printed instead of a JSON deserialization error, and a VPN or proxy page
  answering in the API server's place is named as such.
- With the advanced view off, the sidebar now lists about 20 everyday kinds instead of about
  35: Pods, Deployments, Services, ConfigMaps, Secrets, PVCs, Nodes, Namespaces and the like.
  API machinery hides wherever it is filed, including ControllerRevisions, Endpoints and
  EndpointSlices, IngressClasses, the second `events.k8s.io` Events row, LimitRanges and the
  CSI kinds. Nodes and Namespaces are now reachable in this view. The sidebar filter and
  Ctrl/Cmd+K still find everything.
- The advanced-view toggle has an eye-plus icon ("show more"). It used to show the sliders
  icon, which also marks the Config section.
- The Cluster section no longer lists Nodes twice. The second row was the metrics API's
  NodeMetrics, which now carries its own name (and PodMetrics likewise).
- Releases are no longer published as pre-releases just because the version starts with
  `0.`, so the newest one carries GitHub's **Latest** label and the README's download link
  lands on it. Only a suffixed tag (`-rc.1`) is still a pre-release.
- Choose a log range in pod and workload panes: last 200 or 1000 lines, the last 5 minutes,
  hour or day, or everything still retained. The pane shows when its scrollback limit trims
  older lines.
- Show only what is unhealthy on any list — one click or Ctrl+Z narrows pods, workloads or
  events to the ones in trouble, and they appear the moment they break and leave when they
  recover.
- Open any pod's or workload's logs from anywhere: press Ctrl/Cmd+Shift+L (or Ctrl/Cmd+K),
  type part of the name, press Enter.
- Hover any pod or workload in the list and click its logs icon to open its logs; Shift+click,
  or Shift+L, opens them full-size (Esc goes back to the split). A new "Open logs maximized"
  preference makes full-size the default.
- Node detail says more about the machine and what it is doing. The System card adds
  platform, every address, pod ranges, zone and region, instance type, provider ID and when
  the node joined. A new Events tab shows what the kubelet and the node controller recorded
  (disk pressure, evictions, restarts), and a new Usage tab charts measured CPU and memory
  with each figure as a share of allocatable.
- The Events list reads like `kubectl get events`: when each event last happened, its type,
  reason, the object it was about, how many times it happened and its message, newest first —
  and the search box finds events by reason, object or message.
- Open a pod's logs straight from where it is named — workload details, node details, an event
  about the pod, or an Argo CD application's resources — with L or the row's logs icon, instead
  of going back to the Pods list to find it. If the pod has gone since, it says so.

## [0.3.3] - 2026-09-22

### Added

- Deployments, StatefulSets and DaemonSets open on their pods, conditions and events.
  Pod rows support logs and shell shortcuts. The Actions menu offers scale and rollout restart.
- The namespace picker supports search, five recent namespaces and Ctrl/Cmd+Shift+N.
- Resource rows support L, P, S, F, E, R, Delete and `/` shortcuts.
  The workspace restores the selected kind, namespace and cluster tab.

### Changed
- Pods start loading before discovery finishes. Aggregated discovery uses two requests on supported servers.
  A disk cache skips discovery on warm connections and expires after six hours or a server-version change.
  The sidebar context menu can refresh the catalog immediately.
- **A new app icon, shared with pgNimbus.** The mark was redrawn so that its
  plate, field and broom match pgNimbus's exactly, and every icon size — window,
  taskbar, installer, Microsoft Store tiles — is now generated from one vector
  master instead of a set of per-size drawings that had drifted apart.
- **Dependency updates.** Avalonia 12.1.2 and SvcSystems.UI.Terminal 1.1.4 (the
  exec pane's terminal). No behaviour change is intended.

## [0.3.2] - 2026-08-29

### Added

- **kubeNimbus is on the Microsoft Store.** Windows can install it from the
  [Store listing](https://apps.microsoft.com/detail/9MZ3C28M65PB), which is the same build re-signed by Microsoft during
  certification — no SmartScreen prompt, and it updates itself. The downloads on
  the Releases page are unchanged and stay unsigned.
- **Installers for every platform.** Releases now carry an MSI for Windows, a
  `.dmg` for macOS and a `.deb` and `.AppImage` for Linux, beside the portable
  archives that were the only option before. The Windows install is per-user and
  needs no administrator rights; the macOS app is a real `.app` bundle you drag
  to Applications, ad-hoc signed so Gatekeeper says what is actually wrong
  instead of calling the download damaged; the `.deb` registers a desktop
  launcher and pulls in the libraries it needs. CI installs and launches each
  package on its own platform before a release is created.

### Fixed

- **A resource list no longer says a namespace is empty while it is still
  loading it.** Selecting a kind against a distant cluster showed the "No pods
  found" panel for as long as the list request took — a second or more — and
  only then filled in with the pods. The list now says what it is loading, and
  in which namespace, until either the first row arrives or the server confirms
  there is nothing there. A watch that reconnects, or one that fails outright,
  reaches the same states instead of flashing an empty list or spinning forever.
- **List columns no longer shift sideways on their own.** A column sized to its
  content grew to fit the widest value that had ever scrolled into view and
  never shrank back, and every such growth narrowed all the other columns at
  once — so scrolling, a watch update, or just the Age and Restarts cells
  ticking over would move text the reader was in the middle of reading. Columns
  now keep the width they open with, and can still be dragged to any other.
- **Releases no longer attach the Microsoft Store package.** The `.msix` is
  signed with a throwaway certificate for Partner Center and cannot be installed
  by anyone who downloads it, but it was being published as a release asset
  anyway.

## [0.3.1] - 2026-08-27

The first release with downloads. Everything listed under 0.3.0 below is in it —
that tag's build failed while archiving the Windows binary, so it never produced
a release.

### Fixed

- **The release pipeline could not package the Windows build.** The step that zips
  it wrote into a directory it never created, so the Windows archive failed after
  the binary had already been built and tested, and the release was abandoned
  rather than published half-finished. The other three platforms were unaffected.

## [0.3.0] - 2026-08-27

Tagged but never published: the build failed while archiving the Windows binary,
and 0.3.1 above is where all of this actually shipped. The 0.1.0 and 0.2.0
sections below were never cut as releases either, so there is nothing to
download for any of the three.

### Changed

- **A misspelled field in a manifest is now refused instead of quietly dropped.**
  Applying YAML asks the API server for strict field validation, so a typo like
  `contaienrs:` comes back as the server's own message naming the field — before, the
  server pruned the unknown field, reported success, and the apply silently did not do
  what the document said. The apply preview asks the same way, so the refusal arrives
  before anything changes. A server too old to accept the request (roughly Kubernetes
  before 1.27) still applies normally, and the editor says out loud that unknown fields
  are being dropped rather than refused on that cluster.

- **The Advanced view now hides sidebar sections, and nothing else.** It used to
  strip controls all over the content area — the CPU/memory columns and their
  sparklines, pod detail's Usage tab, the all-clusters toggle, both log toolbars'
  wrap/copy/download, YAML force-apply, the Helm and access-review commands, and a
  CRD's own low-priority printer columns. All of those are now always available.
  The switch governs the two sidebar sections most sessions never open — **Cluster**
  (APIServices, CSRs, ClusterRoles, admission and flow control) and **CRDs** — and it
  is **on by default**, so nothing is missing until you ask for a shorter list.
  The sidebar's own filter and the command palette still reach every kind whatever
  the switch says.
- **The sidebar is narrower and you can drag it.** It used to take about a quarter of
  the content area at every window size, which on a wide monitor was hundreds of
  pixels showing the same short kind names. It now opens at a fixed 224px, resizes by
  dragging its right edge, and remembers where you left it.
- **macOS: the app is called kubeNimbus.** It introduced itself as "Avalonia" in the
  menu bar, and its application menu was a placeholder. There is now a real menu bar —
  application menu with About and Preferences, plus Cluster, View and Help — carrying
  the same commands and the same shortcuts as the rest of the app.

### Fixed

- **The light/dark toggle could not leave dark.** It saved the chosen theme in a
  spelling the settings file does not accept, so the app fell straight back to
  following the operating system's theme. On a machine set to dark that made the
  button look one-way: light → dark worked, dark → light did nothing. An existing
  settings file written by the broken build is read correctly rather than reset.

## [0.2.0] - 2026-08-20

### Added

- **Argo CD is in the sidebar.** A cluster running Argo CD gets an **Argo**
  section, with a GitOps dashboard at the top of it: every Application on the
  cluster, the counts across all of them (Applications, Synced, Healthy, Out of
  sync, Degraded, Missing, Progressing), and a list ordered by what needs looking
  at first. Sync status and health are shown as two separate pills, because they
  answer different questions — an Application can be perfectly synced and
  degraded, which is exactly the case worth finding.

  Opening one shows what Git says it should be, what Argo made of that, every
  object Argo manages for it with its own sync and health, its conditions, and its
  deployment history. Any managed object is one click from its own manifest.

  **Sync** and **Refresh from Git** are on the row's context menu and in the
  command palette. Both ask for a confirmation first, and Sync's *Prune* option —
  the half of a sync that deletes resources which have left Git — is off unless
  you turn it on.

  It needs no Argo CD API server, no URL, no `argocd` binary and no second login:
  Argo keeps its Applications in the cluster as ordinary custom resources, so
  everything here goes through the same Kubernetes connection the rest of the app
  uses. An Argo CD that is only reachable from inside the cluster works fine.
  It also works on the built-in demo cluster, which now ships seven sample
  Applications.

- **The node pane now shows limits as well as requests, on the same track.** Each
  resource's bar carries the requested figure as its filled portion and the sum of
  the pods' declared limits as a lighter extent with a marker where it falls, and
  the row prints the limits total and what percentage of allocatable it is. Limits
  that add up to more than the node has are ordinary overcommit rather than a
  fault, so the marker pins at the end of the track in amber and the figure goes
  amber with it — never silently clamped into looking like a node that is exactly
  full. The pod-count row has no limit and shows none.

- **Container requests and limits are readable on pod detail's Usage tab.** Each
  container's CPU and memory now print what it asks for and what it is capped at,
  underneath the current and peak readings, together with the current usage as a
  percentage of the limit. They used to be reachable only by hovering a container
  chip. A container that declares no request, no limit, or neither says so in
  words rather than showing a blank — and because these numbers come from the pod
  spec rather than from metrics, they stay readable on a cluster with no
  metrics-server installed, where the notice explaining that now sits above them
  instead of replacing the whole tab.

- **Pod detail has an Overview tab.** The pod's conditions, its tolerations, its
  node selector, its QoS class and priority class, and the selected container's
  liveness, readiness and startup probes — each as its own section, rather than
  something to go and find in the YAML. The probe lines read the way
  `kubectl describe` writes them (`http-get http://:8080/healthz`,
  `delay=5s timeout=1s period=5s #success=1 #failure=3`), so a probe read here
  and a probe read in a terminal are visibly the same probe.

  A condition Kubernetes defines as good-when-true reads green when it is true
  and red when it is not; `DisruptionTarget` reads the other way round; and a
  condition type kubeNimbus does not recognise is shown in grey rather than
  claimed to be healthy. Everything comes from the object already on screen —
  no extra request, and it works on the demo cluster.

- **Apply now shows what it would change, before it changes it.** Pressing Apply
  in the YAML editor asks the API server to run the apply as a dry run and then
  shows the difference between the object as it is and the object as the server
  says it would be — as a diff of the manifest, with the changed lines in place,
  line numbers on both sides and the untouched parts collapsed to a few lines of
  context. Nothing is written until you press the second button.

  The diff opens inline, the way `kubectl diff` and `git diff` read; a **Split**
  toggle puts old and new side by side, and **Fields** lists the changed field
  paths instead, which is the view that can tell you a container was inserted
  rather than every container rewritten. Which one you pick sticks for as long
  as the tab is open.

  Because both sides come from the server, the preview includes what your text
  does not say: fields a defaulting webhook fills in, values a mutating
  controller rewrites, and the API server's own validation, which now refuses a
  bad manifest *before* the object moves rather than after. A field-manager
  conflict shows up here too, with its force-apply confirming under its own
  label. Server bookkeeping — `managedFields`, `resourceVersion`, `generation` —
  is left out and counted, so a one-line change reads as a one-line change;
  lists of containers, ports, env vars and volumes are matched by name, so
  inserting one container no longer reports all of them as changed.

  "The server reports this apply would change nothing" is its own answer, and
  the whole step can be turned off in Preferences → *Preview before applying*
  for anyone who wants Apply to go straight through as it used to.

- **Node detail, and cordon / uncordon / drain.** Double-click a node to open a
  pane showing its conditions, its taints, the version and image the kubelet
  reports, and — the number you actually open a node for — how much of it the
  scheduler has already promised away: CPU, memory and pod count as *requested
  against allocatable*, with a bar and a percentage each. A second tab lists the
  pods that are really on the node, with each pod's own CPU and memory requests,
  and opens any of them. Right-click a node (or use the command palette) to
  **cordon** it so nothing new schedules there, **uncordon** it to put it back,
  or **drain** it.

  The drain asks first and shows its plan before it touches anything: how many
  pods it will evict, how many it will leave in place (DaemonSet pods, static
  pods and finished pods, exactly as `kubectl drain` leaves them), and — by name —
  any pod it *refuses* to evict, because evicting it would destroy something that
  does not come back: a pod no controller owns, or a pod whose `emptyDir` data
  lives only on that node's disk. Each refusal names the option that would allow
  it, and ticking one updates the plan in front of you. While the drain runs you
  see every pod as it goes: evicted, held back by a PodDisruptionBudget and still
  being retried, or refused outright with the server's own reason. It runs inside
  kubeNimbus, and says so before it starts — closing the tab or quitting stops it
  partway, leaving the node cordoned with some pods moved; **Stop draining** does
  the same on purpose and then tells you exactly what state the node is in and
  how to finish or undo it.

  The demo cluster ships three nodes — one of them cordoned and reporting disk
  pressure — so the whole pane, the plan and its refusals work with no cluster at
  all; only the eviction itself needs a real API server, and it says so in place.

- **Tail every pod of a workload in one pane.** Right-click a Deployment,
  StatefulSet, DaemonSet, ReplicaSet, Job or Service (or anything else that names
  the pods it owns, custom resources included) and choose **Logs (all pods)** —
  also in the command palette — to get a single stream across every pod it owns,
  with each line prefixed by its pod and coloured to match. Lines are merged on
  the timestamps the server already sends, so a rolling deployment reads as one
  story rather than as two logs you have to interleave by eye: the replica
  draining and the replica coming up appear in order, in the same pane. Pods that
  appear join the stream on their own; a pod that is deleted stops streaming and
  keeps the lines it already sent, because what a terminating replica said last is
  usually the reason you opened the pane. Click a pod's chip to hide or show just
  that replica, filter the merged text as usual, and copy or download the result
  with the pod names attached. Very large workloads stream their first 50 pods and
  say so rather than opening hundreds of connections. Works on the demo cluster
  with no cluster at all.

### Fixed

- **The node pane's resource bars are the same length as each other.** CPU, Memory
  and Pods print different-width numbers, and the bars used to take whatever width
  was left over after them, so the three tracks ended at three different places
  and could not be compared row to row.

- **Log lines without a severity keyword were invisible in the dark theme.**
  Anything the app did not classify as ERROR, WARN or INFO — nginx access logs,
  plain `print` output, JSON lines, which is most real log output — was drawn in
  black on the dark theme's near-black background, in the pod log pane. Light
  theme was unaffected, which is why this went unnoticed.

- **Custom resources now show the columns their CRD asks for.** A
  CustomResourceDefinition declares what a list of its objects should show —
  cert-manager's Certificates want READY and SECRET, Flux's Kustomizations want
  READY and STATUS, an Argo Rollout wants its replica counts — and `kubectl get`
  has always honoured that. kubeNimbus showed all of them the same generic Status
  column; now it shows the same columns `kubectl get <crd>` does, resolved live
  from each object as the watch updates it. Turning on the advanced view is this
  list's `-o wide`: it adds the columns the CRD itself marked as lower priority.
  Nothing about built-in kinds changes — Pods, Deployments, Nodes and Events keep
  exactly the columns they had. A CRD that declares no columns, a resource served
  by an aggregated API, and a cluster where you cannot read CRDs all keep today's
  list rather than showing an error. The demo cluster gained a set of cert-manager
  Certificates so this is visible with no cluster at all.

- **A real terminal in the exec pane.** Exec now runs a full VT emulator instead
  of stripping escape codes, so the tools people actually exec in for work:
  `vi`, `top`, `htop`, `mc`, `less` and anything else that paints a screen draw
  properly, in colour, with the cursor where the program put it. The pane
  scrolls back, text can be selected with the mouse, and it tells the container
  how wide it really is — so nothing wraps at 80 columns any more just because
  the dock is wider than that. `Ctrl`+`C`, `Ctrl`+`D`, `Tab`, the arrow keys and
  the function keys all reach the shell, which means the terminal — not
  kubeNimbus — owns `Ctrl` chords while it has focus; **Copy and Paste are
  `Ctrl`+`Shift`+`C` / `Ctrl`+`Shift`+`V`**, or right-click, as in any terminal
  emulator. The command input box below the terminal is gone: you type into the
  terminal itself. One known gap: highlighted text drawn with a terminal's
  "reverse video" (`top`'s column header, `less`'s prompt line) currently renders
  unhighlighted.
- **Open a terminal on this cluster.** From the ☰ menu or `Ctrl`/`Cmd`+`K`,
  kubeNimbus starts your own terminal — Windows Terminal or conhost, Terminal on
  macOS, whatever `xdg-terminal-exec`/`$TERMINAL` resolves to on Linux — with
  `KUBECONFIG` set and the current context already pointed at the cluster in the
  selected tab. Your kubeconfig is never modified and never copied: the context
  is pinned by merging a tiny generated file ahead of it, which holds a context
  name and nothing else, so `kubectl`, `helm`, `k9s`, `stern` and `kubectx` all
  agree about which cluster that window is on. Each cluster gets its own pinning
  file, so two terminals on two clusters cannot end up pointed at the same one.
  If `kubectl` is not found, the terminal still opens and the app says so — it
  also notes that an app usually sees a shorter `PATH` than your shell does, so
  the tool may well be there. If no terminal could be opened at all, the exact
  `KUBECONFIG` value is shown to copy. On the demo cluster it explains that
  there is no kubeconfig behind sample data rather than opening anything.
- **Workload actions — scale, rollout restart, and delete — from the resource
  list.** Right-click a row (or use `Ctrl`/`Cmd`+`K`) for **Scale…**, **Rollout
  restart…** and **Delete…**. Each one arms a confirm strip above the list that
  names the object before anything happens: Scale reads the workload's current
  replica count and takes a new one, Restart stamps the pod template the same
  way `kubectl rollout restart` does — so the controller rolls the pods under
  its own update strategy, honoring surge, `maxUnavailable` and
  PodDisruptionBudgets, rather than deleting them out from under it — and
  Delete asks first (unless you have turned "Confirm before deleting" off).
  Whether an object can be scaled or restarted comes from the cluster itself,
  so a custom resource with a `scale` subresource or an embedded pod template
  gets the same actions the built-in kinds do. Failures are shown in place with
  the API server's own message, which for the common one (RBAC) names the user,
  the verb and the resource. Until now the only way to change a replica count
  was to edit YAML by hand.
- **Demo cluster.** With no kubeconfig and no cluster, **Explore demo cluster**
  (in the empty state, in `Ctrl`/`Cmd`+`K`, and in the cluster switcher's own
  group) opens a full sample workload set that ships inside the binary — pods
  in every interesting state, live-looking log streams, env vars and secrets,
  events, usage graphs, Helm releases and a realistic CRD catalog. No cluster,
  no credentials, no network. It is labelled as sample data throughout (tab
  name, switcher group, and a banner above the content for the tab's whole
  life), and the panes that genuinely need an API server — exec, port-forward,
  YAML apply and delete — say so rather than pretending. The dataset is the
  same one the screenshot harness renders, so the two cannot drift apart.
- **Open kubeconfig file…** in the no-kubeconfig empty state. Until now the only
  routes to a cluster were `$KUBECONFIG` — which a GUI launched from Explorer, a
  shortcut or the Microsoft Store never inherits — and dropping a file at
  `~/.kube/config` by hand, so a first run on a clean machine had no next step
  that could be taken from inside the app. Only the **path** is remembered (in
  the workspace, across restarts); the file is re-read through the normal
  kubeconfig chain at load and at connect time, so nothing is copied into app
  storage. A picked file that has since moved is listed as `missing` in the
  empty state's search list rather than failing the load.
- **Cluster switcher** (`Ctrl`/`Cmd`+`P`, or the cluster button in the top bar):
  one fuzzy-searchable list over both open cluster tabs and unopened kubeconfig
  contexts, grouped Open / Pinned / Recent / All. Matches on context name,
  cluster name and kubeconfig path, so `ppr` finds `payments-prod` and an
  opaque EKS ARN is still reachable by the cluster behind it.
- **Pinned clusters.** The handful you actually work in sit at the top of the
  switcher, every session. Persisted in the workspace.
- **Environment colours.** Clusters are classified production / staging /
  development from their context and cluster names and coloured accordingly —
  a dot on the switcher button, a left edge on each cluster tab, a pill in the
  switcher, and a red band under the command bar while a production cluster is
  selected. Right-click a cluster tab to correct the guess; the assignment is
  remembered.
- **`Ctrl`/`Cmd`+`1`…`9`** jumps straight to a cluster tab (9 = last).
- **Search the resource list by name** (`Ctrl`/`Cmd`+`F`, or the box in the list
  header). Matches name, namespace and — across clusters — the cluster, with a
  running "12 of 87" beside it so a filtered list never looks like a small one.
  `Esc` clears it, `Enter` moves to the rows, and a search that matches nothing
  says so and offers the way back rather than showing an empty table. The
  sidebar's box filters resource *kinds*; this one filters the objects.

### Changed

- **One bar of chrome at the top of the window instead of two.** On Windows and
  macOS the command bar is now the title bar itself, so the row that held only a
  window title and three buttons is gone and the content starts ~36px higher —
  about two more log lines in the inspector dock, permanently. Dragging,
  double-click to maximize, the window menu and Windows 11 Snap Layouts all still
  work, from the empty space in the tab strip. Linux keeps its system window
  decorations, where client-side ones would look out of place on half the desktops
  we ship for.
- The kubeNimbus wordmark is no longer drawn in the top bar. The window title and
  the taskbar icon already carry it, and once the bar became the title bar it was
  printing the window's own title back at it.
- The cluster switcher and the top bar's cluster button are no longer disabled
  when no kubeconfig context exists — they always carry at least the demo
  cluster, and gating them on "has contexts" made them dead on precisely the
  machine where the demo cluster is the only cluster there is.
- The no-kubeconfig empty state now leads with the file picker and no longer
  tells you to run `scripts/sandbox-up` — an instruction nobody who installed a
  released build can follow. Setting up a throwaway local cluster is covered in
  [CONTRIBUTING.md](CONTRIBUTING.md) and the README, where a contributor is
  already looking.
- The top bar's context dropdown is gone. It could not search, truncated the
  long auto-generated names managed Kubernetes hands out, and only chose what
  the `+` button would open — switching to an already-open cluster was a
  separate gesture. The switcher does both jobs.
- Cluster tabs scroll instead of squeezing the rest of the command bar off the
  right edge, and show an active-tab highlight.
- The command palette no longer lists every kubeconfig context; it offers the
  switcher instead, so a large kubeconfig can't bury every other command.
- **Inspector panels give their content the room back.** The bottom dock spent
  up to four stacked rows of chrome before anything you opened it to read: pod
  detail had an owner row, a container row, a tab strip and a per-tab toolbar,
  plus a full-width filter box on Logs. Tabs are now a compact strip that shares
  its row with the selected tab's tools, the owner chips ride the container row,
  and the Helm and access-review panels dropped title rows that only repeated
  their own dock tab. Roughly 100px of a 300px dock handed back, in every panel.
- Pod detail's environment list puts each variable's name beside its value
  instead of above it, and its events feed puts the count and timestamp on the
  reason's line — twice as many rows visible in the same space. "Reveal" now
  sits next to the reference it reveals rather than at the pane's right edge.
- Long inspector tab titles (a generated pod name is ~50 characters) trim with
  a tooltip instead of pushing the dock's tab strip onto a second row.
- **ConfigMap-backed environment variables show their value straight away.**
  Only Secrets are hidden now, behind an eye toggle that can also hide the value
  again — a ConfigMap is ordinary configuration, and a click to read
  `LOG_LEVEL=info` bought nothing. Nothing is fetched for a Secret until you ask
  for it, so the value still never reaches the app unrequested.
- **The port-forward panel was rebuilt.** Six rows became two: the panel title
  the dock tab already carried is gone, fields read local → pod in the direction
  the traffic goes, the local port is an empty box marked `auto` instead of a
  `0` that silently meant the same thing, Start and Stop are one button rather
  than a pair with one half always dead, and a running forward shows its local
  URL — selectable, copyable, openable — instead of a sentence about it.
- **The list's name filter is now covered by automated tests.** The thing they
  guard is specific: while a filter is on, kubeNimbus keeps watching every
  object, not just the ones on screen, so an update to something the filter
  hides can never make it pop back into a filtered list — and clearing the
  filter always gives you back the current state of everything, including
  objects that appeared or changed while it was on.
- **Changing the shortcut modifier while the app is open is now covered by
  automated tests.** Preferences → Shortcut modifier already applied without a
  restart — the shortcuts, the `F1` cheat sheet and every tooltip switch between
  `Ctrl` and `Cmd` immediately, and the modifier you switched away from stops
  working — and that behaviour was checked by hand against the running app for
  the first time. The tests are there so the half that would fail silently
  cannot come back: a modifier you have turned off continuing to work looks
  exactly like the setting doing nothing.

### Fixed

- **Every downloadable binary is now launched before it is published.** The
  0.1.0 release shipped Linux and macOS builds that could not start at all —
  they exited with an error before drawing anything — because the release
  workflow compiled each one and never ran it. CI and the release workflow now
  start the binary they just built, on a runner of its own platform, and refuse
  to archive or publish it unless its main window actually appears. A build that
  cannot start fails loudly instead of reaching a download page.
- Table columns ran into each other. A right-aligned value sat flush against the
  next column's text — `48 MiB16d` — and, worse, the `—` shown for a pod with no
  usage reading landed against its age and read as a negative one (`—5d`). Cells
  now have a gutter on both sides, and the column widths were re-cut so the last
  column still fits in a narrow window.
- Clicking a cluster in the switcher only worked when the click landed on the
  cluster's name. A pointer handler sat on the row's content panel, which in
  Avalonia doesn't receive clicks where no child covers it — so most of each
  row, and the row's own padding, selected the cluster without opening it. Taps
  are now handled on the list itself and resolved to the row underneath.
- Cluster tabs and switcher rows had no pressed state and no hand cursor, so a
  click gave nothing back until whatever it did became visible — and switching
  to the cluster you were already on gave nothing back at all.
- A cluster tab's status dot showed the same grey for "connecting" and "not
  connected", so opening a cluster looked like it had failed until it finished.
  Connecting is now amber, and the dot carries the connection status as a
  tooltip.

## [0.1.0] - 2026-08-02

First public release. Everything below is new.

### Added

**Connecting**

- Kubeconfig context discovery across the whole `$KUBECONFIG` chain plus
  `~/.kube/config`, with an explicit empty state that names the paths it
  searched (including the ones that didn't exist) and offers a rescan.
- Exec-plugin authentication — `aws eks get-token`, `gke-gcloud-auth-plugin`,
  `azure kubelogin` — resolved through the kubeconfig at connect time, and
  applied to watch and log streams as well as ordinary requests.
- Multi-cluster context tabs with drag-reorder and workspace restore.

**Browsing**

- Discovery-driven sidebar covering built-in kinds *and* CRDs, grouped into
  Workloads / Network / Config / Storage / CRDs. Nothing is hardcoded: an
  unrecognised API group falls through to CRDs automatically.
- Sidebar filter matching display name, API group and `kubectl` short names
  (`svc`, `po`), collapsible sections, and a pinned session-scoped **Recent**
  section.
- Namespace-scoped or all-namespaces live lists for any kind, backed by
  informer-style list+watch with `continue`-token pagination, resourceVersion
  resume, relist on 410 Gone, and exponential-backoff reconnect with the
  connection state surfaced inline.
- Explicit loading, empty, disconnected and error states throughout — no view
  renders as a blank rectangle.
- Owner-reference navigation (pod → replicaset → deployment) as clickable
  chips; double-clicking an Event navigates to its involved object.

**Inspecting**

- Pod detail docked along the bottom, Lens-style, resizable and maximizable:
  container chips with status and restarts, and tabs for logs, environment,
  events and usage.
- Live log streaming with follow mode, container picker, previous-container
  fetch, in-buffer search, ERROR/WARN/INFO colouring, timestamp and wrap
  toggles, copy and download.
- Environment tab showing `env` and `envFrom`, with `secretKeyRef` /
  `configMapKeyRef` displayed as references until an explicit per-row reveal
  fetches and decodes the value.
- Events as a scannable card feed with type colouring and a jump to the
  involved object.
- YAML view and edit with syntax highlighting, server-side apply through a
  field manager, conflict detection with an offered force-apply, and a
  two-step delete. Secrets keep `data` base64 in the editor with a separate
  opt-in decoded panel.
- Interactive **exec** into a container and **port-forward**, both over
  websockets, with ANSI handling and capped scrollback in the terminal.

**Measuring**

- Live CPU and memory from `metrics.k8s.io` in the resource list and per
  container, with the API version read from discovery. A cluster without
  metrics-server degrades to no CPU/Memory columns rather than an error.
- Usage graphs over the session's rolling 30-minute window: a sparkline beside
  each list number, and pod-total plus per-container charts in a Usage tab.
  A missing reading is drawn as a gap, not as zero.

**Operating**

- Read-only Helm release browsing — values, rendered manifest, notes and
  revision history — read straight from release Secrets, with no Helm binary
  involved. The section appears only on clusters that actually store releases.
- RBAC access review in three directions: your effective permissions via the
  API server's own `SelfSubjectRulesReview`; where a ServiceAccount's access
  comes from, via binding provenance; and cluster-wide "who can do X?", a
  local RBAC scan labelled as provenance with a one-click
  `SubjectAccessReview` to confirm any row against the API server. Partial
  results always say they are partial.
- Multi-cluster aggregated ("All clusters") lists for any kind, with a Cluster
  column, per-cluster reconnect handling, and an honest "n of m clusters serve
  X" when a kind isn't served everywhere.

**Shell**

- Command palette (Ctrl/Cmd+K), platform-aware shortcuts, an F1 cheat sheet,
  and light/dark themes.

**Project**

- NativeAOT as the shipping configuration, verified in CI on every change.
- A one-command sandbox cluster (`scripts/sandbox-up`) — single-node k3s in
  Docker preloaded with workloads chosen to make every UI surface non-empty.
- A headless screenshot harness (`tools/Screenshot`) that renders real Views
  without a display, a cluster or a Windows box.

### Security

- No credentials are ever persisted. Kubeconfig is the single source of truth
  and is re-resolved at connect time; the workspace file stores context names
  only. See [SECURITY.md](SECURITY.md).
- No telemetry of any kind, and no network traffic beyond the Kubernetes API
  servers you connect to. This is a permanent non-goal.

### Known limitations

- Release binaries are **unsigned** — Windows shows a SmartScreen prompt and
  macOS quarantines the app.
- macOS and Linux builds are produced by CI but have had far less hands-on
  testing than Windows.
- No `linux-arm64` or `osx-x64`-specific testing beyond the build itself.
- Helm is read-only; install, upgrade and rollback stay Helm's job.
- Usage history is session-scoped and bounded at 30 minutes by design. Long-range
  metrics history is a non-goal — that's Prometheus's job.

[Unreleased]: https://github.com/Shman4ik/kubeNimbus/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/Shman4ik/kubeNimbus/releases/tag/v0.5.0
[0.4.0]: https://github.com/Shman4ik/kubeNimbus/releases/tag/v0.4.0
[0.3.3]: https://github.com/Shman4ik/kubeNimbus/releases/tag/v0.3.3
[0.3.2]: https://github.com/Shman4ik/kubeNimbus/releases/tag/v0.3.2
[0.3.1]: https://github.com/Shman4ik/kubeNimbus/releases/tag/v0.3.1
