# The release train

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "The release train" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## The release train

Work is shipped as **trains**: one train is one release carrying 5–10 deliverables,
every few days. `/release-train` ([`.claude/skills/release-train/SKILL.md`](../../.claude/skills/release-train/SKILL.md))
runs it one step per invocation — SURVEY (repo scan + a background competitor
delta) → SELECT (fresh scored candidates, 5–10 picked, a spec each) → BUILD (one
item per step) → HARDEN (regression sweep, performance gate, polish pass, product
review) → RELEASE → RECORD — and is driven by `/loop /release-train`, normally in a
Claude Code cloud session. Three agents do the heavy lifting: `kn-implementer`
(Opus, medium effort, builds one item), `kn-verifier` (Sonnet, high effort, re-runs
the checks and reviews against the rules above, with no Edit tool so it cannot
quietly fix what it should be reporting), and `kn-researcher` (Opus, medium effort,
the competitor delta and matrix). Effort is set in each agent's front matter
(2026-09-29, from Anthropic's Opus 5.5 / Sonnet 5.5 prompting guides): Opus 5.5 at
medium does what Opus 5 did at high, while Sonnet at low can report a change done
without running the check and at medium can stop to check in on a long task, and
running the checks to the end is the verifier's whole job. Every
agent prompt also says what ends its run — a message with no tool call is the
agent's final report, so a mid-task status note that announces the next step
stops the work there — and forbids self-started review rounds and extra scope.
Outside the train,
`kn-bundle` (Opus, high effort) builds a *bundle* of related backlog issues as one PR, for
parallel runs where the owner wants fewer, larger PRs; bundles never edit `CHANGELOG.md` or
`status-history.md` and never file or close issues themselves, which the orchestrating session
does afterwards from the PR body. Its files live
in [`docs/product-loop/`](../../docs/product-loop/): `TRAIN.md` (the live state),
`CURRENT_STATE.md`, `COMPETITOR_MATRIX.md`, and `history/<date>-v<version>/` for
every shipped train.

It replaced `/backlog-cycle`, which shipped one owner-approved item per cycle and
never released. Five things about the train are load-bearing:

1. **The train selects its own work; the owner steers rather than gates.** The old
   loop could only take items a human had put in Ready, which kept a person in the
   loop and also meant the queue ran dry whenever that person was busy. Now the
   `ready` issues are a set of *forced candidates* (P0/P1 ones enter the plan unless they
   are infeasible where the train runs), the other open backlog issues are an evidence
   pool, and the
   owner's levers are `TRAIN.md`'s Config and Owner notes, applied at the start of
   every step: pin, veto, pause, or change the release mode.
2. **State lives in `TRAIN.md` on the train branch, and every step pushes it.** A
   cloud container is discarded with its session, so a step that only committed
   locally did not happen, and a fresh session finds the live train by its
   `train/*` branch.
3. **Verification debt is still an item, not a footnote.** Whatever the verifier
   reports as unverifiable in its environment — no live cluster, no Windows or macOS
   box, no display — becomes its own backlog issue in the same step. This repo has
   repeatedly lost track of exactly that, and the cost is on record: every release
   RID shipped a binary that could not start, because `ci.yml` published the AOT
   output and never launched it.
4. **`MAX_FIX_ROUNDS` ends in a revert, not a stall.** An item still failing
   verification is reverted off the train branch and marked `blocked` with the
   precise finding; the train moves on without it.
5. **The next train starts from a new survey.** Items 11–20 of the last ranking are
   not a queue — the repository and the market have both moved since they were
   scored.
