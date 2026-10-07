# Release checklist

The list to walk before every public release, kept current as releases teach us
things. [`PRE-LAUNCH-CHECKLIST.md`](PRE-LAUNCH-CHECKLIST.md) was the one-time list for
going public; this one is for every release after it. It is adapted from pgNimbus's
`docs/RELEASE-CHECKLIST.md`, and the two should keep the same shape so that someone who
releases both walks one list twice rather than two lists.

How to use it: copy the checkbox sections into the release PR description (or tick them
in a scratch copy), walk them top to bottom, and add a row to the
[release log](#release-log) at the end. When a release finds something this list would
have caught earlier, change the list in the same PR. A step that nobody can do in the
time a release allows gets automated or deleted, not skipped silently.

How it relates to the release train: `/release-train`'s HARDEN phase already runs most
of section 2 (the regression sweep and the performance gate) on the train branch, and
its RELEASE phase does section 5. What the train cannot do from a cloud session is
section 3, the manual pass on a real window against a real cluster. So a train release
walks sections 1, 3, 4 and 6 here and points at the train's `verification.md` for the
rest; a release cut by hand, outside a train, walks all of it.

Most of the manual pass can be handed to Claude Code: "go through
docs/RELEASE-CHECKLIST.md for vX.Y.Z". The scripted steps (test runs, the hygiene
sweep, release-note drafting) suit a Sonnet subagent; the click-through suits `kn-qa`
for the checks that have a plain expected state, and a model that can look at the
window for everything that needs visual judgement.

---

## 1. Scope and version

- [ ] `git fetch --all --prune`, then `git log --oneline <last-tag>..origin/main`.
      Nothing half-merged: a bundle or a train is either all in or all out, and no
      open PR is something this release's notes already describe.
- [ ] **Every `## [X.Y.Z]` heading in `CHANGELOG.md` has a tag.** A section that was
      written and never tagged is a release nobody received. 0.5.0 is the case that
      put this line here: its section, its `VersionPrefix` bump and its release PR
      (#98) all merged on 2026-09-26, and no `v0.5.0` tag was ever pushed, so the
      Applications mode sat on `main` unreleased. Check with
      `git tag -l "v*"` against `grep "^## \[" CHANGELOG.md`. An untagged section is
      folded into this release's section, not tagged after the fact from a later
      commit, because its binary would carry changes the section does not list.
- [ ] Pick the version. A new user-visible feature is a minor bump, fixes only a
      patch bump. The version is written in **one** place, `<VersionPrefix>` in
      `Directory.Build.props`; bump it in the release PR. A tagged build overrides it
      with the tag, so the two cannot disagree in a shipped binary, but a local
      publish uses the checked-in value, which is why it has to be bumped anyway.
- [ ] Rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD`, open a fresh empty
      `Unreleased` above it, and update the link references at the bottom of the file
      (`[Unreleased]` compares from the new tag; add a line for the new version). The
      release workflow lifts this section verbatim as the release body, so it is the
      release notes.
- [ ] Rewrite the section for users: grouped Added / Changed / Fixed, outcomes rather
      than implementation, and nothing about CI, agents or the build. Run it through
      the `humanizer` skill (no em or en dashes). Check the standing "Known
      limitations" list at the end of the file is still true.

## 2. Automated gates

- [ ] The release PR's `Build & test` is green, and so are `XAML smoke test` and
      `NativeAOT publish (linux-x64)`, which run on it but do not hold the merge:
      `gh pr checks <n>`. There is no CI run on `main` itself (see the `release`
      skill), so the release PR is where two PRs that each passed alone are first
      built together.
- [ ] No open bug that is a crash, data loss, broken install, an unintended write to
      a cluster, or a security issue: `gh issue list --state open`. Triage anything
      new since the last release.
- [ ] No open security alerts: Dependabot
      (`gh api repos/Shman4ik/kubeNimbus/dependabot/alerts?state=open`) and secret
      scanning. No pending Dependabot PR marked as a security update. `NuGetAudit`
      already fails the build on a moderate or worse advisory.
- [ ] Both test suites pass **against a live cluster**, with the cluster-gated tests
      run and not skipped:
      ```powershell
      ./scripts/sandbox-up.ps1 -Recreate            # or -Wsl, see "The stand" below
      $env:KUBENIMBUS_TEST_KUBECONFIG = "$PWD/.sandbox/kubeconfig.yaml"
      ./scripts/test.ps1
      ```
      `scripts/test.ps1` rather than `dotnet test --project`, which on the local
      preview SDK reports "Zero tests ran" (see CLAUDE.md). Read the summary's
      **skipped** count: with the sandbox up, a skipped live test is a test that
      did not run, and each skip names its reason. A release that could not run
      them says so in its log row.
- [ ] The NativeAOT publish emits no warnings beyond the known DataGrid pair
      (IL2104/IL3053), and `dotnet build KubeNimbus.slnx` adds none of our own.
- [ ] The screenshot harness renders every scenario, including the `ux-` checks:
      `dotnet run --project tools/Screenshot -- <scratch>`. It is CI's XAML smoke
      test; locally it is also where the published screenshots come from (section 4).
- [ ] `docs/keyboard-shortcuts.md` is current: `ShortcutDocsTests` passes, which it
      did in the suite run above.
- [ ] The privacy claim in `SECURITY.md` still holds: the app connects to nothing but
      the API servers of the contexts you open (and a `proxy-url` they name).
      `grep -rnE "new HttpClient|WebRequest|HttpClientHandler|SocketsHttpHandler" --include=*.cs src`
      finds only comments and `KubeconfigProxy.cs`; any new hit is read before it
      ships. (The build itself sends Avalonia's build-time telemetry, see
      [known caveats](#known-caveats); that is not the app.)
- [ ] `PRIVACY.md` is still true of this build, because it is the page the Store
      listing links to. `grep -rn "Process.Start" --include=*.cs src` lists every
      address the app hands to a browser, and each one is named in the policy. The
      policy's table of stored files matches what `AppSettings`, `WorkspaceSettings`,
      `DiscoveryCache` and `TerminalLauncher` actually write.
- [ ] Performance: time to first frame and executable size against the last
      release's figures. `--smoke-test` 3×, median; more than 10% worse on either is
      explained in the release notes or fixed. The README says "opens in ~150 ms"
      and "~62 MB payload", so both have to stay true.

## 3. Manual pass on the shipping build

Test the NativeAOT publish, not a Debug build: trimming and AOT fail in ways the JIT
never shows (a reflection binding, a sync-over-async wait on the UI thread that only
NativeAOT's STA wait turns into a hang, an `avares://` resource).

- [ ] Publish it (from PowerShell; Git Bash fails at link on `vswhere.exe`):
      ```powershell
      $env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
      dotnet publish src/KubeNimbus.App -c Release -r win-x64 -p:PublishAot=true -o publish/app
      publish/app/kubeNimbus.exe --smoke-test
      publish/app/kubeNimbus.exe --smoke-test=unreachable-cluster
      ```
      If `link.exe` is not found either, run it from a VC dev prompt as CLAUDE.md
      describes. Run both launch checks in a shell where `KUBENIMBUS_PROFILE_DIR` and
      `KUBECONFIG` are **not** set: the profile override narrows the kubeconfig search
      to `$KUBECONFIG`, which hides the kubeconfig the unreachable-cluster scenario
      seeds, and the check then fails with exit 67 on a binary that is fine (0.5.0's
      pass hit exactly this). Check `$LASTEXITCODE` through `Start-Process -Wait -PassThru`, not
      the call operator: the exe is a GUI-subsystem program and PowerShell does not
      wait for it.
- [ ] Run it on an **isolated profile**, never on your own:
      ```powershell
      $env:KUBENIMBUS_PROFILE_DIR = "$env:TEMP\kubenimbus-release"
      $env:KUBECONFIG = "$PWD\.sandbox\kubeconfig.yaml"
      publish/app/kubeNimbus.exe
      ```
      The profile directory takes the settings and the workspace, and the kubeconfig
      search is narrowed to `$KUBECONFIG` alone, so your own tabs and contexts never
      appear and a Delete or a Drain in the pass cannot land on a real cluster. The
      discovery cache still goes to `%LocalAppData%\kubeNimbus`; that is harmless.
      Delete the profile directory between runs that need a first launch.

**The stand.** The sandbox from `scripts/sandbox-up.ps1` is single-node k3s with the
demo manifests applied: `demo-shop`, `demo-data`, `demo-batch`, a `demo-broken`
namespace of crash-looping, unpullable, unschedulable and never-ready pods, three CRDs
(two of them a `Widget`), RBAC subjects, a three-revision Helm release, and Argo CD's
CRDs with Applications but no controller (so a Sync is accepted and nothing syncs).
k3s ships metrics-server. It needs Docker: Docker Desktop, or Docker Engine inside a
WSL2 distro with `-Wsl`. `wslc` **cannot** run it, because it has no `--privileged`
and k3s dies with `failed to evacuate root cgroup: mkdir /sys/fs/cgroup/init:
read-only file system`. For the fleet check (flow 20) bring up a second one with
`-Name kubenimbus-sandbox-2 -Port 6551 -Kubeconfig .sandbox/kubeconfig-2.yaml` and put
both files in `$KUBECONFIG`.

Walk each flow; the expected result is what "pass" means. Mutations happen in the
`demo-*` namespaces, which the next `sandbox-up.ps1` re-applies.

| # | Flow | Pass when |
|---|------|-----------|
| 1 | First launch with `KUBECONFIG` set to a missing file and a fresh profile | The empty state names every path it searched, Rescan is enabled, and "Explore demo cluster" opens a tab reading `Demo cluster` with the demo bar above the content |
| 2 | Demo cluster: open a pod's logs, Exec, and Edit YAML | Logs stream from the canned set; Exec and Apply say in place that they need a real cluster, and their commands are disabled |
| 3 | First launch against the sandbox | The kubeconfig's `current-context` opens on **Applications**, loading says what it is waiting for, and the `demo-broken` apps sort first with a one-line reason each |
| 4 | Enter on a crash-looping application | The page shows findings with the field each was read from, pods, linked Services and ConfigMaps, the timeline, and logs opened on the last run ending with its exit code |
| 5 | Restart and Sync on the application page | Each arms the confirm strip and does nothing until confirmed; Restart rolls the pods, Sync is accepted |
| 6 | Ctrl+Shift+R to Resources; sidebar filter `svc`, then `widget` | The mode switch is remembered on relaunch; `svc` finds Services, `widget` shows both groups' Widgets |
| 7 | Pods in all namespaces: Ctrl+F `api`, then the Unhealthy only chip (Ctrl+Z) | The list narrows by name and namespace, never by status; each narrowing that matches nothing has its own empty state naming the query |
| 8 | Namespace picker (Ctrl+Shift+N): type part of a name, Enter | Filters as you type, commits only on Enter, and the namespace shows first in Recent next time |
| 9 | L on a running pod, Shift+L, Esc | Logs follow; Shift+L opens them maximized; Esc returns to the split without leaving the list |
| 10 | Log search: a regex (Alt+R), `!healthz`, Levels, Alt+↑ | Matches highlight with "n of m", the exclusion says how many lines it hid, Levels keeps unleveled lines, Alt+↑ jumps to the latest error |
| 11 | P on a crash-looping pod | The previous run's log, ending where it exited; not a live follow |
| 12 | L on a Deployment, and Ctrl+Shift+L | One merged stream from every pod with a pod column; the palette lists the namespace's pods and workloads under `logs ` |
| 13 | S on a pod: `ls`, Tab, Ctrl+C on `sleep 100`, Ctrl+Shift+C/V | Output renders with colour, Tab completes, Ctrl+C interrupts, the clipboard pair copies and pastes |
| 14 | F on the `demo-shop` web pod, then open `http://localhost:<port>` | The pane reads local to pod, the page loads, Start and Stop are one slot |
| 15 | E on a ConfigMap: change a value, Apply | The dry-run preview shows the diff before anything is written; Apply writes it and the list updates live |
| 16 | E on a Secret: Reveal values | Values stay masked until Reveal; a TLS Secret shows its certificate's subject and expiry without a Reveal |
| 17 | S (scale) and R (rollout restart) on a Deployment, then Delete on a pod | Each arms the strip naming the object; after confirming, replicas change, pods roll, the pod is replaced |
| 18 | Nodes: open the node, Cordon, Uncordon, Drain | Detail shows allocatable and usage; cordon and uncordon flip the row; Drain shows its eviction plan and is **cancelled**, never run (it would evict CoreDNS) |
| 19 | CronJobs in `demo-batch`: Run now, Suspend, Resume | A Job appears and its pods open; Suspend and Resume flip the column |
| 20 | Fleet: two sandboxes connected, toggle the fleet view | One list with a Cluster column; each cluster's rows update from its own watch |
| 21 | Services in `demo-broken`, an Ingress, a NetworkPolicy | The typo'd selector, the not-ready endpoint and the ExternalName each read as their own sentence; the default deny reads as denying every pod |
| 22 | Events, Widgets (CRD printer columns), Helm, Argo, Access review | Events newest first with Last seen; the Widget columns and the no-columns CRD; the Helm release's three revisions; Argo sync and health pills; "who can" lists the RoleBinding-bound ClusterRole |
| 23 | Ctrl+P switcher, a second tab, drag to reorder, Ctrl+2, then quit and relaunch | Tabs reopen in order, each on the kind and namespace it showed, the front tab in front |
| 24 | Kubeconfig with a context pointed at `https://127.0.0.1:1` | The content area shows the failure view with the step and cause, not a blank list or a status-bar line |
| 25 | Preferences, theme toggle, F1, About, the ☰ menu | Both themes readable, every overlay closes on Esc and on the scrim, the ☰ tail is Preferences / Keyboard shortcuts / About |
| 26 | Every documented chord in F1 (`docs/keyboard-shortcuts.md`) | Each one does what the sheet says |
| 27 | **The round trip, as the editor does it**: open an existing object from the list (E) and change one value without retyping the manifest, for a ConfigMap, a Deployment and a Widget; preview, Apply, then reload and Force apply | The object opens with the server's own fields in it (`managedFields`, `uid`, `status`) and each of the three paths still goes through; the list shows the new value. Flow 15 is the ConfigMap half of this. Apply on an opened object was broken in 0.5.0 and nothing caught it, because the live tests only ever applied manifests they had written themselves, and those never carry the server's own fields. A pass that applies text it typed is not this check. `ApplyLiveTests.An_object_read_back_from_the_server_can_be_previewed_applied_and_force_applied` is its automated half |
| 28 | Preferences, Appearance: Interface font System, then Inter; Code font (pick an installed one, then back to the built-in one) | Every open view changes at once, the log, YAML and exec panes included. **On a Mac**, System is San Francisco (a Finder window beside it has the same letters) and its spacing at 13px is neither cramped nor loose (`NimbusFonts.MacSystemLetterSpacing`); the CPU and memory columns' digits stay one per column. Code text is JetBrains Mono and `->>` stays three characters. `FontChecks` (`ux-font-settings`) is the automated half; how San Francisco looks is the part only a Mac shows |

Driving it with Claude Code: `scripts/qa-app.ps1` and `kn-qa` do the same isolation for
a Debug build and are the cheap way through the flows with a plain expected state; the
AOT build is then driven by hand or with computer-use for the rest. With computer-use,
grant the exe by its **full path**, type in chunks of 15 characters or fewer (longer
strings go through the clipboard and skip completion triggers), and read small UI by
capturing the window region rather than the downscaled screenshot. **Don't report a
chord as broken on the strength of one input tool.** On 2026-09-28 pgNimbus's Ctrl+,
"did nothing" through computer-use (which cannot send it) and through `SendKeys`
(whose Ctrl never reaches Avalonia), yet opened Preferences at once when sent as real
`keybd_event` key presses. Check a failing chord that way, or by hand, before it goes
in the log. Escape sent by computer-use does not reach the app either; the harness's
`ux-` checks cover Esc.

## 4. Published media

- [ ] Screenshots: UI design rule 21 says a PR that changes a surface re-renders its
      screenshots in the same PR, so this is a check, not a job. Render the harness on
      **Windows** and compare against `design/screenshots/` and
      `design/store/screenshots/`; each directory's README maps files to scenarios and
      themes. Anything that drifted is re-rendered and committed in the release PR.
- [ ] README: the screenshots it shows, the install instructions for each channel,
      and the limitations it states are still true of this release.
- [ ] Microsoft Store listing text. The source is
      `design/store/listing/store-listing.md`; update its "What's new in this version"
      for this release and paste the changed fields into Partner Center, which is where
      the listing actually lives. Check that the privacy policy URL in the listing
      opens the policy.

## 5. Ship

- [ ] Merge the release PR, then tag its merge commit and push the tag:
      ```bash
      git tag -a vX.Y.Z -m "kubeNimbus vX.Y.Z"
      git push origin vX.Y.Z
      ```
- [ ] Watch `release.yml` (`gh run watch`). Every RID's binary is launched before it
      is archived, and every package (the Windows zip unpacked, `.dmg`, `.deb`,
      `.AppImage`) is installed and launched through its own path, so a red smoke step is a real
      failure. A red leg is fixed forward as a patch release; the tag is never moved.
- [ ] The release page: the body is the CHANGELOG section plus the unsigned-binary
      footer, `SHA256SUMS.txt` is attached, no `.msix`, `.msi` or `.wixpdb` is attached, and the release
      carries the **Latest** label (a plain `vX.Y.Z` tag does; only a suffixed tag is
      a pre-release).
- [ ] Download one asset and check both the release attestation and the build
      provenance: `gh release verify-asset vX.Y.Z <file> --repo Shman4ik/kubeNimbus`
      and `gh attestation verify <file> --repo Shman4ik/kubeNimbus`.
- [ ] Microsoft Store: download the `windows-msix` artifact from the release run (kept
      14 days), upload it in Partner Center → kubeNimbus → Packages, and submit.
      The package identity is never edited to make an upload pass. Submit only once
      `PRIVACY.md` is on `main`: the listing's privacy URL points at it there, and 0.4.0's
      submission failed certification (policy 10.5.1) for want of a real policy.

## 6. After

- [ ] Install from the channels a user would: the zip from the release page, and the
      Store update once certified. Launch each and check About shows the new version.
- [ ] If this release came from a train, `/release-train`'s RECORD step moves
      `TRAIN.md` into `docs/product-loop/history/`. Either way, record the pass in
      `docs/status-history.md` if it found anything worth keeping.
- [ ] Clean up merged branches and worktrees (global CLAUDE.md "Git housekeeping"),
      including `claude/*` and `train/*` branches the release merged.
- [ ] Add the release to the log below with anything the pass found.

---

## Known caveats

Things that are true, known, and not release blockers. Keep them honest; they are what
gets asked in a launch thread.

- **Build-time telemetry.** Building from source pulls in `Avalonia.BuildServices`,
  which reports anonymous build statistics to Avalonia at compile time. It does not
  run in the shipped app, which still makes no network connection except to the API
  servers of the clusters you open. pgNimbus's publish log states that an opt-out
  needs a paid Avalonia tier.
- **Unsigned direct downloads.** The Windows zip and the Linux packages are unsigned and the
  `.dmg` is ad-hoc signed, so Windows shows SmartScreen and macOS quarantines the app.
  The Store package is signed by Microsoft, which is why the README sends Windows
  users there first.
- **macOS and Linux get less hands-on testing than Windows.** Every RID is launched in
  CI, but the manual pass above runs on Windows.
- **The sandbox needs Docker.** On a machine with only `wslc`, the live tests skip and
  the manual pass has no cluster; the release log row says so.

## Release log

| Version | Date | Pass by | What the pass found |
|---------|------|---------|---------------------|
| 0.5.0 | 2026-09-29 | Claude Code (Opus 5.5) + `kn-qa` | Folded the untagged 0.5.0 section and everything since into one release. Live suites green on the sandbox (Core 706, App 444; one Events test skips on a fresh cluster until the scheduler has written a series event). win-x64 first frame 357 ms median against 0.4.0's 374 ms, exe 61.6 MB against 59.0 MB. The unreachable-cluster launch check fails with exit 67 when the isolated-profile variables are set, now noted in section 3. A full harness run leaks the Config section's expansion into later scenarios; single-scenario renders match the published images. Store resubmission waits on `PRIVACY.md` reaching `main` (0.4.0 failed 10.5.1). Manual pass: the scripted flows on the AOT build by `kn-qa`; fleet (20) and the visual-judgement flows not walked. |
| 0.5.1 pre-release pass | 2026-10-01 | Claude Code (Sonnet 5.5) + `kn-qa` | **Blocker found and fixed: Apply on an object opened in the YAML editor was refused with `metadata.managedFields must be nil`** (preview, apply and force-apply alike), because the editor sends the server's own object back and every live test applied a manifest it had written itself; section 3 now has a round-trip row (27) for it. Also: Ctrl+S was on the F1 sheet with nothing bound to it (now bound, `ux-yaml-apply-key`); the preferences Theme drop-down went stale if the theme was toggled while the page was open; Windows UI Automation failed on the first application page opened in a session (the hidden `PageHost`); icon buttons, sidebar and Applications rows, preferences switches and the palette had no accessible names (names now come from tooltips, `AutomationChecks` gates it). Not defects: Alt+R is swallowed system-wide by another program on the test machine (a WinForms probe never received it; Alt+C and Alt+P arrive; the harness check passes), Ctrl+Z in the list's search box is that box's Undo (sheet wording fixed), CPU/Memory read dashes for at most one poll interval after a list opens (2 s to 12 s measured, ENG-55). Gates: Core 721/721 and App 464/464 against the live sandbox with 0 skipped (`Everything_already_queued_arrives_as_one_batch_in_order` failed 4 runs in 4 while the machine was busy with other work, on the baseline commit too, and passed on a quiet run); harness 356 PNGs and `--stress` green; NativeAOT win-x64 with only the DataGrid IL2104/IL3053 pair, both launch checks exit 0, exe 61.8 MB against 61.8 MB for the baseline commit, `--smoke-test` wall time median 452 to 457 ms baseline and 421 to 476 ms new when measured with `Start-Process -Wait` (no regression; the 357 ms of the 0.5.0 row is a different measurement). Manual pass on the AOT build by `kn-qa` plus by hand: flows 1, 4, 15, 16 (no TLS Secret in `demo-shop`, so the certificate half is unverified), 20 (two sandboxes: one list with a Cluster column, a delete on cluster 2 replaced only its own row), 24, 25, 26 (27 chords pass, Ctrl+S fixed, Ctrl+R, Ctrl+Shift+C and the Alt+Up/Down jump unobservable in the fixture) and the new 27. Not walked end to end: flows 2, 3, 5 to 9, 11 to 14, 17 to 19 and 21 to 23 (their chords were pressed under 26; flow 10 was walked, with Alt+R excluded). |
| 0.6.0 | 2026-10-04 | Claude Code (Sonnet 5.5) + `kn-qa` | Replaces the stale release PR #129 (it conflicted after the capsule, font and Windows zip work); the section now covers everything on `main` since 0.5.0. Live suites green on a fresh sandbox (Core 765/766 with the one known fresh-cluster Events skip, App 475/475). Harness 366 PNGs with every `ux-` check (0 dead tooltips, 0 unnamed controls, 0 font failures) and `--stress` green. NativeAOT win-x64 exe 62.4 MB against 61.8 MB for 0.5.1's pass, both launch checks exit 0, `--smoke-test` wall time 422 to 512 ms (median 438). Privacy greps unchanged: the only `HttpClient` is `KubeconfigProxy.cs`, `Process.Start` only opens addresses the policy names. No open issues, Dependabot or secret-scanning alerts. **Published screenshots had drifted**: 9 of the 10 README images were stale against the current look and Segoe UI, so all 10 and the 8 Store images are re-rendered on Windows in this PR. In the 1280×800 Applications hero the last row is clipped under a scroll bar with the system font (rows are taller than in the Inter render). Manual pass on the AOT build by `kn-qa` (flows 3 to 9, 17 to 19, 21, 22, 24, 25): no failures. Chords sent as keys (Ctrl+Shift+R, L, Shift+L, S, R) did nothing through UI Automation, so those gestures went through the menus and buttons and are not verified as keys; the mode being remembered across a relaunch was not checked (`workspace.json` held `ShellMode: Resources`); flow 6's and 9's other steps pass. Node cordon lasted about 40 s, not 1 s. Not walked: 1, 2, 10 to 16, 20, 23, 26 to 28 and the visual-judgement checks. |
