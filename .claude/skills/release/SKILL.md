---
name: release
description: kubeNimbus release, CI and packaging rules — tag-driven release.yml, per-RID launch checks, the Windows zip, .dmg/.deb/AppImage installers, why NativeAOT is not compiled for size, Microsoft Store MSIX identity, assembly-name coupling, and the 0.5 GB Actions storage budget. Load before cutting a release or editing .github/workflows, installer/, or the packaging scripts.
---

## Actions storage is a 0.5 GB budget (2026-08)

The account's included GitHub Actions storage is **0.5 GB**, shared with
pgNimbus, and it is a *standing* budget: an artifact counts for every day it
stays alive, not once per run. This repo's `screenshots` artifact alone held
**1.34 GB** — ~20 CI runs a day at ~14 MB each, kept 7 days, uploaded
unconditionally. So:

1. **Every `upload-artifact` sets `retention-days`**, and the release
   packages get **1**, not 7. The `release` job downloads them in the same run
   and republishes every file as a GitHub Release asset, and Release assets do
   not count against the Actions allowance — a week-long second copy buys
   nothing. A day still leaves a `workflow_dispatch` dry run's packages
   downloadable, which is the only case where `release` never runs.
2. **Diagnostic artifacts upload on `failure()` only** (the screenshots
   above).
3. **One commit gets one CI run, and the *trigger list* is what holds that,
   not the concurrency group.** `ci.yml` used to trigger on pushes to
   `claude/**` as well as on the PR for that same branch, and the two events
   ran the whole job twice for one commit — two sets of runner minutes and two
   artifacts. The concurrency key was believed to prevent it and cannot: a push
   yields `refs/heads/claude/x` where the PR event yields `claude/x`, so they
   are different groups by construction. Branch work is built through its PR,
   which is the run the branch ruleset requires anyway, and the cost is that a
   branch pushed with no PR open is not built until one is.
   **There is no push-to-`main` trigger either (2026-09).** The merge commit is
   the code its PR just built, so a second run re-proved nothing, and a release
   cycle showed three workflow runs — the release PR, the merge, the tag — where
   two carry all the information. The cost, stated: a semantic conflict between
   two PRs that each passed alone surfaces on the next PR or in `release.yml`,
   not on `main`. What the main run uniquely supplied was a NuGet cache every PR
   can read, and rule 5 covers that now.
   The group stays `github.event.pull_request.head.ref || github.ref` and the
   two sides stay in **different namespaces on purpose**. Normalising them (to
   `github.ref_name`) would be a regression now the repository is public: a
   fork's PR from its own `main` reports `head.ref` as `main`, which would
   share a group with this repo's default-branch runs and — with
   `cancel-in-progress` — let an outside PR cancel them.

4. **CodeQL runs on a weekly schedule only, never a pull request and never a
   push** (`.github/workflows/codeql.yml`). GitHub's "default setup" runs it on
   every PR, which put three more checks — the slowest of them a full C#
   build — in front of every merge, on a repository whose PRs are small and
   frequent and whose CodeQL findings are almost never about the lines a PR
   touches. It ran on push to `main` for a while too, which meant a merge fired
   both `CI` and `CodeQL`, and a release tag on top of that made three workflow
   runs show up for one release cycle — visibly more than pgNimbus's two (`CI`
   on the merge, `Release` on the tag), which carries no CodeQL at all.
   Scheduled-only keeps the weekly sweep (new queries against unchanged code)
   without adding a run to either path a human is watching. The trade is stated
   in the workflow: a vulnerability introduced by a merge is reported up to a
   week later rather than the same day, while dependency risk — the likelier
   source — is still gated *on* the PR by `NuGetAudit`, which fails the
   ordinary build. The build mode for C# is **manual** rather than autobuild:
   autobuild guesses a build command, and its guess for a `.slnx` on a preview
   SDK is exactly the kind of thing that starts failing silently months later.
   Default setup has to be switched off in repository settings for this workflow
   to run at all — the two cannot coexist.

5. **A PR runs only the jobs its files can affect, and restores NuGet from a
   cache it never writes.** `ci.yml`'s `changes` job classifies the PR's files:
   a PR touching only `docs/`, `design/`, `.claude/`, `LICENSE` or `*.md` skips
   every build (except `docs/keyboard-shortcuts.md`, which `ShortcutDocsTests`
   reads), and the AOT job runs only for `src/`, `shared/`, the build-wide props,
   `global.json`, `nuget.config` (it decides where the ILCompiler comes from) or
   `ci.yml` itself. It is a job and not `paths-ignore` because
   `Build & test` is required: a workflow skipped by its trigger never reports
   the check, and the PR would wait for it forever, while a job skipped by its
   own `if:` reports success. The consumers test `!= 'false'`, so a failed
   classifier makes everything run — it can cost time, never coverage.
   The XAML smoke test (the screenshot render, ~90 s) is its own job in
   parallel with `Build & test` rather than a step at the end of it, which is
   what took the required check from 2.5–4 min to about one.
   Caches are scoped to the ref that saved them, so a PR's saved cache is
   invisible to the next PR. `warm-cache.yml` is therefore the **only writer**:
   on `main`, when a `*.csproj`, `Directory.Build.props` or `global.json`
   changes, weekly (GitHub evicts a cache unread for 7 days), and by hand. It
   saves `nuget-<os>-<arch>-<hash>` (the solution restore) and
   `nuget-aot-<os>-<arch>-<hash>` per release RID (the app restored with `-r
   <rid> -p:PublishAot=true`, which adds the ILCompiler and runtime packs).
   `ci.yml` and `release.yml` use `actions/cache/restore` and never save. Cache
   storage is separate from the 0.5 GB artifact budget above (10 GB per repo).

**Only `Build & test` and `dependency-review` are required checks.** The
branch ruleset on `main` requires a PR, resolved review threads and those two
jobs, and no approval (2026-09, the same as pgNimbus); `dependency-review`
refuses a PR that adds a package with a known advisory. `XAML smoke test` and `NativeAOT publish
(linux-x64)` still run on every PR that can affect them and are still worth
reading, but they do not hold the merge. The smoke test is a candidate for the
required list (it catches a view that no longer loads, which nothing else in CI
does); the AOT job is not, because it
is the slow half of the wait and an AOT regression cannot reach anybody without
going through `release.yml`, which publishes *and launches* every RID.

**A tag and a release are final (2026-09).** The `Release tags` ruleset lets a
`v*` tag be created but never moved or deleted, and immutable releases lock a
published release's tag and assets. A bad release is fixed by the next tag, as
before; there is no longer any other way. Every `uses:` is pinned to a commit
SHA with a `# vX.Y.Z` comment, which the repository setting
`sha_pinning_required` enforces, and Dependabot's `github-actions` entry moves
SHA and comment together.

Retention is **not retroactive**. Lowering it leaves already-uploaded
artifacts on their original clock, so a change like this has to be paired with
a one-time purge of the backlog (`gh api repos/OWNER/REPO/actions/artifacts`
→ `DELETE`). The repo default is set to 7 days as a backstop for uploads that
forget rule 1.

## Releasing

Tag-driven, `.github/workflows/release.yml`. The procedure is written for
humans in [CONTRIBUTING.md](../../../CONTRIBUTING.md#cutting-a-release-maintainers), what
to walk before tagging is [`docs/RELEASE-CHECKLIST.md`](../../../docs/RELEASE-CHECKLIST.md)
(add a log row for every release), and the design decisions behind the pipeline are here.

- **The version lives in exactly one place**, `<VersionPrefix>` in
  `Directory.Build.props`, and a tagged build overrides it with
  `-p:Version=<tag>`. A tag and the checked-in value disagreeing therefore
  cannot produce a mislabelled binary — the tag always wins.
- **`CHANGELOG.md` is machine-read.** The workflow lifts the section whose
  heading matches the tag (`## [0.1.0]` ↔ `v0.1.0`) and uses it verbatim as the
  release body, stopping at the next `## ` heading *or* at the link-reference
  block the file ends with. A release therefore cannot claim something the
  repository doesn't say. A missing section degrades to `--generate-notes`
  with a warning rather than failing the release.
- **NativeAOT cannot cross-compile**, which is the entire reason the build job
  is a matrix of four runners rather than four `-r` flags on one. `win-x64` is
  the shipping target; `linux-x64`, `linux-arm64` and `osx-arm64` ship too.
  `fail-fast: false` — knowing that only one RID broke is the useful outcome.
- **Every RID is launched before it is archived.** The matrix already gives each
  RID a runner of its own OS and architecture (it has to — NativeAOT cannot
  cross-compile), so each one also *runs* the binary it just built, via
  `--smoke-test`, between Publish and Stage. See "The launch check" above. This
  step is not optional polish: without it, v0.1.0 attached four binaries — every RID — that
  could not start to a public release page.
- **Only a tag with a pre-release suffix (`v0.4.0-rc.1`) ships flagged as a
  pre-release; a plain `0.x` tag is a full release and takes the Latest
  label.** Every `0.x` used to be flagged, on the argument that a pre-1.0
  project should say so — but GitHub never gives a pre-release the Latest
  label, so v0.3.1–v0.3.3 left the repository with no latest release at all:
  `/releases/latest` (which the README links to) resolved to nothing and the
  sidebar showed no release. The version number already says pre-1.0. The
  workflow passes no `--latest` flag: GitHub's automatic choice picks the
  highest semver, and forcing it would hand the label to an older tag
  re-published through `workflow_dispatch`.
- **Binaries are unsigned** (no certificates), so every release body repeats
  the SmartScreen/Gatekeeper workaround. Don't drop that footer.
- **Every platform ships an installer beside the portable archive**, and each
  one is *installed and launched* in CI before the release exists — see below.
- `workflow_dispatch` with `dry_run: true` builds and archives all four RIDs
  without creating anything public — use it after touching the workflow.

### Supply chain of a release (2026-10, security audit block 5)

Four rules, each closing a way a release could ship something nobody reviewed:

1. **A tool a release downloads and runs is pinned by version and SHA-256, and
   checked before it runs.** `scripts/linux/build-packages.sh` used to fetch
   appimagetool from its `continuous` release and run it unchecked on both Linux
   legs, *before* the Checksum step, so a compromised tool could have rewritten
   that runner's `.AppImage`, `.deb` and `.tar.gz` and `SHA256SUMS.txt` would
   then have vouched for them. It now fetches a tagged appimagetool and a tagged
   type2-runtime (appimagetool 1.9.x otherwise downloads the runtime it embeds,
   from `continuous`, at build time) and verifies both with `sha256sum -c`.
   Neither upstream release is immutable (1.9.1's assets were re-uploaded two
   weeks after it was published), which is why a pinned tag alone is not enough.
   How to move the pin is a comment beside the hashes. A new download in any
   release step follows the same rule; an action is pinned by commit SHA instead.
2. **Build provenance is attested in the `release` job only**
   (`actions/attest-build-provenance`, over `artifacts/*` and `SHA256SUMS.txt`,
   before `gh release create`). Not in the build legs: a dry run never reaches the
   `release` job, so it never puts a throwaway build into Sigstore's public
   transparency log. The job alone holds `id-token: write` and
   `attestations: write`; the workflow default stays `contents: read`. A dry run
   therefore cannot exercise the step, and the first real release is its test
   (`docs/RELEASE-CHECKLIST.md` has the `gh attestation verify` row).
3. **A real release is dispatched from `main` only.** The `release` job's first
   step fails a `workflow_dispatch` from any other ref; a dry run from a branch is
   still how the workflow is tested. `gh release create` passes `--target
   "$GITHUB_SHA"`, so a dispatched release tags the commit the legs built rather
   than whatever `main` points at when the job reaches that line.
4. **Every `actions/checkout` sets `persist-credentials: false`.** Nothing in
   any workflow runs git against the remote; `gh` reads `GH_TOKEN` from its
   step's env. A persisted token sits in `.git/config` for every later step,
   including third-party code, and in the `release` job it can write.

### Installers (.dmg, .deb, .AppImage) and the Windows zip

A tarball is what a developer wants. Everyone else expects an
installer, and until this every platform's instructions in the README ended in
"extract it and run the binary from a terminal" — which on macOS was not even
optional, since an unbundled binary has no Dock icon, no app name and no way to
be launched from Spotlight. `release.yml` now builds, per RID:

- **Windows: the portable zip, and no installer.** The Store package covers
  installing and updating, so the direct download is
  `kubeNimbus-<version>-win-x64.zip`: one top-level folder, no `.pdb`, nothing
  to install, and the app's data lives in `%AppData%\kubeNimbus` either way, so
  replacing the folder is the update. **It replaced a per-user WiX MSI on
  2026-10-04**, following pgNimbus (#343, 2026-10-01): across 0.3.2–0.5.0 the
  MSI was downloaded once and the zip three times, the workflow installed WiX
  as a floating `5.*` on every release run (WiX 7 has since started to require
  accepting its OSMF EULA), and WiX wrote a `.wixpdb` beside the MSI in `dist/`
  that every release from 0.3.2 attached as a public asset. What the zip gives
  up is the Start menu entry, the Apps entry and upgrading in place. If an MSI
  ever comes back, its old `UpgradeCode` was
  `29a90bce-dcfd-4567-b2fc-da434722f319`; reuse it, so it upgrades the 0.3–0.5
  installs instead of landing beside them.
- **`.app` + `.dmg`** (`scripts/macos/build-app-bundle.sh`). The bundle is
  **ad-hoc signed**, and that is not decoration: a quarantined bundle with no
  signature at all is reported as *"kubeNimbus is damaged and can't be opened"*,
  which reads as a corrupt download and cannot be cleared by right-click →
  Open, while an ad-hoc signature fails the same Gatekeeper check as *"Apple
  cannot check it for malicious software"*, which is true and which the normal
  right-click → Open path clears. Apple Silicon also refuses to execute
  unsigned code at all. The `/Applications` symlink in the volume is what stops
  people running the app off a read-only disk image.
- **`.deb` + `.AppImage`** (`scripts/linux/build-packages.sh`). The `.deb`
  declares the seven X11 libraries Avalonia dlopens plus fontconfig and
  freetype; the AppImage's `AppRun` is a symlink rather than a wrapper script,
  because NativeAOT resolves its side-car `.so` files relative to
  `/proc/self/exe` and needs nothing exported. The `.tar.gz` is still built by
  the workflow itself, so the script deliberately does not produce one.

Three things about this are load-bearing:

1. **Each package is smoke-launched through its own installed path**, not just
   after publishing. The launch check on the publish output (see "The launch
   check") proves the binary starts; it says nothing about what an installer can
   break, which is a different list: a file left out of an archive, a bundle
   layout Gatekeeper refuses, a `Depends` line one library short of what the
   X11 backend loads. The zip leg unpacks the archive into a fresh folder, runs
   both smoke modes from there, and fails if a `.pdb` reached it. The `.deb` leg
   is the strongest: `apt` resolves the control file's own `Depends`, so a
   forgotten library fails on a runner rather than on a minimal desktop.
2. **The packages are written into `dist/`**, which is where the existing
   checksum and upload steps already look. Nothing about `SHA256SUMS.txt` or the
   release job needed a per-format branch.
3. **The MSIX is now excluded from the release by an artifact `pattern`.** The
   workflow has always *said* the Store package is not a Release asset, and the
   release job downloaded every artifact in the run, so v0.3.1 attached a
   self-signed `.msix` that nobody could install. `pattern: kubeNimbus-*` is
   what makes the comment true; the MSIX artifact keeps its own name and its
   14-day retention.

**Not done:** winget manifests, a Homebrew cask, and signing/notarization of any
kind (deferred until after 1.0; build provenance and GitHub's release
attestations are what a download can be checked against meanwhile). The first two are distribution channels that need this to exist first; the
third needs a paid certificate and an Apple Developer account.

### Microsoft Store (MSIX)

The Store is how an unsigned free project buys out of SmartScreen for $0: an
uploaded package is **re-signed by Microsoft with its own trusted certificate**
during certification, so the upload only needs a throwaway self-signed
certificate to satisfy MSIX's "must be signed" rule — not a purchased
Authenticode one. It is an *additional* channel, not a replacement for the
direct download; the GitHub Release archives stay exactly as they are.

**The app is live on the Store**: <https://apps.microsoft.com/detail/9MZ3C28M65PB> (product ID `9MZ3C28M65PB`). That makes
it the only signed way to get kubeNimbus, which is why the README leads Windows
users there and treats the Release downloads as the alternative — and it is why
the identity values below are now load-bearing for an *update* rather than for a
first submission: a package whose identity does not match the listing is
rejected at upload, and there is a listing to be rejected against now.

`release.yml`'s `win-x64` leg packs the publish output into a `.msix` via
[`scripts/windows/build-msix.ps1`](../../../scripts/windows/build-msix.ps1) and uploads
it as the `windows-msix` artifact. Five things are load-bearing.

1. **The package is not a Release asset, and that is deliberate — and the way it
   leaks onto one is the `release` job's download step.** That job hands
   everything under `artifacts/` to `gh release create`, so a bare
   `download-artifact` collects `windows-msix` along with the archives; v0.3.1
   shipped exactly that, a self-signed `.msix` offered for public download and
   missing from `SHA256SUMS.txt` because the checksum step never saw it. The
   step carries `pattern: kubeNimbus-*` for that reason.
   As for why it must not be there: A
   self-signed MSIX cannot be installed by anyone who has not first trusted the
   certificate, so publishing it beside the zip would offer a download that
   fails for every person who takes it. It is a workflow artifact instead, and
   the only one carrying more than a day of retention (14 days), because
   Partner Center submission is a manual download-and-upload rather than a
   same-run hand-off. That is a stated exception to the retention rule under
   "Actions storage is a 0.5 GB budget".
2. **The identity is fixed and must never be edited.**
   [`installer/msix/Package.appxmanifest`](../../../installer/msix/Package.appxmanifest)
   carries this repo's reserved Partner Center product identity —
   `DmitriiShmanev.kubeNimbus` / `CN=04FDF7B0-6D86-4EB7-B798-21CD434897BC`,
   Store ID `9MZ3C28M65PB`. A mismatch is rejected at upload, and the failure
   would otherwise surface only after a full release run, which is why
   `build-msix.ps1` refuses outright if the placeholder text is still there.
   The publisher CN is the same GUID pgNimbus uses because both apps are under
   one Partner Center account; that is correct, not a copy-paste error.
3. **Plain Win32 / Desktop Bridge, not Windows App SDK.** The app is a
   NativeAOT executable with no WinUI dependency, so the manifest declares
   `EntryPoint="Windows.FullTrustApplication"` and the `runFullTrust`
   capability, and nothing else. `Executable` is `kubeNimbus.exe` — the
   assembly name, which is the product name (see the section below).
4. **`makepri` is what makes the tile assets take effect.** The 25 files in
   `src/KubeNimbus.App/Assets/Msix/` are scale- and targetsize-qualified
   (`Square44x44Logo.scale-200.png`,
   `Square44x44Logo.targetsize-48_altform-unplated.png`, …), and `makeappx`
   infers no resource map from filenames alone: without a compiled
   `resources.pri`, Windows resolves only the scale-100 entries and silently
   backplates or upscales the icon on the taskbar, Start and the install
   dialog, while the other 20 files sit in the package unused. The script also
   strips `createconfig`'s default auto-split, which would scatter the
   qualified resources into `resources.scale-*.pri` side files that only an
   AppxBundle's manifest could reference — this is one flat package.
5. **The version is 4-part with the last field zero.** `ConvertTo-MsixVersion`
   drops any prerelease suffix (`0.3.0-rc.1` → `0.3.0.0`), because MSIX
   versions are numeric-only and Store convention reserves the revision field.
   Two prereleases of one version therefore collide in the Store; bump the
   patch rather than relying on a suffix for a Store submission.

**Submission is manual and not automated.** Download `windows-msix` from the
release run, upload the `.msix` in Partner Center → kubeNimbus → Packages, and
submit for certification. Moving to the Store submission API would need its own
Entra ID app registration under the Partner Center account (free, unrelated to
paid signing) and is not worth it before the second or third release.

### NativeAOT does not compile for size, and that was measured (2026-10-04)

`OptimizationPreference=Size` (ILC `-Os` instead of its default blended `-O`)
was tried and rejected. Measured on win-x64, the same commit built both ways:

| | default `-O` | `Size` |
|---|---|---|
| `kubeNimbus.exe` | 62.4 MB | 46.5 MB |
| the zip a user downloads | 29.6 MB | 28.4 MB |
| median `--smoke-test` (launch, first frame, exit) | 432–436 ms | 448–456 ms |
| idle after 4 s: working set / private bytes | 101 / 111 MB | 128 / 130 MB |

The 16 MB comes almost entirely out of what compresses well, so the download
shrinks by 1.2 MB, and the app starts slower and holds about 20 MB more memory.
Every package format here is compressed (zip, tar.gz, `.deb`, AppImage,
`.dmg`, MSIX), so only the unpacked size on disk would gain. Re-measure before
proposing it again, and compare compressed packages and memory, not the
binary's size. The rest of a win-x64 package is native side-cars no ILC switch
touches: `libSkiaSharp.dll` 11.1 MB, `av_libglesv2.dll` (ANGLE) 5.1 MB,
`libHarfBuzzSharp.dll` 1.7 MB.

Two traps for whoever measures it next. `IlcOptimizationPreference`, the name
most samples and blog posts use, is the pre-.NET 8 spelling: it is silently
ignored, the response file keeps `-O`, and the binary comes out the size it
was, so check `obj/<platform>/Release/net10.0/<rid>/native/kubeNimbus.ilc.rsp`
for `--Os` rather than trusting the property name. And `UseSystemResourceKeys`,
the next size switch people reach for, stays off: it replaces the framework's
exception messages with resource keys, and the connection failure view quotes
those messages to the user.

### The app's assembly name is `kubeNimbus`, not `KubeNimbus.App`

The shipped executable is the product name, because that is what a user
downloads and pins to a taskbar. Three things are coupled to it and must move
together, or the app builds fine and dies at startup:

1. `<AssemblyName>` in `KubeNimbus.App.csproj`,
2. `App.axaml`'s `avares://kubeNimbus/Styles/Theme.axaml` — `avares://`
   authority *is* the assembly name,
3. `app.manifest`'s `assemblyIdentity name`.

The `Yaml-Mode.xshd` resource is safe: it is included with an explicit
`LogicalName`, so `GetManifestResourceStream("Yaml-Mode.xshd")` doesn't depend
on the assembly name. Root namespace and `x:Class` values are unchanged and
unaffected.
