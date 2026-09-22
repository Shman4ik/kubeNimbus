---
name: release-train
description: The kubeNimbus release train — a resumable autonomous loop that surveys the repo and the market, picks 5–10 deliverables, builds and verifies them one at a time on a train branch, and ships them as one release every few days. Each invocation advances the train by one step; drive it with `/loop /release-train`.
---

# kubeNimbus release train

One **train** = one release: survey → select 5–10 deliverables → build them one by
one → harden → release → record → start the next train from a fresh survey.
One **invocation** of this skill = one **step** of the current train. The state
lives in [`docs/product-loop/TRAIN.md`](../../../docs/product-loop/TRAIN.md), never
in the conversation, so any session (or a context that was just compacted) can pick
the train up where the last one left it.

`CLAUDE.md` is already loaded and is the engineering contract — this skill does not
restate it. What this skill adds is *what to build, in what order, and when to ship*.

## Product intent (the tie-breaker for every judgement call)

kubeNimbus wins on **speed, clarity and daily-workflow friction removed**, not on
feature count. The target reactions are "the information I needed was already
visible" and "the action I needed was exactly where I expected it". Free, MIT,
local-first, telemetry-free, agentless, NativeAOT, cross-platform — those are
permanent and are not traded for any feature. Do not clone Lens; competitor parity
alone never justifies an item.

Jobs worth optimising, roughly in order of how often they happen: *what is broken →
why → what changed → which pod/container → its logs/events/metrics → act on it
(restart, scale, exec, port-forward, edit) → confirm it worked*. Then: find anything
fast, understand ownership and relationships, GitOps state, permissions, resource
pressure, comparing clusters. A candidate that removes a `kubectl` round-trip, a
copy/paste, or a view switch from one of those jobs beats one that adds a surface.

## Step 0 — every invocation starts here

1. **Find the live train.** A cloud session starts on a fresh clone of `main`, but
   once BUILD begins the train's state lives on its branch. So look first:
   `git fetch origin` and `git branch -r --list 'origin/train/*'` (or an open PR
   titled `Release train v…`). If one exists, check it out — its `TRAIN.md` is the
   current state. Otherwise read `main`'s `docs/product-loop/TRAIN.md`. If there is
   none at all, create it from the template at the end of this file with
   `Phase: SURVEY` and the next version.
   Then read Config, State, Items and Owner notes.
2. **Owner notes win.** Anything the owner wrote there since the last step (veto an
   item, pin one, pause the train, change a config value) is applied first. A pinned
   item enters the plan; a vetoed one is `dropped` with the owner's reason.
3. Check the tree: correct branch (`train/vX.Y.Z` once BUILD has started, `main`
   before), clean working copy. A dirty tree from an interrupted step: if it is this
   train's in-flight item, resume that item; otherwise stop and report — never
   discard work you did not create.
4. Run exactly **one** step for the current phase (below), update `TRAIN.md`,
   commit it **and push it** before the step report. A cloud container is thrown
   away when the session ends; a step whose state was only committed locally did not
   happen. Push with `git push -u origin <branch>`, retrying a network failure up to
   four times (2 s, 4 s, 8 s, 16 s). Do not chain phases within one invocation
   except where a step says so.

## Phase SURVEY — one step, two tracks in parallel

**Track A, in the background:** spawn `kn-researcher` with `run_in_background: true`
and a *delta* brief: "What changed at Lens, FreeLens, Aptakube, Headlamp, KubeUI,
k9s and any newly relevant client since `<date of last train's SURVEY>` — release
notes, changelogs, top new issues. Plus one deep theme: `<theme>`." Pick the theme
from the job list above that the last two trains did *not* touch. Budget:
`RESEARCH_FETCH_BUDGET` fetches. Output: `docs/product-loop/history/<train>/research.md`
and an updated `docs/product-loop/COMPETITOR_MATRIX.md` (workflows compared, not
feature names). A full re-survey of the market every train is noise; the matrix is
cumulative and only the delta is new. On the very first train the matrix does not
exist yet: the researcher seeds it from `docs/research/*.md` (dated August 2026) and
then researches only what changed since those reports.

**Track B, yourself, while it runs** — the repo scan, cheap sources first:

- `git log <last tag>..main`, `CHANGELOG.md` `[Unreleased]`, open GitHub issues and
  PRs (`gh issue list`, `gh pr list`), the last CI run on `main`.
- `docs/BACKLOG.md`: the **Ready** table (owner-validated — every row is a candidate,
  P0/P1 rows are forced into the plan unless infeasible here) and the **Inbox**
  (evidence pool — mine it, do not re-research what it already cites).
- `TODO`/`FIXME`/`HACK` added since the last tag (`git diff <tag> -G`).
- A **friction walk**: render the screenshot harness for 6–8 core screens (rotate
  which ones between trains; always include the resource list and pod detail) and
  count, for each job above, the interactions it takes today. Look at the PNGs; a
  friction claim made from code alone is a guess.
- Performance baseline, measured, not remembered: publish NativeAOT, run
  `--smoke-test` 3× and take the median time-to-first-frame, record the executable
  size. These go in `TRAIN.md` and are what HARDEN compares against.

Update `docs/product-loop/CURRENT_STATE.md` **incrementally** — what is implemented,
partial, broken, missing — changing only the sections the scan contradicts. Trace
the code before recording something as missing; a doc gap is not a feature gap.

When the researcher's notification arrives, move to `Phase: SELECT`. Do not wait for
it by polling; if the step would otherwise end first, end it and let the next tick
find the report on disk.

## Phase SELECT — one step, ends with the brief

Build a **fresh** candidate list from: this survey, the research delta, Ready rows,
Inbox rows, carry-overs from the last train (not auto-included — they compete again),
and any verification debt the environment can now pay. Score each 1–5:

`Value = 0.20·Frequency + 0.20·TimeSaved + 0.15·Troubleshooting + 0.15·UX + 0.10·Reach + 0.10·Competitive + 0.10·Confidence`
`Score = Value − 0.25·(Effort − 1) − 0.25·(Risk − 1)`

Overrides above any score: P0 bugs, security, data loss, a broken shipped workflow,
owner pins. Then pick the train:

- **5–10 items, `CAPACITY` points** (S = 1, M = 2). **No L items** — split an L into
  an independently shippable S/M slice, or leave it with a design note.
- **Feasible here, end to end**: this machine can build, test, render and verify it
  (the Docker sandbox counts as a live cluster when it is up; a Mac does not exist).
- **Mix**: at least 60% user-visible workflow/UX; at least one reliability or
  verification-debt item; at most one item touching `shared/nimbusUi` (it costs a
  paired pgNimbus PR).
- Up to 3 more as `reserve`, pulled only if the train finishes before
  `MIN_DAYS_BETWEEN_RELEASES`.

For each selected item write a compact spec into `TRAIN.md` — no more than it takes
to build and judge it:

```
### T1 — <user-outcome title>        (<Kind>, <S|M>, score 3.9, source: FEAT-31 / research / scan)
Problem + evidence:   one or two lines, with the link or file:line.
Now → after:          "<job>: 5 interactions (…) → 2 (…)".
Acceptance:           checklist; each line observable in a test, a screenshot or the sandbox.
States:               which of loading / empty / error / RBAC-denied / unsupported API /
                      partial / disconnected / stale apply, and what each shows.
Keyboard + entry:     where it lives, what key reaches it.
Verify:               test names, screenshot scenario(s), sandbox check.
Risk:                 the one thing most likely to go wrong.
```

Create branch `train/vX.Y.Z` from `main` — or, if the session was handed a designated
branch it must push to, use that one and write its name into State — commit
`TRAIN.md`, set `Phase: BUILD`, push, and
output the **iteration brief** in chat: product assessment (3 lines), notable
competitor moves, where kubeNimbus is ahead, the biggest workflow gaps, the ranked
items with one-line rationale and expected user impact. Then end the step — **do not
wait for approval.** The owner steers through Owner notes between ticks.

## Phase BUILD — one item per step

Take the next `planned` item in plan order (an item at `needs-fix` with rounds left
comes first). Mark it `building`, commit that line.

1. **Implement** — spawn `kn-implementer` (`run_in_background: false`) with the item's
   spec copied verbatim from `TRAIN.md`, the train branch name, and what you already
   know about where the code lives. It follows UNDERSTAND → DESIGN → IMPLEMENT → TEST
   → SCREENSHOT → POLISH itself and commits, prefixing the message with the item id.
2. **Verify** — spawn `kn-verifier` (`run_in_background: false`) with the same spec
   text, the implementer's report **verbatim**, and `git show --stat` of the item's
   commits. Never your opinion of the work.
3. `VERDICT: FAIL` → the findings go back verbatim to the same implementer via
   `SendMessage`, then re-verify; increment `Rounds`. At `MAX_FIX_ROUNDS` still failing:
   `git revert` the item's commits, mark it `blocked` with the precise failing
   finding, and move on. A train never stalls on one item.
4. `VERDICT: PASS` → mark `landed` with the commit sha; add the implementer's
   `Release note:` line under `## [Unreleased]` in `CHANGELOG.md` (you own that file
   during a train — implementers do not touch it, which is what keeps parallel items
   conflict-free); push the branch. After the **first** landed item, open the train PR
   as a draft titled `Release train vX.Y.Z` (`gh pr create --draft --base main`, or
   the session's GitHub tools; body from `.github/PULL_REQUEST_TEMPLATE.md`) so CI
   builds every later push — `ci.yml` builds branches only through their PR.
5. Anything the verifier called unverifiable here, and anything out of scope the
   implementer found, goes into the `docs/BACKLOG.md` Inbox as its own row. If the
   item came from BACKLOG, mark that row `done <sha>`.

`PARALLEL: 2` allows two items at once, only when their specs name disjoint files and
neither touches `CLAUDE.md`'s shared sections; run the second implementer with
`isolation: "worktree"` and cherry-pick its commits onto the train branch after its
own PASS. Default is 1 — sequential is slower and never conflicts.

Leave BUILD for HARDEN when every item is terminal (`landed`/`blocked`/`dropped`), or
when `TRAIN_DAYS` have passed since SELECT and at least `MIN_ITEMS` have landed. If
every planned item is done before `MIN_DAYS_BETWEEN_RELEASES` since the last release,
pull the top `reserve` item instead of hardening early.

## Phase HARDEN — one step

1. **Regression sweep** on the train branch head: full build; both TUnit suites
   (report succeeded / failed / **skipped** — skipped cluster tests are not
   cluster-backed verification); the full screenshot harness; NativeAOT publish for
   the local RID with no new trim/AOT warnings beyond the known DataGrid pair; and
   `--smoke-test` 3×. With the sandbox up, the Core integration tests must run
   un-skipped.
2. **Performance gate**: median time-to-first-frame and executable size against the
   SURVEY baseline. More than 10% worse on either is a finding: find the item
   responsible and fix it, or revert it, or write the justification into the
   release notes' engineering section. Never ship an unexplained regression.
3. **Polish + cohesion pass** — one `kn-implementer` run given every screen the train
   changed (both themes, narrow width, long values): hierarchy, spacing, alignment,
   labels, truncation, hover/focus/disabled states, tooltips, keyboard order, visual
   noise. The question is "would this pass in a mature paid desktop app?".
   Then one `kn-verifier` pass over that diff.
4. **Product review**: for each landed item, did the job get measurably faster (the
   Now → after count)? Did anything get more cluttered? A weak or bolted-on item is
   reverted, not shipped — fewer, better changes are a good release.

Record results in `docs/product-loop/history/<train>/verification.md`, then
`Phase: RELEASE`.

## Phase RELEASE — one step (load the `release` skill first)

1. Version: minor bump if anything user-visible was added or changed, patch if only
   fixes. Rename `[Unreleased]` to `## [X.Y.Z] - <date>` and rewrite it for users —
   **outcomes, not implementation** ("See unhealthy workloads immediately", not
   "Added WorkloadHealthService"), grouped Added / Changed / Fixed. Bump
   `<VersionPrefix>` in `Directory.Build.props`. Update README if a screenshot or
   limitation it states is now wrong.
2. Push, mark the train PR ready, retitle it `Release X.Y.Z`, and wait for
   `Build & test` (background `gh pr checks --watch`, not a polling loop).
3. By `RELEASE_MODE`:
   - `auto` — merge with a merge commit (keeps one commit per item for bisecting):
     `gh pr merge --merge --admin`, then `git tag -a vX.Y.Z -m "kubeNimbus vX.Y.Z"` on
     the merge commit and push the tag. Watch `release.yml` in the background; a red
     leg is fixed forward as a patch release, never by moving the tag.
   - `pr` — stop at a green, ready PR and tell the owner the exact merge + tag commands.
   - `none` — leave the branch release-ready.

   `auto` needs an admin identity: `main`'s ruleset requires a code-owner review that
   only the admin bypass skips, and a tag push must be allowed. A cloud session's
   GitHub App token usually has neither (it already lacks `actions: write` — see
   VER-1 in the backlog). If the merge or the tag push is refused, that is not a
   failure of the train: fall back to `pr` for this release, record the refusal in
   `TRAIN.md`, and hand the owner the two commands. Never work around a refusal.
4. The Microsoft Store update stays manual: tell the owner the run id whose
   `windows-msix` artifact to upload.

A missing credential or a refused permission blocks only this phase: finish
everything else, leave the repo release-ready, and say precisely what is missing.

## Phase RECORD — same step as a finished release

Move the final `TRAIN.md` into `docs/product-loop/history/<YYYY-MM-DD>-vX.Y.Z/` with a
short retro appended: items landed / blocked / dropped, interactions removed per job,
perf numbers, what the next survey should look at first. Append a pass entry to
`docs/status-history.md`. Write a fresh `TRAIN.md` from the template (`Phase: SURVEY`,
next version, config carried over, owner notes cleared). Commit on `main` via a small
PR in `auto` mode, or leave it for the owner otherwise.

The next train **starts from a new survey**. Items 11–20 of the last ranking are not
a queue; the repo and the market have both moved.

## Rules that hold in every phase

- **Honesty over completeness.** Permission denied is never rendered as zero
  resources; stale or partial data never looks complete; secrets are masked by
  default; a destructive action names its cluster and namespace; production is
  visually obvious. `CLAUDE.md` has the mechanisms — use them.
- **Every new view state is designed**, not defaulted: loading, empty, error,
  RBAC-denied, unsupported API, partial, disconnected, reconnecting — each named in
  the spec, each seen in a screenshot or a test.
- **Interaction budget**: a selected-resource action is one interaction, a row
  action two at most, a common task after launch three at most. Keyboard first.
- **No new dependency** without a written justification in the item (AOT safety,
  size, licence). No speculative abstractions, framework moves or settings that
  exist only to avoid a decision.
- **Ask the owner only** for money, certificates or paid services, a licence change,
  abandoning a permanent principle above, destructive changes to external
  infrastructure, or a credential this machine does not have. Everything else is
  decided with evidence and written into `TRAIN.md`.
- **Where it runs.** The intended home is a Claude Code cloud session (Linux). The
  `SessionStart` hook installs the .NET 10 SDK, the AOT toolchain and Xvfb; use the
  commands exactly as `CLAUDE.md` gives them, publish `linux-x64`, and wrap
  `--smoke-test` in `xvfb-run -a`. Docker is usually unavailable there, so a "live
  cluster" item is feasible only if `./scripts/sandbox-up.sh` actually comes up — try
  once in SURVEY, record the answer in State, and select accordingly. On the owner's
  Windows machine instead: if `dotnet test --project …` reports "Zero tests ran", run
  `tests/<Project>/bin/Debug/net10.0/<Project>.exe` directly (local SDK quirk), and
  NativeAOT needs the vcvars recipe in `CLAUDE.md`. Either way, scratch output goes
  to a temp directory, never into the repo.

## Step report — at most six lines

Phase and step done; item id + outcome if BUILD; verifier verdict and the one finding
that mattered; what a user gets, in their words; what could not be verified here;
what the next step is. Then, under `/loop`, schedule the next tick: 60 s while there
is work, 3600 s (noop) while waiting on a background researcher, CI run, or the
release window.

## `TRAIN.md` template

```markdown
# Release train — vX.Y.Z

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

Phase: SURVEY · Branch: — · PR: — · Started: <date> · Last release: vA.B.C (<date>)
Baseline: first frame — ms (median of 3), executable — MB · RID: — · Sandbox: untried

## Owner notes

_(empty)_

## Items

| # | Source | Deliverable (user outcome) | Kind | Size | Score | Status | Rounds | Commit |
|---|---|---|---|---|---|---|---|---|

## Specs

## Log
```
