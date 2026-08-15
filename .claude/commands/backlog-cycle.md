---
description: One kubeNimbus backlog cycle — pick the top validated item, implement it with an Opus subagent, verify it with a Sonnet subagent, push and record the result.
model: opus
---

Run **one** backlog cycle for kubeNimbus. One cycle = at most one item taken from
`ready` to `done`. Do not batch, do not "also fix" anything you notice, do not
start a second item because the first was small.

`docs/BACKLOG.md` is the state of this loop. Its `Config` block sets
`AUTO_PR`, `MAX_FIX_ROUNDS`, `RESEARCH_EVERY` and `READY_POOL_MIN` — read them
each cycle rather than remembering them; the human edits that file between runs.

## 1. Orient

Read `docs/BACKLOG.md`. Confirm you are on the designated feature branch and that
the working tree is clean; if it is dirty from a previous cycle, resolve that
first — commit it, or report it and stop. Never discard work you did not create.

## 2. Choose exactly one item

In this order:

1. An item at `needs-fix` whose `Rounds` is below `MAX_FIX_ROUNDS` — finish what
   is already open before starting anything new.
2. Otherwise the highest-priority item at `ready`. Ties break toward the smaller
   size: a shipped S beats a half-finished L.
3. **If the `Ready` table is empty, stop.** Do not promote anything out of the
   Inbox yourself. The Inbox is the human's queue — validating it and setting
   priorities is their job, and taking it from them is the one way this loop can
   do real damage. Report that Ready is empty, name how many items are waiting in
   the Inbox, and end the cycle.

Set the item to `in-progress` in `docs/BACKLOG.md` and commit that line alone, so
an interrupted cycle is visible in the next one.

## 3. Implement — `kn-implementer` (Opus)

Spawn `kn-implementer` with `run_in_background: false` (the verification step
depends on its result, and nothing else useful can happen meanwhile).

Give it, in the prompt: the item id, its full text and acceptance criteria copied
out of the backlog, whatever you already know about where the code lives, and any
constraint from the item's Notes column. Do not paste `CLAUDE.md` — it reads that
itself.

## 4. Verify — `kn-verifier` (Sonnet)

Then spawn `kn-verifier`, also `run_in_background: false`. Give it: the item and
its acceptance criteria (the same text — not your paraphrase), the implementer's
report **verbatim** so it can check the claims in it, and `git diff` /
`git status` output naming the files that changed. Do not tell it what you think
of the work; an anchored reviewer is not an independent one.

- `VERDICT: PASS` → go to step 5.
- `VERDICT: FAIL` → send the findings **verbatim** back to the same
  implementer with `SendMessage` (its context is intact; a fresh agent would
  re-derive everything). Then re-run `kn-verifier` on the new diff. Increment
  `Rounds` in the backlog each time.
- Still failing at `MAX_FIX_ROUNDS` → do not keep grinding. Set the item to
  `blocked`, write what specifically fails and what both agents concluded into
  its Notes, commit that, and end the cycle. A blocked item with a precise note
  is a good outcome; a fifth round of the same failure is not.

If the verifier's finding is that the *item itself* is wrong — the acceptance
criteria contradict `CLAUDE.md`, or the design is unsound — stop the cycle and
put that in your report. That is a decision for the human, not a fix to force.

## 5. Land it

On PASS: push to the designated branch with `git push -u origin <branch>`
(retry a network failure up to 4 times, 2s/4s/8s/16s). Open a PR **only** if
`AUTO_PR: yes` in the backlog config; if you do, follow
`.github/PULL_REQUEST_TEMPLATE.md`.

Then update `docs/BACKLOG.md`: move the row to `Done` with the date, the commit
sha and one line on what shipped. Anything the verifier flagged as
*unverifiable here* (no live cluster, no Windows or macOS box, no display) goes
into the Inbox as its own verification item — that debt is exactly what this
repo has repeatedly lost track of, and dropping it silently is how three
release binaries shipped unable to start.

## 6. Keep the Inbox fed — `kn-researcher`

Run `kn-researcher` in the **background** (`run_in_background: true`) when either
holds: the Ready table is below `READY_POOL_MIN`, or `RESEARCH_EVERY` cycles have
passed since the last dated report in `docs/research/`. Otherwise skip it — it is
the expensive path and a repeated survey of the same market is noise.

Give it a specific brief, not "research competitors": one theme per run, chosen
from what the backlog is thin on. Its proposals go into the **Inbox** table only,
with priority left blank for the human. Never into Ready.

## 7. Report

Six lines or fewer, and no summary of your own reasoning:

- item id + title, and the outcome (`done` / `blocked` / `needs-fix` / none);
- the verifier's verdict and, if FAIL, the one finding that mattered;
- what shipped, in a user's words;
- what could not be verified here;
- whether the researcher was started, and on what brief;
- how many items sit in the Inbox awaiting the human's priorities.

Then end the cycle. The next tick starts at step 1 again.
