# Microsoft Store presentation

The listing has the same presentation pieces as pgNimbus: the app icon and
poster, eight screenshots, a short feature trailer with a thumbnail, and a
wide hero image behind the Store's title and install button. Listing text and
the upload fields live in [listing/store-listing.md](listing/store-listing.md).

## Generated media

| File | Source | Generator |
|---|---|---|
| `SuperHeroArt-16x9-1920x1080.png` | `screenshots/01-applications-list.dark.png` | `scripts/windows/make-store-hero.ps1` |
| `trailer/kubenimbus-trailer.mp4` | The three committed `design/screenshots/*-demo.gif` recordings and the dark wordmark | `scripts/windows/make-store-trailer.ps1` |
| `trailer/kubenimbus-trailer-thumbnail.png` | The trailer at 5 seconds | `scripts/windows/make-store-trailer.ps1` |

Run from the repository root on Windows:

```powershell
pwsh scripts/windows/make-store-hero.ps1
pwsh scripts/windows/make-store-trailer.ps1
```

The hero generator uses System.Drawing and copies pgNimbus's layout; only the
product screenshot differs. Keep that layout aligned with pgNimbus, as required
by [CLAUDE.md](../CLAUDE.md). Existing logo and poster generators are unchanged.
The trailer generator needs ffmpeg on PATH, uses Segoe UI for captions, and
never launches the app or sends input. It reads only committed sources and
cleans up its own unique temporary directory. Both generators accept `-OutDir`
for a scratch rebuild; the hero also supports `-Size 3840x2160`.

The UI footage is real Windows input on the built-in demo cluster, recorded for
the README GIFs. The source GIFs are 1000×626 at 10 fps; the trailer scales them
to 1424×892 on a 1920×1080 canvas. Encoding at 30 fps makes fades smooth but
does not add intermediate UI states. A future high-resolution re-recording
must run inside the owner's QA VM, following the repository's desktop rule.
The demo labels remain in the recordings; no real cluster or credentials appear.

## Verification

Inspect a contact sheet across all scenes, transitions and the closing card,
then check the stream information:

```powershell
ffprobe -v error -show_entries format=duration,size:stream=codec_name,profile,width,height,pix_fmt,r_frame_rate,sample_rate,channels -of json design/store/trailer/kubenimbus-trailer.mp4
ffmpeg -v error -i design/store/trailer/kubenimbus-trailer.mp4 -f null -
```

Expected: 34.5 seconds; 1920×1080 H.264 High/yuv420p at 30 fps; AAC-LC stereo
at 48 kHz. The MP4 has Fast Start metadata, a closed GOP and two B frames.
The trailer thumbnail must be 1920×1080, and the hero must be an opaque
1920×1080 PNG. Match these against Microsoft's
[screenshots, images and trailer guidance](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
when upload requirements change.

## Partner Center upload

1. Open kubeNimbus (`9MZ3C28M65PB`) and start a listing update, retaining the
   currently published package, price, availability and privacy URL.
2. Update both English and English (United States). Select the reserved name
   `kubeNimbus - Kubernetes Client` and paste the reviewed short description,
   description and product features from `listing/store-listing.md`.
3. Keep the eight screenshots in their documented order and add their captions.
4. Under Trailers and additional assets, upload the MP4, its thumbnail and
   title, and the 16:9 Super hero art. These are separate upload fields; the
   poster is not a substitute for the hero.
5. Save, reopen the listing and verify the text and media previews. Submit the
   listing update for certification. Store publication follows Microsoft's
   review; uploading files alone does not update the public listing.

Nothing in CI publishes Store listing content. Partner Center is the place to
verify whether a prepared or submitted listing has reached the public Store.
