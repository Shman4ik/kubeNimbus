# Pre-launch checklist — making kubeNimbus public, shipping it, and telling people

A one-time working document, in the same shape as pgNimbus's. Ordered so each
phase gates the next: don't flip the repo public until the hygiene items are
done, don't promote until there is something to download, and don't submit to
the Store until a real release exists to point people at.

It was written on 2026-08-27 for a first release of v0.3.0, when the repository
was private with no tags and no releases. **Re-audited on 2026-10-05 against the
live repository**: phases 1 to 6 are done except for the items still unticked
below, v0.6.0 is the latest release, and the Microsoft Store listing is live. What
is left of the launch is the community scaffolding, a few decisions, and the whole
of promotion (phases 7 and 8). Delete this file once those are behind us.

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
- [x] **GitHub secret scanning and push protection** are on (checked through the
      API on 2026-10-05), with Dependabot security updates.
- [x] **The personal commit email is in the history** (`shman4ik@gmail.com` is
      author and committer throughout). Accepted: it is normal for open source,
      and rewriting a public history is not worth it.
- [x] **Package metadata in `Directory.Build.props`** — `Product`, `Authors`,
      `Copyright`, `RepositoryUrl`, `PackageLicenseExpression`. This is what
      shows in the shipped binary's file properties.
- [ ] **Read `CLAUDE.md` and `docs/BACKLOG.md` once with public-reader
      glasses.** Both are engineering notes rather than marketing, which is fine
      and is what pgNimbus does — but they name unverified paths and open
      defects openly, so skim for anything that reads worse out of context than
      it does in it. The 2026-10-05 pass closed the backlog's stale rows and
      duplicate IDs (rows still open for work that had shipped); the read for tone
      is still to do.

## Phase 2 — Quality gates

- [x] **CI on every PR and push** (`.github/workflows/ci.yml`): build, both test
      projects, the screenshot render as a XAML smoke test, the stress mode, and
      the linux-x64 NativeAOT publish followed by an actual `--smoke-test` launch.
- [x] **Dependabot** for NuGet and GitHub Actions, Avalonia grouped.
- [x] **Vulnerability gate** — `NuGetAuditMode=all` with NU1902–NU1904 promoted
      to errors in `Directory.Build.props`.
- [x] **Branch protection on `main`** — the `main` ruleset requires the CI checks
      and resolved threads; `v*` tags sit under the `Release tags` ruleset. The
      settings are kept the same as pgNimbus's (see `CLAUDE.md`, "Release, CI and
      packaging").

## Phase 3 — Community scaffolding

- [x] `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, issue templates
      (the bug form asks for the Kubernetes distribution and whether the sandbox
      reproduces it), a PR template built from CLAUDE.md's rules.
- [x] **Private vulnerability reporting** is enabled, so `SECURITY.md`'s link
      resolves.
- [ ] **Curate 5–10 `good first issue` candidates** from `docs/BACKLOG.md`'s
      Inbox. The label exists, but on 2026-10-05 the tracker held no issues at all,
      open or closed, and an empty tracker at launch reads as "not really open to
      contributors". The verification-debt rows are good candidates for anyone
      with hardware this project has rarely run on (a Mac, an arm64 Linux box).
- [ ] **Pin a roadmap** — an issue or a Discussion with the top of the backlog, so
      visitors can see where this is going. Discussions are enabled and empty.

## Phase 4 — First release

The first public release ended up being v0.3.2 (2026-08-29, a pre-release), then
v0.3.3, 0.4.0, 0.5.0 and 0.6.0. The release procedure now lives in the `release`
skill and `docs/RELEASE-CHECKLIST.md`.

- [x] **`CHANGELOG.md` cut**, with a note that 0.1.0 and 0.2.0 were never
      published, so nobody hunts for downloads that do not exist.
- [x] **Dry-run the pipeline** — release run
      [35968059715](https://github.com/Shman4ik/kubeNimbus/actions/runs/35968059715)
      launched all four RIDs on their own runners.
- [x] **Tag, push and check the release page** — every release since v0.3.2 carries
      the archives, `SHA256SUMS.txt`, the CHANGELOG section as the body and the
      unsigned-binary footer.
- [ ] **Download and run one archive per platform you own.** The launch check
      proves the binary starts on a runner; it does not prove the archive you
      published extracts into something that starts on a real desktop. The win-x64
      build is walked in each release's manual pass (`docs/RELEASE-CHECKLIST.md`);
      **no person has yet run the osx-arm64 build on a Mac** (backlog VER-8).
- [x] **README screenshots match the UI.** All of them were re-rendered on Windows
      for 0.6.0, and the hero again on 2026-10-05 so that its last row is not cut
      off (`readme-applications-list`, 1280×880).
- [ ] **Code signing — decide, don't necessarily block.** The Store channel buys the
      SmartScreen trust for $0; a purchased Authenticode certificate would
      additionally clean up the direct-download path, and macOS notarization needs
      an Apple Developer account. pgNimbus deliberately does not buy one. Make the
      same decision consciously (backlog DIST-1), because "is it signed?" is the
      first question every thread asks.

## Phase 5 — Flip the repo public

- [x] **Description** — "A fast, open-source Kubernetes desktop client (.NET +
      Avalonia, NativeAOT)", with the Store listing as the homepage.
- [x] **Topics** — `kubernetes`, `k8s`, `kubectl`, `gui`, `desktop-app`,
      `avalonia`, `dotnet`, `csharp`, `native-aot`, `devops`.
- [x] **Social preview image** is set.
- [x] **Discussions** are enabled.
- [x] **Public.**

## Phase 6 — Microsoft Store

The product identity is `DmitriiShmanev.kubeNimbus`, Store ID `9MZ3C28M65PB` — the
manifest carries it and it must not be edited. Mechanics and reasoning are in the
`release` skill.

- [x] **Listed and live** at
      [apps.microsoft.com/detail/9MZ3C28M65PB](https://apps.microsoft.com/detail/9MZ3C28M65PB).
      The 0.4.0 submission failed certification on policy 10.5.1 because the privacy
      URL pointed at `SECURITY.md`; `PRIVACY.md` fixed that (see `CLAUDE.md`, "The
      privacy policy and the Store listing text").
- [x] **The README carries the Store badge** and `winget install --id 9MZ3C28M65PB
      --source msstore`.
- [ ] **Homebrew cask and AUR** — the other half of backlog DIST-2. Needs accounts
      and, for Homebrew, a decision about the unsigned `.dmg`.

## Phase 7 — Promotion

Sequence matters: seed the quiet channels first and save the spike for when the
repo has a release, screenshots and a Store listing — all three exist now.

- [ ] **Write the pitch once.** The thesis is in CLAUDE.md's Mission and it is
      unusually defensible: KubeUI is the one true open-source native peer, and
      the difference is measured — ~156 ms to first window against ~645 ms, a
      ~62 MB payload against a 382 MiB single file, and no telemetry where theirs
      is on by default. The README's three GIFs (#138) cover the capture this item
      used to ask for; a 20–30 s video for the Store listing is still open (DIST-5).
- [ ] **A comparison page** (backlog DIST-4, DIST-7, DIST-9) — the positioning lives
      only in `CLAUDE.md` and `docs/research/`, where no prospective user reads it.
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
