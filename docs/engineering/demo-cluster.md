# The demo cluster

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "The demo cluster" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## The demo cluster

`ClusterContext.Demo` is a built-in cluster with no cluster behind it: a dataset that
ships inside the binary, browsable with no kubeconfig, no credentials and no network.
It exists for two audiences at once. **Microsoft Store certification** requires that a
reviewer on a clean Windows machine — no kubeconfig, no Kubernetes anywhere — can see
the app function; before this, they landed on an empty state whose only instruction
was to run a script from a repo they don't have. And **anyone evaluating kubeNimbus**
can now look around before wiring up credentials, which is the one thing a Kubernetes
client cannot otherwise demonstrate. Handing out a real cluster to either group is not
an option (rule #4 forbids the app holding credentials at all), so the sample data
*is* the demo.

Six rules:

1. **A demo tab is an ordinary `ClusterContext` with a sentinel `KubeconfigPath`**
   (`ClusterContext.DemoKubeconfigPath`, `"<demo>"`; `IsDemo` reads it). A sentinel
   rather than a new record field, so `WorkspaceSettings` tab snapshots, the cluster
   switcher's name+path keying and fleet member naming all keep working untouched —
   verified against `RestoreWorkspaceAsync` (which needs one explicit branch, because
   the demo context is not in `AvailableContexts` and so can never match by name+path)
   and `ClusterSwitcherViewModel` (which needed a `Demo` group and a subtitle that
   doesn't print the sentinel as a filename). `ClusterEnvironments.Classify` reads it
   as **Development** — `demo` is a development marker, so it can never come out
   production and put a red band under a screen of invented pods.
2. **There is no `ClusterClient`, and that is the mechanism, not a detail.**
   `ClusterTabViewModel.Client` stays null for a demo tab's whole life, and every
   inspector tab takes `ClusterClient?` and derives `IsDemo` from `client is null`.
   "A demo tab never connects, never watches, never touches the network" is therefore
   something the compiler helps hold: every call site that would have talked to a
   server had to be branched before it would build. `ConnectDemo` fills in for
   discovery/namespaces/the metrics probe, and `RestartWatch`'s `if (Client is not { }
   client)` arm is the list. Do **not** reintroduce an offline `ClusterClient` pointed
   at a dead port to satisfy a constructor — the screenshot harness still has one
   (`FixtureData.CreateOfflineClient`, for scenarios that want the *failed-connection*
   paths), and it is exactly the thing the app must not copy.
3. **One dataset, not two.** `src/KubeNimbus.App/Demo/` owns it — `DemoData` (objects,
   catalog, sidebar, Helm, and the one CRD whose `additionalPrinterColumns` the demo
   list draws — `crds.json` is a real-shaped `CustomResourceDefinition`, read through
   the same `PrinterColumns.Parse` a live cluster's GET goes through, and
   `networking.json`, one object per state the Service, Ingress and NetworkPolicy panes
   render), `DemoLogs`
   (canned streams), `DemoUsage` (replayed metric polls) — and `tools/Screenshot/FixtureData.cs` is now a passthrough to it. What a
   screenshot shows and what a user clicking "Explore demo cluster" sees cannot drift
   apart. The JSON is an `EmbeddedResource` with an explicit `LogicalName`
   (`Demo.<file>.json`), same reasoning as `Yaml-Mode.xshd`: the lookup must not depend
   on the assembly being called `kubeNimbus`, and a `Fixtures/` directory next to the
   exe would break the single-file NativeAOT publish. `JsonDocument` only, kept alive
   for the process lifetime (`DynamicResource` wraps `JsonElement`s that die with their
   document).
4. **Everything that can work, works through production code.** Logs go through
   `Enqueue` on a timer, so batching, trimming, filtering, the timestamp toggle and
   every placeholder state are the real ones. Usage goes through `ResourceRowViewModel
   .ApplyUsage` / `PodDetailTabViewModel.ApplyMetrics` with stamped timestamps — which
   is what those optional `at` parameters have always been for. Env resolves
   Secret/ConfigMap refs through the same cache and the same base64 decode, against
   `DemoData.ReadObject` instead of a GET. A kind the dataset has nothing for lands on
   the real "No &lt;kind&gt; found" empty state, which is most of a 100-kind catalog.
   `DemoRowsTests` pins what the dataset shows through `PopulateDemoRows` (ENG-14): every
   payments pod, a crash loop among them, usage on **every** running pod, three nodes of
   which one is cordoned, a CRD in its own columns, and an empty kind landing on the
   empty state. Writing it found drift: pods added for later features had arrived with
   no `pod-metrics.json` entry, so ten running pods drew a usage column of dashes — the
   rule is one metrics entry per running pod, and that test is what holds it.
   `DemoLogs` deliberately carries **lines with no severity keyword** (nginx access
   logs, JSON, plain prints): every fixture line having one is precisely what hid the
   log pane's invisible-plain-line bug, twice — see "Log severity is three classes,
   not a brush binding".
5. **What cannot work says so, in place.** Exec, port-forward and YAML apply/delete
   need a real API server. Each renders a styled `Border.demoUnavailable` (or, for the
   YAML editor, a `demoBar` above a still-useful read-only editor) naming what it can't
   do and what to do instead, and each disables its commands via `CanExecute` — never a
   spinner that hangs, never a blank pane, never a silent no-op (UI rule 9's last
   clause). The access review is palette-gated on `IsDemo: false` for the same reason:
   its three API-server calls have no honest offline stand-in, and a palette entry that
   matches a search and then refuses to run is worse than no match.
6. **Nobody may mistake it for a real cluster.** The tab reads `Demo cluster`, the
   switcher lists it under its own "Demo (sample data, not a real cluster)" heading,
   and a `Border.demoBar` sits above the content area for the tab's entire life. That
   last one is a deliberate exception to UI rule 1, and the justification is the
   alternative: someone believing a screen full of invented pods is their own workloads.
   A notice that appears once and dismisses does not prevent that.

**Reachability.** "Explore demo cluster" is the most prominent control in the
no-kubeconfig empty state (it is the button a Store reviewer presses), plus a
Ctrl/Cmd+K entry, plus the switcher's own group. Two silent `HasContexts` gates had to
go for that to hold — `SwitcherButton.IsEnabled` and, less visibly,
`MainWindow.OpenSwitcher`'s early return — which between them made the top bar's
cluster button and Ctrl/Cmd+P dead on exactly the machine where the demo cluster is
the only cluster there is. `AddNewTabCommand.CanExecute` is now unconditionally true
for the same reason: the switcher always has at least the demo row in it.
