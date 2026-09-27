# Microsoft Store screenshots

The screenshots for the Microsoft Store listing, in the order the listing shows them.
Like [`../../screenshots`](../../screenshots/README.md), every PNG here is **generated**
by the headless harness in [`tools/Screenshot`](../../../tools/Screenshot), from the demo
cluster's synthetic dataset, and the same rule applies: **rebuild them on Windows**, because
the monospace panes ask for Cascadia Mono or Consolas, which only Windows ships.

They are a separate set from the README's because of size. The Store asks for desktop
screenshots of 1366×768 or larger, and every ordinary harness scenario renders at 1280
wide. The `store-*` scenarios render the same fixtures at 1920×1080, the size a listing is
most often viewed at. Half are the light theme and half the dark, alternating, so the
listing shows both without one dominating.

To refresh them after a UI change:

```bash
dotnet run --project tools/Screenshot -- /tmp/kubenimbus-store store-
```

then copy the theme each file names:

| This file | Harness scenario |
|---|---|
| `01-applications-list.dark.png` | `store-applications-list` |
| `02-applications-page.light.png` | `store-applications-page` |
| `03-pod-detail.dark.png` | `store-pod-detail` |
| `04-yaml-editor.light.png` | `store-yaml-editor` |
| `05-fleet-list.dark.png` | `store-fleet-list` |
| `06-cluster-switcher.light.png` | `store-cluster-switcher` |
| `07-exec-terminal.dark.png` | `store-exec-terminal` |
| `08-rbac-who-can.light.png` | `store-rbac-who-can` |

The first one is the Applications list because that is the screen the app opens on. The
two Applications shots carry the demo cluster's banner, which says the data is sample
data; keep it in, for the same reason the README says so under its gallery.

Uploading them to Partner Center is a manual step; nothing in the release workflow does it.
