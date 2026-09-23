---
name: release
description: kubeNimbus release, CI and packaging rules — tag-driven release.yml, per-RID launch checks, MSI/.dmg/.deb/AppImage installers, Microsoft Store MSIX identity, assembly-name coupling, and the 0.5 GB Actions storage budget. Load before cutting a release or editing .github/workflows, installer/, or the packaging scripts.
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
   are different groups by construction. The push trigger is `main` only now,
   which is also pgNimbus's shape; branch work is built through its PR, which
   is the run the branch ruleset requires anyway, and the cost is that a branch
   pushed with no PR open is not built until one is.
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

**Only `Build & test` is a required check.** The branch ruleset on `main`
requires a PR and that one job; `NativeAOT publish (linux-x64)` still runs on
every PR and is still worth reading, but it does not hold the merge, because it
is the slow half of the wait and an AOT regression cannot reach anybody without
going through `release.yml`, which publishes *and launches* every RID.

Retention is **not retroactive**. Lowering it leaves already-uploaded
artifacts on their original clock, so a change like this has to be paired with
a one-time purge of the backlog (`gh api repos/OWNER/REPO/actions/artifacts`
→ `DELETE`). The repo default is set to 7 days as a backstop for uploads that
forget rule 1.

## Releasing

Tag-driven, `.github/workflows/release.yml`. The procedure is written for
humans in [CONTRIBUTING.md](../../../CONTRIBUTING.md#cutting-a-release-maintainers); the
design decisions behind it are here.

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
  step is not optional polish: without it, v0.1.0 attached three binaries that
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

### Installers (MSI, .dmg, .deb, .AppImage)

A zip and a tarball are what a developer wants. Everyone else expects an
installer, and until this every platform's instructions in the README ended in
"extract it and run the binary from a terminal" — which on macOS was not even
optional, since an unbundled binary has no Dock icon, no app name and no way to
be launched from Spotlight. `release.yml` now builds, per RID:

- **MSI** (`installer/windows/Product.wxs`, WiX 5). Per-user into
  `%LocalAppData%\kubeNimbus`, no elevation: the MSI is unsigned until there is
  a certificate, and per-machine plus unsigned is a much rougher UAC and
  SmartScreen experience than per-user plus unsigned. The app needs no
  machine-wide state to justify the trade. **The `UpgradeCode` is fixed
  forever** — regenerating it makes a new MSI install *beside* the old version
  instead of upgrading it.
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
   break, which is a different list: a file missing from the WiX component
   group, a bundle layout Gatekeeper refuses, a `Depends` line one library short
   of what the X11 backend loads. The MSI leg is the strongest — it installs,
   runs `%LocalAppData%\kubeNimbus\kubeNimbus.exe --smoke-test`, and
   uninstalls, because leaving the product registered would make the next run
   hit an upgrade path instead of a clean first install. The `.deb` leg is the
   second: `apt` resolves the control file's own `Depends`, so a forgotten
   library fails on a runner rather than on a minimal desktop.
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
kind. The first two are distribution channels that need this to exist first; the
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
