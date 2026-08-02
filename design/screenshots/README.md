# README screenshots

Every PNG in this directory is **generated**, not hand-captured — same rule as
`design/masters/` (see [`../LOGO-ASSETS.md`](../LOGO-ASSETS.md)). They come out
of the headless harness in [`tools/Screenshot`](../../tools/Screenshot), which
renders the *real* Views bound to fixture ViewModels, so they can be rebuilt
identically on any machine with no display, no cluster and no Windows box.

To refresh them after a UI change:

```bash
dotnet run --project tools/Screenshot -- /tmp/kubenimbus-screenshots
```

then copy the ones the README uses:

| This file | Harness scenario |
|---|---|
| `workloads-list.light.png` / `.dark.png` | `cluster-tab-workloads-list-metrics` |
| `pod-detail.dark.png` | `cluster-tab-pod-detail` |
| `yaml-editor.dark.png` | `cluster-tab-yaml-editor-maximized` |
| `rbac-who-can.dark.png` | `cluster-tab-rbac-who-can` |
| `fleet-list.dark.png` | `cluster-tab-fleet-list` |
| `cluster-switcher.dark.png` | `main-window-switcher` |

Only the hero image is checked in for both themes — GitHub's `<picture>` element
switches it with the reader's theme. The gallery below it is dark-only, to keep
the repository from carrying twice the bytes for a marginal gain.

**The data is synthetic.** Cluster names, pod names, usage numbers, RBAC
subjects and Secret values all come from
[`tools/Screenshot/Fixtures`](../../tools/Screenshot/Fixtures) — no real cluster
was screenshotted, and nothing here needs redacting. The README says so under
the gallery, and it should keep saying so.
