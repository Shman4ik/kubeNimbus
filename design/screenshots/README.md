# README screenshots

Every PNG in this directory is **generated**, not hand-captured — same rule as
`design/masters/` (see [`../LOGO-ASSETS.md`](../LOGO-ASSETS.md)). They come out
of the headless harness in [`tools/Screenshot`](../../tools/Screenshot), which
renders the *real* Views bound to fixture ViewModels, so they can be rebuilt on
any machine with no display and no cluster.

**Rebuild them on Windows.** "Any machine" is true of the harness and not of these
images: the log, YAML, exec and every other monospace pane ask for
`Cascadia Mono,Consolas,monospace`, both named faces ship with Windows only, and
the harness bundles no monospace font of its own (Inter is the only one it
loads). On Linux or macOS those panes fall back to whatever the platform's font
matching gives them, which is not the face these images were made with — so a
refresh from there silently changes every pane the README shows, not only the one
you touched. The same commit rendered twice on one Windows machine does come out
byte-identical, except for the panes that merge several live log streams (the
workload logs pane and the Applications page's merged view), whose line order
depends on which flush tick a replayed line lands in; none of the images below is
one of those.

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
| `exec-terminal.dark.png` | `cluster-tab-exec-fullscreen-maximized` |

Two of these are the **maximized** variant of their scenario
(`yaml-editor.dark.png`, `exec-terminal.dark.png`), and that is not a stylistic
preference: a gallery cell renders at half the table's width, and a ~300px
inspector dock inside a 1280px window shrinks to a band whose text nobody can
read. The pane is the subject of those two images, so it gets the whole content
area.

Only the hero image is checked in for both themes — GitHub's `<picture>` element
switches it with the reader's theme. The gallery below it is dark-only, to keep
the repository from carrying twice the bytes for a marginal gain.

**The data is synthetic.** Cluster names, pod names, usage numbers, RBAC
subjects and Secret values all come from the demo cluster's dataset,
[`src/KubeNimbus.App/Demo/Fixtures`](../../src/KubeNimbus.App/Demo/Fixtures), which
the harness shares with the app's "Explore demo cluster" — no real cluster
was screenshotted, and nothing here needs redacting. The README says so under
the gallery, and it should keep saying so.
