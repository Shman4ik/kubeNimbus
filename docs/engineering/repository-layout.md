# Repository layout: the OSS Scanner image and the public docs

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "Repository layout" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

`.oss-scanner/` is what Anthropic's [OSS Scanner](https://github.com/anthropics/oss-scanner)
builds and reads: a `Dockerfile` (the SDK image with every package restored, a
sha256-checked k3s, both suites run once), `services.sh` (starts k3s as an API server
with no node, for reproducers that want real objects) and `threat_model.md` (what is
untrusted, how findings are rated, what is accepted by design; it leans on SECURITY.md's
security model rather than restating it). The scan runs with no network, so anything a
test or reproducer needs is fetched in the Dockerfile. No CI job builds it (a failed
build is emailed by the scanner); the commands to check it by hand are at its top. Its
k3s version follows `scripts/sandbox-up.sh`, and a change that moves a trust boundary or
accepts a finding by design updates `threat_model.md` in the same PR.

Public-facing docs, each with one job — don't duplicate content between them:

| File | Audience |
|---|---|
| `README.md` | Someone deciding whether to download it. Screenshots, download/install, what it does, limitations. |
| `CONTRIBUTING.md` | Someone opening a PR. Setup, verification, PR expectations, the release procedure. |
| `SECURITY.md` | Reporting a vulnerability, plus the **security model** the app claims to hold (no persisted credentials, no telemetry, exec plugins run external programs). |
| `PRIVACY.md` | The privacy policy: every file the app writes, every connection it makes, what a cluster sees. The Microsoft Store listing's privacy URL and the About box's "Privacy policy" button both point at it — see "The privacy policy" below. |
| `CHANGELOG.md` | Release history — and machine-read: the release workflow lifts the section matching a tag out of it verbatim. |
| `CODE_OF_CONDUCT.md` | Contributor Covenant 2.1, unmodified apart from the contact address. |
| `CLAUDE.md` (this file) | Whoever is changing the code. The engineering contract and the *why* behind every rule. |
| `docs/product-loop/` | The release train's live state (`TRAIN.md`), product assessment, competitor matrix and per-release history. |
| `docs/BACKLOG.md` | How the backlog works. The backlog itself is GitHub issues labelled `roadmap` (since 2026-10-10): the labels, the lifecycle, where the old table rows went, and the proposals deliberately not taken. |
| `docs/PRE-LAUNCH-CHECKLIST.md` | One-time: making the repo public, cutting the first release, and the Microsoft Store submission. Delete it once the launch is behind us. |
| `docs/RELEASE-CHECKLIST.md` | Whoever cuts a release. What to walk before tagging (gates, the manual pass on the AOT build against the sandbox, media, ship), and a log of what each release's pass found. Same shape as pgNimbus's. |
