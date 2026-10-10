# kubeNimbus backlog

The backlog lives in [GitHub issues](https://github.com/Shman4ik/kubeNimbus/issues?q=is%3Aissue%20is%3Aopen%20label%3Aroadmap)
since 2026-10-10. This file says how those issues are labelled and how the release train
(`/release-train`, see `CLAUDE.md` → "The release train") reads and writes them, and it keeps
the list of proposals that were deliberately not taken.

Useful views:

- [Every open backlog item](https://github.com/Shman4ik/kubeNimbus/issues?q=is%3Aissue%20is%3Aopen%20label%3Aroadmap)
- [Ready](https://github.com/Shman4ik/kubeNimbus/issues?q=is%3Aissue%20is%3Aopen%20label%3Aready) — the owner's forced candidates
- [Agent-ready](https://github.com/Shman4ik/kubeNimbus/issues?q=is%3Aissue%20is%3Aopen%20label%3Aagent-ready) — what a train can take without a human at a keyboard
- [Verification debt](https://github.com/Shman4ik/kubeNimbus/issues?q=is%3Aissue%20is%3Aopen%20label%3Averification)

## Why issues, not this file

By October this file had grown to 527 lines and about 170 KB. Every train loaded all of it
to read a handful of rows, two IDs had been issued twice before anyone noticed, and rows
stayed open for work that had already shipped because closing one meant editing a table
by hand. An issue is filtered by label, linked from the PR that works on it and closed by
the PR that ships it. It is also visible to people outside the project: the pre-launch
checklist had already noted that an empty tracker at launch reads as "not really open to
contributors". pgNimbus keeps its roadmap the same way, under the same label names.

## Where the old rows went

- **Every open row became one issue** (#154–#257). The title is `<ID> · <item>`, so the IDs
  that `CLAUDE.md`, `docs/research/` and `docs/status-history.md` cite (`VER-15`, `ENG-21`, …)
  still find their item through the issue search. The body carries the row's *done when*,
  its signal, the notes this file kept about it, and a link to the row as it stood.
- **Shipped and rejected rows were not migrated.** They are in
  [the file as it stood before the move](https://github.com/Shman4ik/kubeNimbus/blob/93d28da/docs/BACKLOG.md),
  and what shipped is also in `CHANGELOG.md` and `docs/status-history.md`. The rejections
  that should stop a proposal from coming back are repeated at the end of this file.
- **New issues get no backlog ID.** The issue number is the ID; the old prefixes are kept
  only because so much already refers to them.

## Labels

Each label set mirrors a column the old tables had.

| Label | Meaning |
|---|---|
| `roadmap` | A backlog item, as opposed to a bug report or question filed from outside. The release train only selects from these (and from bugs it chooses to adopt). |
| `P0`–`P3` | Priority. **Only the owner sets it.** The train scores unprioritised issues itself and records the score in `docs/product-loop/TRAIN.md`, never on the issue. |
| `size: S` / `size: M` / `size: L` | About a session / about a day / several sessions and its own design pass first. A train only ever takes an S or M slice of an L. |
| `verification`, `enhancement`, `distribution`, `engineering` | The old sections: verification debt (claimed but never checked where it matters), product gaps, distribution and adoption (no change to the app), engineering hygiene. A defect in shipped behaviour may carry `bug` as well. |
| `agent-ready` | An agent container can take it from nothing to a verified, pushable change: build, both TUnit suites, the screenshot harness, the linux-x64 NativeAOT publish and `--smoke-test` under Xvfb. This is the old `can do here` mark. |
| `needs: cluster`, `needs: desktop`, `needs: Windows`, `needs: Mac`, `needs: human` | Who can start it: a real API server (the sandbox or more), a real desktop session (a display, a mouse, a terminal emulator; `kn-qa` covers Windows, and runs only inside the owner's Hyper-V QA VM, never on the owner's own desktop), a platform, or the owner (a decision, an account, a certificate, a repository permission). The mark is about reachability, not worth: several `needs: cluster` items matter more than anything `agent-ready`. The issue's first line keeps the original wording, which is often more precise. |
| `ready` | Owner-validated. Every `ready` issue is a candidate in every SELECT phase, and a `ready` `P0`/`P1` issue is forced into the next train unless it cannot be built and verified where the train runs. |
| `blocked` | Reverted off a train after `MAX_FIX_ROUNDS`, or waiting on something outside the repository. A comment says which, with the precise finding. |

## Lifecycle

The old Status column (`inbox` → `ready` → `in-progress` → `needs-fix` → `done`, or
`blocked` / `rejected`) maps onto issue state like this:

- **Inbox** — an open `roadmap` issue without `ready`. This is the evidence pool the train
  mines; it never re-researches what an issue already cites.
- **Ready** — the owner adds `ready`. Removing it demotes the item without losing it.
- **In progress, needs fix** — tracked in `TRAIN.md` on the train branch, where each item's
  spec names its issue. Nothing on the issue changes while a train works on it.
- **Done** — closed as completed by the PR that ships it. The train PR's body carries a
  `Closes #N` line for every landed item, so merging the release closes them; a bundle PR
  does the same for its own items.
- **Blocked** — the `blocked` label and a comment with the failing finding.
- **Rejected** — closed as *not planned*, with the reason in a comment. A closed issue stays
  searchable, so the reason is found the next time the same proposal comes up.

**Filing.** Verification debt the verifier could not pay and out-of-scope findings become
issues in the same step that found them, labelled `roadmap`, type, size and feasibility —
never a priority. A session whose GitHub token cannot create issues (a cloud session's app
token may lack `issues: write`) writes each one, title, labels and body, under
`## Issues to file` in `TRAIN.md` instead; the next session that can file them does, and
removes the section.

## Rejected / deliberately not doing

Kept so the same proposal does not come back every quarter.

| Item | Why |
|---|---|
| Long-range metrics history | Prometheus's job. `UsageHistory` is bounded to the session and never persisted — a permanent non-goal. |
| Helm install / upgrade / rollback | Read-only browsing is the deliberate scope; mutation stays Helm's. Revisit only with real demand evidence. |
| Cluster provisioning, in-cluster agents, telemetry | Permanent non-goals. |
| An update check of any kind (DIST-3, #229) | Decided by the owner on 2026-10-10: the README's promise of no network connection other than to the clusters stands. GitHub's Watch → Releases tells people about new versions, and the Microsoft Store build updates itself. |
| A per-cluster "allow untrusted certificate authorities" setting (ENG-21, #241) | Refused by the owner on 2026-10-10: `insecure-skip-tls-verify` in the kubeconfig already does it and is flagged; see docs/engineering/connecting.md. |
| Coalescing same-named CRD kinds into one sidebar row | Rejected once already: nesting inside a 100-kind section costs more than the group label it would replace. |
| A cluster overview, a "Cluster issues" panel, a choice of landing surface (FEAT-9, FEAT-24, FEAT-26) | Superseded by the Applications mode, which is the "what is broken right now" screen and the landing surface, persisted in the workspace. Node warning conditions, the one part it does not cover, are on node detail. |
| Structured (JSON) log rendering (FEAT-32) | Rejected by the owner on 2026-09-27: the services looked at do not log JSON. |
| A control for the drain's grace period (ENG-25) | Each pod's own grace period is almost always right, and the XML doc on `DrainOptions.GracePeriodSeconds` says so. A control would be the always-visible control UI rule 1 says no to; kubectl keeps `--grace-period` for the rare case. |
