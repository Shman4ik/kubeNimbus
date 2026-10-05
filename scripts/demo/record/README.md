# Recording the README GIFs

Ported from pgNimbus's `scripts/demo/record`. One scene script per GIF in
`design/screenshots/`, driven by `demo-lib.ps1` (Win32 window placement, real key and mouse
events, an ffmpeg `gdigrab` region capture). Each scene starts the NativeAOT build on an
**isolated profile** (`KUBENIMBUS_PROFILE_DIR`, `KUBECONFIG` pointing at an empty file), so no
real cluster or setting is ever read, restores one tab on the built-in demo cluster, and
writes `%TEMP%\kn-demo\out\<scene>.mp4` (`KN_DEMO_OUT` overrides it).

| Scene | GIF |
|---|---|
| `scenes/applications.ps1` | `applications-demo.gif` |
| `scenes/palette-logs.ps1` | `palette-logs-demo.gif` |
| `scenes/unhealthy-logs.ps1` | `unhealthy-logs-demo.gif` |

The demo cluster needs no sandbox, no Docker and no network, and its data is the same on every
run, so a take can be repeated until it is clean.

## Rules

- Never point it at real app data. `Prep-Data` builds a fresh profile for every scene.
- Input is sent only while kubeNimbus is in front (`Assert-Front`).
- **Never send Escape.** It is Claude's stop key and turns desktop control off. Close popups by
  clicking.
- The capture box is 1424x892 in the middle of a 3840x1600 desktop, well inside the edges
  (computer-use draws a glow on the monitor edges). Do not use `gdigrab title=`: Avalonia gives
  a black frame. `Scroll` takes a **positive** count to scroll down.
- Clicks need `Start-Rec -Mouse`, or the pointer is not in the video.

## Steps

```powershell
# 1. The shipping build (PowerShell only: Git Bash fails at link on vswhere)
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
dotnet publish src/KubeNimbus.App -c Release -r win-x64 -p:PublishAot=true -o publish\app

# 2. Record every scene (or one: run its script directly, -Dry to rehearse without recording)
.\scripts\demo\record\record-all.ps1
```

```bash
# 3. Read a contact sheet before trusting a take, then encode (width 1000, 10 fps, 0.2-1.5 MB)
scripts/demo/record/sheet.sh "$TEMP/kn-demo/out/applications.mp4" sheet.png 4 4
scripts/demo/record/make-gif.sh "$TEMP/kn-demo/out/applications.mp4" design/screenshots/applications-demo.gif 14.5
```

## Why there is no cold-start GIF

pgNimbus has one. Here the same recording shows about a second from launch to a drawn window
(`--smoke-test` measures 1.1 s wall on this Windows machine, process start and exit included),
far from the ~150 ms the README quotes for linux-x64. The GIF would contradict the claim it
was meant to illustrate, so it is not published until the Windows figure is understood.
