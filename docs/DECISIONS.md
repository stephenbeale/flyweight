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
