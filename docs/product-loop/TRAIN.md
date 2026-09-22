# Release train — v0.4.0

Owned by `/release-train` (`.claude/skills/release-train/SKILL.md`). The owner steers
by editing **Config** or **Owner notes**; the next step applies it first.

## Config

| Key | Value |
|---|---|
| `MIN_ITEMS` / `MAX_ITEMS` | 5 / 10 |
| `CAPACITY` | 14 (S = 1, M = 2, no L) |
| `TRAIN_DAYS` | 4 |
| `MIN_DAYS_BETWEEN_RELEASES` | 2 |
| `MAX_FIX_ROUNDS` | 2 |
| `PARALLEL` | 1 |
| `RELEASE_MODE` | auto |
| `RESEARCH_FETCH_BUDGET` | 30 |

## State

Phase: SURVEY · Branch: claude/relaxed-franklin-gssbew (session-designated) · PR: — · Started: 2026-09-22 · Last release: v0.3.3 (2026-09-22)
Baseline: first frame 99 ms (median of 3 warm runs: 99/99/95; cold first run 3529 ms, font cache), executable 51.3 MiB (53 769 168 B; payload with libSkiaSharp + libHarfBuzzSharp 64.6 MiB) · RID: linux-x64 · AOT warnings: the known DataGrid IL2104/IL3053 pair only
Sandbox: **API-server-only.** Docker Hub blobs 403, so `sandbox-up.sh` fails; native `k3s server` (GitHub binary + airgap images) comes up and the demo manifests apply (50-crds/51-custom-resources included, so VER-24 is paid), but `runc` cannot start containers here — no pod ever runs. Real: discovery, list/watch, CRDs, RBAC, SSA/dry-run, patches, evictions. Not real: logs, exec, port-forward, metrics. Must be re-started each session (see CURRENT_STATE.md → Environment notes).

## Owner notes

_(empty)_

## Items

| # | Source | Deliverable (user outcome) | Kind | Size | Score | Status | Rounds | Commit |
|---|---|---|---|---|---|---|---|---|

## Specs

## Log

- 2026-09-22 — SURVEY started. Researcher (delta since 2026-08-15; deep theme "what is
  broken → why → what changed": cluster-wide triage and change history) running in the
  background. Repo scan: no open issues or PRs, CI green on `main`, one commit since
  v0.3.3 (the train machinery), no new TODO/FIXME. Friction walk (7 screens) found the
  Events list unreadable as a timeline (no message/object/last-seen), the workload Events
  tab clipping, absolute timestamps in pod events, and a redundant Namespace column
  when one namespace is selected. `CURRENT_STATE.md` created. Waiting on the researcher
  before SELECT.

- 2026-09-22 — train machinery created; the first train starts at SURVEY. Being the
  first, its researcher seeds `COMPETITOR_MATRIX.md` from `docs/research/` (August
  2026) and researches only what changed since those reports.
