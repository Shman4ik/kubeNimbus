# Verification workflow

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "Verification workflow" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## Verification workflow

```powershell
# Build everything.
dotnet build KubeNimbus.slnx

# Run Core tests against the sandbox cluster (skips if none).
# `--project` is MANDATORY: under the .NET 10 Microsoft.Testing.Platform runner
# (pinned in global.json) a positional csproj prints "Specifying a project for
# 'dotnet test' should be via '--project'" and exits 0 having run NOTHING. That
# silently passed for a while in CI — if a change to the suite looks suspiciously
# green, check the invocation first.
dotnet test --project tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj

# Run the App layer's view-model tests. Same runner, same --project rule; these
# need no cluster, no display and no Avalonia app instance. Two invocations
# rather than one over the solution so a red run names which half broke.
dotnet test --project tests/KubeNimbus.App.Tests/KubeNimbus.App.Tests.csproj

# Run the app against the sandbox during development.
$env:KUBECONFIG = ".sandbox/kubeconfig.yaml"
dotnet run --project src/KubeNimbus.App

# Headless visual check (no display, e.g. Claude Code Cloud) — see below.
dotnet run --project tools/Screenshot -- /tmp/kubenimbus-screenshots

# Stress mode: every data surface fed a large cluster's worth of objects, against a
# budget of visuals, collection notifications and time — see "The stress mode" below.
dotnet run --project tools/Screenshot -- --stress

# NativeAOT publish — THE shipping build. Verify it end-to-end on every change
# that could affect trimming/AOT (new package, new reflection, new binding).
dotnet publish src/KubeNimbus.App -c Release -r win-x64 -p:PublishAot=true -o publish/app

# And then LAUNCH what you just published. A clean publish is not a working
# binary — see "The launch check" below.
publish/app/kubeNimbus --smoke-test        # Linux: wrap in xvfb-run -a
```

On a machine without the Windows/MSVC toolchain (e.g. this repo's Linux dev
containers, Claude Code Cloud), `dotnet publish src/KubeNimbus.App -c Release
-r linux-x64 -p:PublishAot=true -o publish/app` exercises the same
IL-trimming/AOT analysis and catches the same class of problems (new
reflection, a non-trim-safe binding) even though it isn't the shipping
binary — run it after any change that could plausibly affect trimming, and
call out in the PR that the authoritative win-x64 publish still needs a
local Windows pass.

### View-model tests (`tests/KubeNimbus.App.Tests`)

The App layer had no test project until VER-5, and the gap was not an oversight so
much as an unanswered question: the code worth pinning is in `KubeNimbus.App`, and
hard rule 1 forbids moving it to Core to reach `KubeNimbus.Core.Tests`. So there is
now a second TUnit project — same runner, same `--project` rule, same "never add
`Microsoft.NET.Test.Sdk`" — referencing `KubeNimbus.App` directly.

Four things about it:

- **It starts no Avalonia application.** `ClusterTabViewModel`'s constructor, the
  watch-apply path and the `Rows`→`VisibleRows` mirror are plain
  CommunityToolkit MVVM over `ObservableCollection`; `Dispatcher.UIThread` only
  appears on the far side of a live watch, which these tests never start (`Client`
  stays null, so `RestartWatch` returns before it can). If a future test does need
  a rendered control, that is `Avalonia.Headless` and the screenshot harness's
  pattern — not a headless app instance bolted onto this one by default.
- **It drives the real methods, not a copy.** `ClusterTabViewModel.Apply` and
  `ApplyFleet` are `internal` (with `InternalsVisibleTo` in the App csproj) purely
  so the tests can post watch events the way the watch pump does. A test over a
  stand-in reproduction of the mirroring logic would pin nothing: the bug it guards
  against is one a second implementation, written from the rule, would not have.
- **It redirects both stores.** `AppSettingsStore.DirectoryOverride` and
  `WorkspaceStore.DirectoryOverride` are set to a temp directory in
  `TestObjects.RedirectStores`, same reason the screenshot harness sets them —
  a test run must not read, still less write, the files of whoever is running it.
  It also empties `Kubeconfig.EnvironmentSearchOverride`, and that one was a live
  bug rather than hygiene: a test that builds `MainWindowViewModel` read the
  developer's real `~/.kube/config` and, with no saved tabs, opened one on its
  current context — a real connect, credential plugin included, from inside a unit
  test. Its async restore also wrote the workspace after the test had moved on,
  which made the shell-mode tests fail or pass depending on which class ran first.
  A `[ModuleInitializer]` runs the redirect once when the assembly loads, so a test
  that forgets to call it still cannot reach real files. What the helper does **not**
  give is isolation from a parallel test — both overrides are process-wide statics —
  so a test that writes a setting or the workspace and reads it back is
  `[NotInParallel]` and redirects in a `[Before(Test)]` hook, before the body writes
  (ENG-37). `App`'s settings store used to be a static field that fixed its path on
  first use, which quietly shared one `settings.json` across every test after the
  first; it resolves the path per call now, and `TestStoreRedirectTests` pins that.
- **Its limit is a running Avalonia application, and the harness is where that lives.**
  Contracts that need a real window — the window's key bindings following the
  Ctrl/Cmd scheme (VER-19), the exec terminal's bytes for ^C/^D/Tab (ENG-20) — are
  `ux-` checks in `tools/Screenshot/KeyboardChecks.cs`, and what an AvaloniaEdit
  editor draws (no links; only a laid-out editor has visual lines) is checked in
  `EditorChecks.cs`. They throw and fail CI's render step like the other `ux-`
  checks. One Avalonia.Headless host rather than a
  second one bolted onto this project; the shell view model's own half of VER-19 is a
  plain test here (`ShellHotkeySchemeTests`).
- **The screenshot harness cannot replace it, and that is the whole argument.**
  `Rows` and `VisibleRows` agree with each other in every state a PNG can capture;
  the difference between a correct mirror and one that filters `Rows` in place only
  shows on the *next* watch event. That is not a rendering property, so it needed a
  different kind of check.

### `dotnet test --project` is broken on this machine (SDK 10.0.400-preview)

`dotnet test --project tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj`
reports **"Zero tests ran", exit code 5**, and it does so on a clean checkout of
the checkpoint commit too — this is the local SDK
(`10.0.400-preview.0.26322.102`), not a regression in the suite. Running the
test executable directly works and is what these 137 results come from:

```powershell
tests/KubeNimbus.Core.Tests/bin/Debug/net10.0/KubeNimbus.Core.Tests.exe
```

CI pins `10.0.100` via `global.json` and still uses `--project`, so it is
unaffected — but if a local run ever looks suspiciously green *or* suspiciously
empty, check the invocation before the code. This is the second distinct way
`dotnet test` has silently run nothing in this repo; the first (a positional
csproj, exit 0) is documented under Verification workflow.

**`scripts/test.ps1` / `scripts/test.sh` make the working invocation the easy one**
(ENG-2): build, then run both test executables directly, and fail a run in which a
suite reports zero tests (the runner's exit code 8) — unless a filter was passed, when
only "every suite ran nothing" fails. Runner arguments pass through
(`-RunnerArgs '--treenode-filter','/*/*/DemoRowsTests/*'`, or after `--` in bash).

### The launch check (`--smoke-test`)

**A publish that emits no warnings is not a binary that starts, and this repo has
the receipts.** `Icon="/Assets/app.ico"` published perfectly cleanly on every RID —
same two DataGrid warnings, exit 0 — and then died before the first frame with
`FileNotFoundException: The resource /Assets/app.ico could not be found` out of
`IconTypeConverter.CreateIconFromPath` (see `WindowIcons`). Because `ci.yml`
published the AOT output and never ran it, and `release.yml` published four RIDs and
never ran any of them, **v0.1.0 shipped four release binaries — every RID — that could not
launch**, and nobody found out from CI. That is what this check exists to stop, and
it is the reason "publishes cleanly" is never again allowed to stand in for "works".

`kubeNimbus --smoke-test` (`src/KubeNimbus.App/SmokeTest.cs`) starts the app the
ordinary way and exits **0 only after the main window has opened and composited a
frame**. Anything else is a distinct non-zero code: 64 no MainWindow, 65 a frame
rendered but the window is hidden or 0×0, 66 startup threw, 67 the watchdog expired, 68 the
unreachable-cluster scenario's kubeconfig could not be built into a client
configuration (see below), 69 that scenario's failed connect left no failure view in the
content area.
Five things about it are deliberate:

- **It lives in the app, not beside it.** A GUI process never exits on its own, so an
  external checker needs both a way to end it and a way to see a window — and that is
  a different tool per platform (`xdotool` on X11, `MainWindowHandle` polling on
  Windows, scripted Accessibility on macOS, which a runner will not grant). One flag
  is uniform across all four shipped RIDs and adds no packages.
- **It observes the window the app already built**, from `App
  .OnFrameworkInitializationCompleted`; it never constructs one of its own. A check
  with its own startup path is a check that can pass while the real path is broken.
- **The verdict is the exit code**, not the log line. `kubeNimbus` is `WinExe`
  (GUI subsystem), so on Windows stdout only exists if the parent supplied a handle —
  the `SMOKE-OK`/`SMOKE-FAIL` lines are for reading a red job, not for deciding it.
- **The assertion happens inside `RequestAnimationFrame`, not in `Opened`.** `Opened`
  fires before layout and render, so a size check there reads a window that is
  legitimately still 0×0. Requesting an animation frame schedules a compositor tick
  and calls back after it, which is what makes "a window appeared" an actual claim.
- **The watchdog is armed in `Run`, before `StartWithClassicDesktopLifetime`** — not
  in `Attach`, which is the obvious place and is wrong. `Attach` runs inside framework
  initialization, so a hang in platform detect, `App.Initialize`'s XAML load or a
  static constructor would never arm it and would sit on the runner until the job
  timeout. It is a pool-thread `Timer` calling `Environment.Exit`, because the failure
  it has to survive is a wedged UI thread and a `DispatcherTimer` would be wedged
  with it. Verified by running with `KUBENIMBUS_SMOKE_TIMEOUT_SECONDS=1` against a
  ~1.4 s Debug start: `SMOKE-FAIL (67) no window after 1s (last stage: process
  started)`.

**Where it runs.** `ci.yml`'s `aot` job runs it on the linux-x64 output under Xvfb —
Xvfb rather than headless, so the backend under test is the X11 one a user gets.
`release.yml` runs it on **every** RID, on that RID's own runner (NativeAOT cannot
cross-compile, which is why the matrix already has one runner per RID), **before**
staging: a binary that cannot start must fail its leg rather than be archived,
checksummed and attached to a public release. The Windows leg uses `Start-Process
-Wait -PassThru` and not `&` — PowerShell does not wait for a GUI-subsystem child
invoked with the call operator, so `$LASTEXITCODE` would be meaningless and the step
would pass unconditionally. Avalonia's X11 backend dlopens exactly seven native
libraries (`libX11`, `libXext`, `libXrandr`, `libXi`, `libXcursor`, `libICE`,
`libSM`); both Linux workflows install them alongside `xvfb`.

**A runner has no workspace, so the plain check never connects to anything — hence
`--smoke-test=unreachable-cluster`.** The plain run reads whatever workspace and
kubeconfig the machine has. On a runner that means none, so no tab is restored and no
connect runs. That is how a win-x64 NativeAOT hang shipped past CI and past every
release leg: `ClusterTabViewModel.ConnectAsync` called the synchronous
`ClusterClient.Connect`, which built the client with the library's
`BuildConfigFromConfigFile`, and that method is sync-over-async
(`.GetAwaiter().GetResult()`). On the UI thread, which is an STA, NativeAOT parks that
wait in `CoWaitForMultipleHandles`. The kubeconfig read finished on a pool thread in
about a millisecond, and the UI thread still never woke. That was 8 launches in 10 on
a developer machine with one restored tab; the JIT build never hung. cdb showed where
the thread was stuck, and an instrumented build showed that the awaited task had
completed. The fix is `ClusterClient.ConnectAsync` /
`Kubeconfig.BuildClientConfigAsync`: the whole config build runs on the pool and the
UI thread only awaits it. The synchronous `Connect` stays for tests and tooling, and it
must never be called on the UI thread. The scenario seeds a kubeconfig pointed at
`https://127.0.0.1:1` and a workspace that restores it, in a temp directory, with both
stores redirected. It passes only after the tab reports `Connection failed` and a frame
has composited after that. The watchdog stays armed until then, and the stage it
reports names the tab status it last saw. Against the unfixed build it failed 3 runs in
6 with exit 67. Both CI's `aot` job and every `release.yml` leg run it after the plain
check. The installer legs do not, because those check packaging, not connect.

**"Connection failed" is also what a binary that cannot read any kubeconfig says**, so
the scenario does not stop at the tab's status. Once the tab has failed, it builds the
seeded context into a client configuration through `Kubeconfig.BuildClientConfigAsync`,
the same call the connect path makes, and exits **68** if that throws: only a failure at
the socket, after the configuration was built, is the failure it expects. The YamlDotNet
18 bump (#21) produced exactly the other kind — no cluster reachable from the binary at
all — and the window-only check passed it; what caught it was the test suite. Proved the
way the icon check was: with `BuildClientConfigAsync` made to throw, the tab reported
`Connection failed: simulated…`, which the old condition accepted, and the check now
exits 68.

**And the failure has to be stated on the page, not only in the status bar.** Before
the socket check, the scenario requires the tab's `ConnectionFailure` — the view that
takes the list's place and says which step failed, why, and with what (see
[connecting](connecting.md)). Missing, it exits **69**. Proved with a build
whose connect path computed the report and dropped it: `SMOKE-FAIL (69) the restored tab's
connect failed but the content area has no failure view to show`.

**The check is only worth having if a broken binary fails it, so prove that, don't
assume it.** Restore `Icon="/Assets/app.ico"` on `MainWindow`, publish, and run the
check: the publish succeeds with the same two DataGrid warnings and the check exits
66 with the historical stack trace. Revert afterwards. Doing this again is cheap and
is the only thing that distinguishes this from a step that always passes.

### NativeAOT publish needs the MSVC toolchain (Windows)

The ILCompiler links with `link.exe` and locates it via `vswhere.exe`. On this
machine the raw `dotnet publish -p:PublishAot=true` fails with
`'vswhere.exe' is not recognized` unless run from a VC dev environment **with the
VS Installer dir on PATH**. Working invocation:

```bat
call "C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Auxiliary\Build\vcvars64.bat"
set "PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"
dotnet publish src\KubeNimbus.App\KubeNimbus.App.csproj -c Release -r win-x64 -p:PublishAot=true -o publish\app
```

Known AOT warnings today: `Avalonia.Controls.DataGrid` emits IL2104/IL3053 trim
warnings. The publish still succeeds and the app runs; revisit if DataGrid gets
an AOT-clean release. Do not let *new* trim/AOT warnings from our own code slip
in unnoticed.

### DevTools / visual inspection

`KubeNimbus.App` references `AvaloniaUI.DiagnosticsSupport` **Debug-only** and
calls `WithDeveloperTools()` under `#if DEBUG`, so the Avalonia DevTools MCP can
attach to a running Debug build and screenshot/inspect the tree. It never enters
the Release/AOT build.

### Checking the running app (`kn-qa`, Windows)

**Since 2026-10-10 this runs only inside the owner's Hyper-V QA VM, never on the owner's own
desktop.** During the backlog sweep that month (#259), `kn-qa` runs drove the owner's real
desktop: the app launched there, the pointer and keyboard moved, and checks pressed Win+V,
Win+K and Snap Layouts and opened terminals and a browser. The owner decided that desktop
checks belong in a VM with Claude running inside it. The VM sets `KUBENIMBUS_QA_VM=1`;
`qa-app.ps1` and `qa-ui.ps1` refuse to start without it (`qa-app.ps1 -Stop` still works
anywhere), and `qa-ui.ps1 screenshot` captures one element cropped to its bounds, because a
run that read full 3840×1600 screenshots cost 316k tokens. Outside the VM, a desktop check is
written into the PR body as a numbered check for the VM.

The screenshot harness renders views bound to fixtures and the view-model tests drive
view models; neither exercises real input, a real window or a real API server's timing.
Those halves are most of the backlog's verification debt ("driven by a real mouse",
"against a real API server"), and they are *scripted* checks with a plain expected
state — work a cheap model can do. So there is a third kind of check:

- `scripts/qa-app.ps1` starts a Debug build on an **isolated profile** and against the
  local sandbox only, and `-Stop` ends it. `KUBENIMBUS_PROFILE_DIR` (read in
  `Program.ApplyIsolatedProfile`) points settings and workspace at a fresh directory
  and restricts the kubeconfig search to `$KUBECONFIG` alone. Without it a launched
  Debug build restores the developer's own tabs and lists their real contexts, and an
  automated check that presses Delete or Drain would press it wherever the developer
  last was. The script also refuses any kubeconfig whose `server:` is not loopback.
- `scripts/qa-ui.ps1` drives it through **Windows UI Automation**: Avalonia publishes
  its control tree there, so every element's type, name, `x:Name`, enabled/selected
  state and bounds come back as one line of text. Pattern actions (`invoke`, `select`,
  `set-text`) do not touch the mouse; `click`/`double-click`/`right-click`/`keys` are real
  OS input and move the user's pointer, so they are for checks that are *about* real
  input. The DevTools MCP would do this too, but it is not available on every
  subscription, and UIA needs nothing but Windows.
- `kn-qa` (`.claude/agents/kn-qa.md`, Sonnet) takes a list of checks and returns PASS /
  FAIL / UNSURE, quoting what it observed. No Edit or Write tool, same reason as
  `kn-verifier`. Sonnet rather than Haiku by the owner's call: a false PASS is the one
  failure this agent must not have, and its cost outweighs the price difference. UNSURE
  is still the honest answer for anything that needs visual judgement; the orchestrating
  session looks at those itself.

One instance at a time — one desktop, one pointer — so QA runs are never parallel, and
they do not run in CI or the cloud (no desktop there). The accessibility tree is a
finding source of its own: icon-only buttons currently report their accessible name as
`Avalonia.Controls.PathIcon` (shown as `<unnamed>`), which is ENG-4's problem made
measurable.

### Headless screenshot harness (`tools/Screenshot`)

For environments with no display and no DevTools MCP (Claude Code Cloud
sessions, CI) — renders real Views bound to fixture ViewModels via
`Avalonia.Headless` (Skia software rendering, `UseHeadlessDrawing = false`)
and dumps PNGs. Not part of the shipping app; excluded from the App's
NativeAOT publish.

```bash
dotnet run --project tools/Screenshot -- <outputDir> [scenario-name-substring]
```

Writes one `<scenario>.<light|dark>.png` per scenario × theme to `outputDir`
(pass a scratch dir — nothing under it is committed). Omit the filter to
render every scenario in `Program.cs`'s `scenarios` array.

Key structural point: a `ClusterTabView` (or any inspector tab view) screenshot
must be hosted inside a real `MainWindow`, not a bare wrapper — `ContentControl`'s
implicit `DataTemplate` lookup only resolves `PodDetailView`/`YamlEditorView`/etc
by walking the visual tree to `MainWindow.axaml`'s `Window.DataTemplates`; a
bare `Border`/`Window` wrapper falls back to a `ToString()`-in-a-TextBlock
placeholder instead of the real view. See `HostInMainWindow` in `Program.cs`, which
also puts the window in the Resources mode unless a scenario asks for Applications — every
cluster-tab scenario is about the explorer, and the shipped default is the other mode.

Fixture data is the demo cluster's own dataset (`src/KubeNimbus.App/Demo/Fixtures/*.json`
— pods, deployments, events, a 72-kind CRD catalog spanning
cert-manager/argoproj/istio/velero/keda/flux/etc to stress-test sidebar scaling
realistically), which `FixtureData.cs` passes through as real
`DynamicResource`/`ResourceDescriptor` instances; `tools/Screenshot/Fixtures` now holds
only the offline kubeconfig. `ClusterTabScenarios.cs`
builds fully-populated `ClusterTabViewModel`s by setting the same public
properties `ConnectAsync`/`RestartWatch`/`Apply` would, using an **offline
`ClusterClient`** (`FixtureData.CreateOfflineClient()`, pointed at
`Fixtures/kubeconfig-fake.yaml` → `https://127.0.0.1:1`, an address nothing
listens on) so ViewModel constructors that require a live `ClusterClient`
(pod detail's event refresh, exec's connect) still work — those calls just
fail fast in the background and are swallowed by the same error handling a
real lost connection already has.

Gotcha already hit once: setting `SelectedNamespace` on a fixture
`ClusterTabViewModel` fires the real `OnSelectedNamespaceChanged` → `RestartWatch()`
hook. With no `Client` wired up that only touches `IsListLoading`/`IsListEmpty`,
but if you set `SelectedNamespace` *before* manually populating `Rows`, the
empty-state flag latches `true` and never gets recomputed (production code
never hits this ordering — there, `RestartWatch`'s background pump is what
populates `Rows`). `ClusterTabScenarios.BaseTab()` recomputes `IsListEmpty`
after populating rows for exactly this reason; follow the same pattern for
new scenarios that set view-model properties directly. The sidebar highlight is the
other thing never to set by hand: assign `SelectedKind` and `MarkSelectedKind` lights
the row (ENG-26 — two scenarios had drawn two kinds selected at once).

**Two runs of one commit must produce the same PNGs, and the harness now makes sure of
the four things that stopped that (ENG-10).** Measured before the fix: 9 of 266 PNGs
differed between two runs of the same build. (1) Every scenario builds a real
`MainWindowViewModel`, which read the machine's kubeconfig chain and connected to its
current context — `main-window-no-kubeconfig` rendered the developer's own sandbox pods
whenever the connect landed before the capture, and never in CI, which has no kubeconfig.
`Program.cs` sets `Kubeconfig.EnvironmentSearchOverride = []`. (2) Log streams run on
real timers — the demo replay and the offline client's failing follows — so a capture
took whatever line count and Follow state the clock had reached. `LogSettle` waits,
before every capture, until each pod-detail stream has stopped following and each
aggregated pane's sources have ended. (3) **Every capture starts with no `settings.json`
and no `workspace.json`** in the scratch directory, because anything a scenario persists
otherwise reaches every scenario rendered after it, and a full run then disagrees with a
run of the one scenario. The reset used to clear named workspace fields (grid layouts,
Recent kinds) and missed a preference: `cluster-tab-events-list` expands Config on a demo
tab, the demo tab's sections persist their expansion to `settings.json`, and the 281 PNGs
rendered after it — the published `store-*` set among them — showed Config open where the
default is collapsed, while a single-scenario run showed it closed. Deleting both whole
files means the next persisted preference cannot leak the same way. The check for this
class is a full run against single-scenario runs (`… -- <dir> <scenario>`), byte for
byte: after the fix, 50 of 50 PNGs across the affected scenarios matched, and 46 of them
had differed before. (4) The scratch
directory itself was one fixed name under `%TEMP%`, so two harness runs at once — two
worktrees, two agents — wrote each other's `settings.json` mid-render; measured, that
alone made 189 of 272 PNGs differ between two runs (sidebar sections expanded in one and
collapsed in the other). Each run now gets its own directory and removes it at the end.
**The merged log panes were the last of them (ENG-53).** Panes that merge several replayed
streams (`cluster-tab-workload-logs*`, `applications-page-crashloop-merged`,
`applications-page-rollout`, and `ux-logs-palette`, which ends on a workload's logs) ordered
lines by which flush tick they arrived in, so those PNGs differed between runs. The demo now
replays every stream of a pane from one loop in the order the lines were logged
([multi-pod-logs](multi-pod-logs.md)), and `DrainWorkloadLogs` waits for every
stream to end before a scenario sets its search, which had landed on whichever match was newest
part-way through. Measured: two full runs of one build, back to back, matched in all 418 PNGs, and three
filtered runs of the workload-log scenarios matched each other byte for byte. What still moves
is the clock: the demo's Age column and a restart's "71d ago" are relative to now, so a byte diff
across a day boundary flags them, which is not a regression.

When Docker is available (unlike this session — `docker version` succeeds but
`dockerd` isn't running here), prefer driving the harness against a real
k3s sandbox (see below) instead of fixtures for a final verification pass;
note in the PR which screenshots were fixture-only.

The harness is also **CI's XAML smoke test**. A build that compiles can still
fail to load XAML at runtime — a stale `avares://` URI, a missing embedded
resource, a `DataTemplate` that stops resolving — and rendering every View is
the only check that catches that without a display. `SeedContexts` in
`Program.cs` fills `MainWindowViewModel.AvailableContexts` so the command bar
reads a real context name rather than "No kubeconfig contexts"; that is a real
state, but it is not what these scenarios are about and it makes every shot
look like a failed connection. The harness sets `Kubeconfig.EnvironmentSearchOverride`
to empty for the same reason the stores are redirected: every `MainWindowViewModel`
reads the kubeconfig chain and opens a tab on the current context, and on a developer's
machine that was a live connect landing on top of the scenario — the no-kubeconfig shot
rendered "Connecting to kubenimbus-sandbox…" over its own empty state. CI has no
kubeconfig, which is why it never showed there.

Its PNGs upload as a CI artifact **only when the render step went red**
(`if: failure()`, `if-no-files-found: ignore`, `retention-days: 3`). That is
not stinginess about disk — see the Actions storage budget below — it is what
the artifact is for: a green render is a smoke test that passed, and nobody
has ever downloaded 58 PNGs to confirm it. `if-no-files-found` has to be
`ignore` rather than `error` precisely because the common failure is the
render throwing, which leaves the directory empty; a missing diagnostic must
not turn one red step into two.

### The stress mode (`tools/Screenshot -- --stress`)

**A fixture of a dozen rows is fast whatever the code does**, which is how two freezes
shipped past every screenshot. The log panes' line lists did not virtualize, so a full
4,000-line buffer was about 37,000 live controls — seven seconds of layout, and every theme
switch restyled all of them — and their scrollback trim was a `RemoveAt(0)` per line, 396,000
collection notifications for one "Everything" flush. A read-only audit then found the same
class in the Argo Resources tab, the apply-preview diff, the Service pane's initial list,
the list search and the CPU-sorted metrics poll. `StressChecks` feeds each of those surfaces
what a large cluster produces (5,000 pods, 200,000 log lines, 3,000 Argo resources, a
5,000-key diff, a 1,000-pod Service) through the same entry points the watch and the flush
timer use, then switches the theme with it all on screen.

Three rules:

- **The budgets rest on counts, not on time.** Visuals left in the window (a list that does
  not virtualize holds a row of controls per item) and notifications the watched collection
  raised (a list rebuilt item by item raises one per item) are the same on every machine.
  Time has a budget too, loose enough for a slow runner, to catch seconds where there should
  be milliseconds.
- **A list bound to cluster data virtualizes, and is rebuilt with one notification.** An
  `ItemsControl` gets a `VirtualizingStackPanel`; a wholesale change goes through
  `RangeObservableCollection` (`AddRange`, `RemoveFromFront`, `ReplaceAll`) rather than a
  `Clear()` and an `Add` per item. A new surface of that kind gets a check here.
- **It runs in CI** after the render in the XAML smoke test job, and was proved against the
  code before each fix: see the pass logs in `docs/status-history.md`. A check's label
  states what it actually loaded ("search keystroke over 2,010 apps"), because a check
  that quietly measured an empty list would pass every budget.
