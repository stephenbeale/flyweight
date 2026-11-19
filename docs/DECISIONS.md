# Decisions log

Short records of *why*. Newest at the bottom. Add an entry when you make a choice someone might later question.

---

### D1. WinForms UI, FFmpeg for all media work
WinForms is quick to build in and the owner knows .NET. It only does UI; decoding/encoding is never hand-written.
WPF/Avalonia were considered; Avalonia is the upgrade path if cross-platform is ever wanted.

### D2. Edit decision list; originals are never modified
A clip is `(source, in, out)`. The whole project is small JSON, which makes save, autosave and undo trivial.

### D3. Proxy ("optimise") on import
Phone/camera footage is long-GOP H.264/HEVC, often 10-bit and variable frame rate. That is the main reason
editors like Resolve/Clipchamp lag and crash. Every import gets a 960×540, constant-frame-rate, **all-keyframe**
proxy for preview. That gives fast, frame-accurate scrubbing and cheap decode. Export still uses the originals.

### D4. Frame-accurate scrubbing strategy
With all-intra proxies, a LibVLC seek lands on the exact frame. Rapid scrub seeks are throttled (40 ms) and
coalesced. ←/→ step one frame.

### D5. Output is 16:9 only, aimed at YouTube
Owner decision: no vertical/9:16 for now. Non-16:9 sources are fitted with black bars (blurred fill is a later
option). Export presets are fixed: 1080p / 1440p / 4K, H.264 High CRF 18, AAC 320k 48 kHz, `+faststart`.
1440p is offered because YouTube gives 1440p+ uploads a better encode.

### D6. Annotations (arrows, boxes, text) are a core feature, not an add-on
Owner's key requirement. Model: anchored to a clip in **source time** (so they stay on the footage when you
trim or reorder); normalised 0–1 coordinates on the 16:9 canvas; sizes as "px at 1080p".
Splitting a clip moves annotations to the half they start in and duplicates ones that straddle the cut.

### D7. Preview renders video into memory, not into LibVLC's VideoView
VideoView is a native window, and WinForms can't draw over it ("airspace"). LibVLC video callbacks decode into
a buffer that we paint, with annotations drawn on top.

### D8. One annotation renderer for preview and export (WYSIWYG)
Export renders each annotation to a transparent full-frame PNG with the same GDI+ code as the preview, and
ffmpeg overlays it with `enable='between(t,a,b)'`. Static annotations only for now; animated ones would need
per-frame rendering.

### D9. ffmpeg called directly (not FFMpegCore)
Originally planned FFMpegCore. A thin `Process` wrapper gives full control over `filter_complex` and progress
parsing with less surface area. Each clip is its own input with `-ss/-t` before `-i` (fast and accurate seeking).

### D10. Snapshot undo
Undo stores whole-project JSON snapshots (cap 200) rather than inverse commands. It's simple and can't get subtly
wrong. Drags collapse into one step via `BeginEdit/CommitEdit`.

### D11. Crash safety
ffmpeg runs out of process. Projects save atomically (temp + move). Autosave every 10 s to
`%LocalAppData%\Flyweight\autosave.flyweight`; a `session.lock` file detects unclean exits and offers recovery.

### D12. Licensing
Repo is MIT. ffmpeg is invoked as a separate executable (not linked); LibVLC is LGPL. When distributing binaries,
prefer an LGPL ffmpeg build or accept GPL terms for the bundled ffmpeg (x264).

### D13. No database (yet): Dapper + SQLite only when a feature needs querying
Owner asked about Dapper. There is no SQL today: a project is one JSON file, which keeps save/undo/autosave
trivial and projects portable. **If** a cross-project media library (tags/search), a proxy-cache index
(size/last-used/auto-cleanup) or a large presets store is built, use **SQLite + Microsoft.Data.Sqlite + Dapper**,
in Core, behind an interface (e.g. `IMediaCatalog`). The project file stays JSON regardless.

### D14. No Docker for the app; GitHub Actions for CI
A WinForms desktop app can't usefully run in a container, and users get a normal exe/installer. For reproducible
testing use GitHub Actions: an Ubuntu job (apt ffmpeg, `dotnet test`, including export pixel checks) plus a
Windows job (build Flyweight.App). Revisit Docker only if a server component appears (cloud render, licence/update server).

### D15. Edit on proxies, export from originals (don't replace originals with downscaled copies)
Owner asked: why not scale videos down on load, since the output is compressed anyway? That is what the proxy
step (D3) already does: all editing uses 960×540 proxies, and originals are only read once, at export. We
deliberately do NOT replace originals with output-size copies, because that would mean double compression
(quality loss), no way to change output resolution upward later, and no headroom to zoom/crop. Zooming into
part of a 4K frame matters for tutorial/annotation videos.
Accepted improvements: choose output size when creating a project (v0.1); later, a choosable proxy quality
(540p/720p), an opt-in "save disk space" mode (keep an output-size copy instead of the original), and exporting
from a high-quality mid-size copy if export speed becomes a problem.

### D16. Icon: gold boxing glove inside a single film frame
Plays on the name/tagline ("punches above its weight"). The tile is one frame of film (sprocket holes on both
edges) with a gold glove mid-punch in the blue picture window. The owner chose this over a film-strip cuff and a
film strip behind the glove (both in `assets/concepts/`, compared in `comparison.png`).
Rule: **no red + white play button**. Early drafts read as the YouTube logo (confusing and a trademark risk).
Source: `assets/icon.svg`; rendered `assets/icon-128.png`/`icon-512.png`; multi-size
`src/Flyweight.App/flyweight.ico` (16–256 px). Re-render the PNGs/ICO if the SVG changes.

### D17. Still images and freeze frames are first-class "stills"
Images are probed as stills (`SourceMedia.IsStill`), never as near-zero-length videos (that caused unbounded
ffmpeg output and a never-finishing export). Defaults: 8s on the timeline (`DefaultStillLength`), 5s for a freeze
frame, and a 5-minute cap (`StillMaxDuration`) so the looped proxy stays cheap (~8s to build for 5 min). Export loops
the original image at the fps of the first non-still clip and errors on clips shorter than `MinClipLength`.
Freeze frames are full-res PNGs extracted from the original into `%LocalAppData%\Flyweight\frames`.
Known limitation: that folder is not portable with the `.flyweight` file, so a project moved to another machine
loses its freeze-frame images. Revisit (e.g. store beside the project) if this bites.

### D18. Export frame as image; exports and freeze frames default next to the largest video
Owner request. **Export frame** (toolbar, Ctrl+Shift+E, right-click on the preview) saves the frame under the
playhead as a PNG at the project's output size. The frame comes from the ORIGINAL file, fitted to the 16:9 canvas
(`FrameExport`), and the annotations visible at that moment are drawn on with `AnnotationRenderer` (`FrameRenderer`),
so it matches the preview and the video export. It is added to the **media bin immediately, highlighted**, but not
to the timeline. (Freeze frame, D17, is the "insert into the timeline" counterpart.)
**Default location** for exported frames, the video export and **freeze frames** is the folder of the **largest
video file** in the project (the main recording), falling back to Videos. This resolves D17's limitation:
freeze frames no longer hide in `%LocalAppData%`, so they travel with the footage. Names come from the project file,
else that video ("Holiday - edit.mp4", "Holiday frame 1m23s.png", "Holiday freeze 0m12s.png"); exports never
overwrite ("name (2).png").

