# Pre-launch checklist — making kubeNimbus public, cutting v0.3.0, shipping to the Microsoft Store

A one-time working document, in the same shape as pgNimbus's. Ordered so each
phase gates the next: don't flip the repo public until the hygiene items are
done, don't promote until there is something to download, and don't submit to
the Store until a real release exists to point people at.

Status when this was written (2026-08-27): the repository is **private**, has
**no git tags and no GitHub Releases**, no description and no topics.
Everything else — MIT LICENSE, CONTRIBUTING, SECURITY, CODE_OF_CONDUCT, issue
templates, a PR template, Dependabot, CI with the launch check, and a release
workflow — is already in place, which is why this list is much shorter than the
sibling project's was.

---

## Phase 1 — Repo hygiene (before flipping public)

The history becomes permanently public the moment the switch flips.

- [x] **MIT LICENSE present**, copyright holder correct.
- [x] **No secrets in the working tree** — grepped for
      api-key/secret/password/private-key literals. Every hit is a Kubernetes
      API term (`secretKeyRef`, the `Secret` kind) or obviously-synthetic
      fixture data.
- [x] **No secrets in git history** — no `.env`, key, certificate or kubeconfig
      file has ever been added. `.sandbox/` (which holds the local cluster's CA
      and client certs) has been git-ignored from the start.
- [x] **Agent worktrees ignored** — `.claude/worktrees/` is throwaway checkouts
      of this repo; added to `.gitignore` so it cannot be committed by accident.
- [ ] **Enable GitHub secret scanning + push protection** once public
      (Settings → Code security). A backstop to the manual grep above.
- [ ] **Accept that the personal commit email is in the history**
      (`shman4ik@gmail.com` is author and committer throughout). Normal for open
      source; if you would rather use GitHub's noreply address going forward,
      set `git config user.email` now — rewriting history is not worth it.
- [x] **Package metadata in `Directory.Build.props`** — `Product`, `Authors`,
      `Copyright`, `RepositoryUrl`, `PackageLicenseExpression`. This is what
      shows in the shipped binary's file properties.
- [ ] **Read `CLAUDE.md` and `docs/BACKLOG.md` once with public-reader
      glasses.** Both are engineering notes rather than marketing, which is fine
      and is what pgNimbus does — but they name unverified paths and open
      defects openly, so skim for anything that reads worse out of context than
      it does in it.

## Phase 2 — Quality gates

- [x] **CI on every PR and push** (`.github/workflows/ci.yml`): build, both test
      projects, the screenshot render as a XAML smoke test, and the linux-x64
      NativeAOT publish followed by an actual `--smoke-test` launch.
- [x] **Dependabot** for NuGet and GitHub Actions, Avalonia grouped.
- [x] **Vulnerability gate** — `NuGetAuditMode=all` with NU1902–NU1904 promoted
      to errors in `Directory.Build.props`.
- [ ] **Branch protection on `main`** — require the CI check, require PRs. Do
      this right after the repo is public, before anyone can open one.

## Phase 3 — Community scaffolding

- [x] `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, issue templates
      (the bug form asks for the Kubernetes distribution and whether the sandbox
      reproduces it), a PR template built from CLAUDE.md's rules.
- [ ] **Enable private vulnerability reporting** (Settings → Code security), or
      `SECURITY.md`'s link 404s.
- [ ] **Curate 5–10 `good first issue` candidates** from `docs/BACKLOG.md`'s
      Inbox. An empty issue tracker at launch reads as "not really open to
      contributors". The verification-debt rows are good candidates for anyone
      with hardware this project has never run on (a Mac, an arm64 Linux box).

## Phase 4 — First release (v0.3.0)

- [x] **`CHANGELOG.md` cut** — `## [0.3.0] - 2026-08-27`, with a note that 0.1.0
      and 0.2.0 were never published, so nobody hunts for downloads that do not
      exist.
- [x] **`<VersionPrefix>` bumped to 0.3.0.**
- [ ] **Dry-run the pipeline** — `workflow_dispatch` with `dry_run: true` builds
      and archives all four RIDs, runs the launch check on each, and packs the
      MSIX, without publishing anything. This is also the only way to find out
      whether the new MSIX step works on a hosted Windows runner, so do it
      *before* the tag rather than after.
- [ ] **Tag and push:**

      ```bash
      git tag -a v0.3.0 -m "kubeNimbus v0.3.0"
      git push origin v0.3.0
      ```

- [ ] **Check the release page** — four archives, `SHA256SUMS.txt`, the CHANGELOG
      section as the body, the unsigned-binary footer, and the pre-release flag
      (automatic for `0.x`).
- [ ] **Download and run one archive per platform you own.** The launch check
      proves the binary starts on a runner; it does not prove the zip you
      published extracts into something that starts on a real desktop.
- [ ] **Confirm the README screenshots still match the UI.** They are generated
      (`design/screenshots/`) and drift on their own, because the Age column is
      computed from the clock — regenerate only if the layout itself has moved.
      Screenshots are the first thing every visitor judges.
- [ ] **Code signing — decide, don't necessarily block.** The Store channel below
      buys the SmartScreen trust for $0; a purchased Authenticode certificate
      would additionally clean up the direct-download path. pgNimbus deliberately
      does not buy one. Make the same decision consciously, because "is it
      signed?" is the first question every thread asks.

## Phase 5 — Flip the repo public

- [ ] **Description** — "A fast, open-source Kubernetes desktop client (.NET +
      Avalonia, NativeAOT)".
- [ ] **Topics** — `kubernetes`, `k8s`, `kubectl`, `gui`, `desktop-app`,
      `avalonia`, `dotnet`, `csharp`, `native-aot`, `devops`. This is what GitHub
      search and topic pages index.
- [ ] **Social preview image** (Settings → General) — the dark-theme main window
      at 1280×640. It is what renders when the link is pasted anywhere.
- [ ] **Enable Discussions** — somewhere for "how do I…" that is not the issue
      tracker.
- [ ] **Make it public** (Settings → Danger Zone), then immediately verify:
      README renders, screenshots load, Releases shows v0.3.0, LICENSE is
      detected, the About sidebar looks right.

## Phase 6 — Microsoft Store

The product identity is already reserved: `DmitriiShmanev.kubeNimbus`, Store ID
`9MZ3C28M65PB` — the manifest carries it and it must not be edited. Mechanics
and reasoning are in CLAUDE.md, "Microsoft Store (MSIX)".

- [ ] **Download the `windows-msix` artifact** from the v0.3.0 release run
      (14-day retention — take it while it exists).
- [ ] **Verify the package locally before uploading.** On a Windows box, install
      it side-loaded after trusting the ephemeral certificate, and check that the
      taskbar and Start icons are the crisp unplated marks rather than a
      backplated square. A wrong `resources.pri` is invisible until exactly that
      moment.
- [ ] **Partner Center → kubeNimbus → Packages** — upload the `.msix`.
- [ ] **Store listing** — description, at least one screenshot (1366×768 or
      larger; the generated `design/screenshots/` shots qualify), search terms
      (`kubernetes`, `k8s`, `kubectl`, `cluster`, `devops`), and the support and
      privacy links (this repo's Issues, and `SECURITY.md` for the "no telemetry,
      no persisted credentials" claim).
- [ ] **Properties and age rating** — category Developer tools; declare **no data
      collection**, which is true and is what makes the questionnaire short.
- [ ] **The reviewer needs a working app with no cluster.** This is what the demo
      cluster exists for: a certification reviewer on a clean Windows machine has
      no kubeconfig and no Kubernetes anywhere. Confirm the no-kubeconfig empty
      state still leads with **Explore demo cluster**, and say so in the notes for
      certification so the reviewer does not have to find it.
- [ ] **Submit for certification** and expect 24–72 hours.
- [ ] **After it goes live**, add the Store badge and `winget install 9MZ3C28M65PB`
      (Store apps are reachable through winget's `msstore` source with no separate
      submission) to the README's Download section and to the release footer.

## Phase 7 — Promotion

Sequence matters: seed the quiet channels first and save the spike for when the
repo has a release, screenshots and a Store listing.

- [ ] **Write the pitch once.** The thesis is in CLAUDE.md's Mission and it is
      unusually defensible: KubeUI is the one true open-source native peer, and
      the difference is measured — ~156 ms to first window against ~645 ms, a
      ~62 MB payload against a 382 MiB single file, and no telemetry where theirs
      is on by default. Lead with a 20–30 s capture: connect → pod list → logs
      streaming → exec into a container → Ctrl+K.
- [ ] **awesome-kubernetes** and similar curated lists — permanent discovery.
- [ ] **r/kubernetes and r/devops** — read the self-promotion rules first, and be
      in the comments all day.
- [ ] **Hacker News "Show HN"** — weekday morning US time. Have answers ready for:
      why not Electron, how it compares to Lens/OpenLens/FreeLens/k9s/Aptakube/
      KubeUI, unsigned binaries, and the startup-time methodology
      (`docs/research/2026-08-17-kubeui-positioning.md` has the receipts).
- [ ] **r/dotnet, r/csharp, Lobsters** — staggered, angled per audience; the .NET
      crowd cares about the NativeAOT + Avalonia story more than the Kubernetes
      one.
- [ ] **Tag @AvaloniaUI** on X/Mastodon/Bluesky and post in their showcase channel
      — free reach into exactly the right audience.
- [ ] **CNCF landscape** — kubeNimbus fits the Kubernetes-tooling category; slow
      burn, but permanent.

## Phase 8 — Launch week operations

- [ ] **Block time for triage.** The spike is 48–72 hours; a fix committed in
      response to an issue within a day is the strongest possible signal that the
      project is alive.
- [ ] **Label everything immediately** (`bug`, `enhancement`, `good first issue`).
- [ ] **Fold recurring questions into the README** the same week, while they are
      fresh.
- [ ] **Publish the roadmap** — pin an issue or a Discussion with the top of
      `docs/BACKLOG.md`'s Ready table, so visitors can see where this is going.
