# Release train — v0.7.0

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

Phase: SURVEY · Branch: — · PR: — · Started: — · Last release: v0.6.0 (2026-10-04)
Baseline: first frame — ms (median of 3), executable — MB · RID: — · Sandbox: untried
(For reference, the 0.6.0 release pass measured the win-x64 NativeAOT exe at 62.4 MB and
`--smoke-test` wall time at a median of 438 ms; see `docs/RELEASE-CHECKLIST.md`.)

## Owner notes

_(empty)_

## Items

| # | Source | Deliverable (user outcome) | Kind | Size | Score | Status | Rounds | Commit |
|---|---|---|---|---|---|---|---|---|

## Specs

## Log

- 2026-10-05 — Fresh file. The v0.4.0 train's file had never been closed by a RECORD
  step and still read `Phase: BUILD`; it is in `history/v0.4.0/TRAIN.md` with a retro.
  Releases 0.5.0 and 0.6.0 were cut outside the train.
