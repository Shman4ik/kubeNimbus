# README screenshots

Every PNG in this directory is **generated**, not hand-captured — same rule as
`design/masters/` (see [`../LOGO-ASSETS.md`](../LOGO-ASSETS.md)). They come out
of the headless harness in [`tools/Screenshot`](../../tools/Screenshot), which
renders the *real* Views bound to fixture ViewModels, so they can be rebuilt on
any machine with no display and no cluster.

**Rebuild them on Windows.** "Any machine" is true of the harness and not of these
images: the interface is drawn in the platform's own face by default (DESIGN.md rule 22),
and these images show it as Windows users see it, in Segoe UI. Rendered on Linux or macOS
the same scenarios draw their interface text in that platform's face (DejaVu Sans in the CI
container), so a refresh from there silently changes every image, not only the one you
touched. Code text is not the reason any more: the log, YAML, exec and every other
monospace pane draw in the bundled JetBrains Mono NL on every platform. (Until 2026-10
they asked for `Cascadia Mono,Consolas,monospace`, which only Windows ships.) The same
commit rendered twice on one Windows machine does come out byte-identical, except for the
panes that merge several live log streams (the workload logs pane and the Applications
page's merged view), whose line order depends on which flush tick a replayed line lands
in; none of the images below is one of those.

To refresh them after a UI change:

```bash
dotnet run --project tools/Screenshot -- /tmp/kubenimbus-screenshots
```

then copy the ones the README uses:

| This file | Harness scenario |
|---|---|
| `applications-list.light.png` / `.dark.png` | `applications-list` |
| `application-page.light.png` | `applications-page-crashloop` |
| `pod-detail.dark.png` | `cluster-tab-pod-detail` |
| `exec-terminal.dark.png` | `cluster-tab-exec-fullscreen-maximized` |
| `yaml-editor.light.png` | `cluster-tab-yaml-editor-maximized` |
| `cluster-switcher.light.png` | `main-window-switcher` |
| `fleet-list.dark.png` | `cluster-tab-fleet-list` |
| `workloads-list.dark.png` | `cluster-tab-workloads-list-metrics` |
| `rbac-who-can.light.png` | `cluster-tab-rbac-who-can` |

Two of these are the **maximized** variant of their scenario
(`yaml-editor.dark.png`, `exec-terminal.dark.png`), and that is not a stylistic
preference: a gallery cell renders at half the table's width, and a ~300px
inspector dock inside a 1280px window shrinks to a band whose text nobody can
read. The pane is the subject of those two images, so it gets the whole content
area.

The hero is the Applications list, the screen a cluster opens on, and it is the one
image checked in for both themes — GitHub's `<picture>` element switches it with the
reader's theme. The gallery under it is half light and half dark, laid out as a
checkerboard (light left in odd rows, right in even ones), so a reader of either theme
sees that the app has both and neither theme dominates the page. Each gallery image is
checked in for one theme only, to keep the repository from carrying twice the bytes; the
theme is in its filename, and a swap is a re-copy plus the README's `src`.

The Microsoft Store listing has its own set, at the size the Store asks for, in
[`../store/screenshots`](../store/screenshots/README.md).

**The data is synthetic.** Cluster names, pod names, usage numbers, RBAC
subjects and Secret values all come from the demo cluster's dataset,
[`src/KubeNimbus.App/Demo/Fixtures`](../../src/KubeNimbus.App/Demo/Fixtures), which
the harness shares with the app's "Explore demo cluster" — no real cluster
was screenshotted, and nothing here needs redacting. The README says so under
the gallery, and it should keep saying so.

## The animated GIFs

`applications-demo.gif`, `palette-logs-demo.gif` and `unhealthy-logs-demo.gif` are not
harness output: a harness render is a still, and these show motion. They are screen
recordings of the NativeAOT build running on the built-in demo cluster (so the "Demo
cluster" banner is in every frame, on purpose), driven by scripted input. Scenes, the
capture box and the encode recipe are in
[`scripts/demo/record/README.md`](../../scripts/demo/record/README.md). Re-record them
after a change to the surfaces they show; like the PNGs, they are made on Windows.

| This file | Scene |
|---|---|
| `applications-demo.gif` | `scenes/applications.ps1` |
| `palette-logs-demo.gif` | `scenes/palette-logs.ps1` |
| `unhealthy-logs-demo.gif` | `scenes/unhealthy-logs.ps1` |
