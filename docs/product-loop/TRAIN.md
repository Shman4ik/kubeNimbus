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

Phase: SURVEY · Branch: — · PR: — · Started: — · Last release: v0.3.3 (2026-09-22)
Baseline: first frame — ms (median of 3), executable — MB · RID: — · Sandbox: untried

## Owner notes

_(empty)_

## Items

| # | Source | Deliverable (user outcome) | Kind | Size | Score | Status | Rounds | Commit |
|---|---|---|---|---|---|---|---|---|

## Specs

## Log

- 2026-09-22 — train machinery created; the first train starts at SURVEY. Being the
  first, its researcher seeds `COMPETITOR_MATRIX.md` from `docs/research/` (August
  2026) and researches only what changed since those reports.
