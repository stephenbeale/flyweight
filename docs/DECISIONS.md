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
Old projects: `ProjectSession.Load` runs `TimelineOps.UpgradeLegacyStills`, which turns image files saved as
~0.04 s videos into stills (and their zero-length clips into 8s ones) and marks the project dirty. Still proxies
use a `-still` cache key so the old 0.04 s proxies aren't reused.

### D18. Custom annotation style panel instead of PropertyGrid
In the first real run the PropertyGrid fought the owner: colours opened only from a tiny ▾ (not by clicking the
swatch), and a drop-down opacity slider rendered off-screen. `Controls/AnnotationStylePanel` replaces it with
click-to-pick colour swatches (ColorDialog), inline sliders with a value read-out, and a fill/background toggle
that switches on automatically when you pick a fill colour or opacity. The panel never mutates the project. It
raises `Edit(action, phase)`: `Single` → `Execute` (one undo step); slider drags send `Live` → `BeginEdit` +
`Preview`, then `Commit` → `CommitEdit`, so a whole drag is one undo step (rule 2).

### D19. Thumbnails before proxies, in their own queue
Proxies transcode one at a time (`_jobs`), and a 4K proxy takes minutes. Thumbnails (320×180, fit/padded, from the
ORIGINAL) now run first in their own 2-wide queue, so the bin, timeline and preview get a picture in about a second.
While a clip's proxy isn't ready the preview shows a stand-in: the photo itself for stills (EXIF-rotated, capped to
~1080p in memory), or the thumbnail for videos, with an "Optimising for playback…" badge.

### D21. Annotations that span cuts are linked pieces
Rule 4 (each annotation is anchored to ONE clip's source time) stays. To let an annotation run across a cut,
dragging a bar's end/start past its clip creates pieces on the neighbouring clips that share `Annotation.LinkId`
(`TimelineOps.ExtendAnnotationEnd/Start`, rebuilt on every drag step so dragging back removes them). Each piece
stays glued to its own footage. Look and position are synced across the run (`SyncLinked`, called from the
panel, canvas drags and text edits); deleting one piece deletes the run; moving one piece's body detaches it.
Split now links the two halves of an annotation it cuts. Export needs no change: pieces are ordinary annotations.
Moving a bar keeps its length fixed: it belongs to the clip under its middle (`MoveAnnotation`).

### D20. Flyweight.App is in the solution
It was missing from `Flyweight.sln`, so `dotnet build Flyweight.sln` (and the CI "App build (Windows)" job)
only built Core. This hid a compile error and made local "builds" silently run a stale exe. Now added.

### D22. Clip volume and mute live on the clip
`Clip.Volume` (gain 0–2, i.e. 0–200%, default 1) and `Clip.Muted` are separate, so unmuting restores the level.
`EffectiveVolume` is 0 when muted. Edits go through `TimelineOps.SetClipVolume/SetClipMuted`, and split halves
copy both. Export adds `volume=` to the clip's audio chain only when the gain isn't 1. A muted or 0% clip exports
as `anullsrc` silence, so its audio isn't decoded. Preview sets LibVLC's volume (100 = as recorded, up to 200)
on every playback tick, because the audio output can reset it when new media loads. Old project files load at
100%. The clip panel's slider is shared with the annotation panel (`Controls/Slider.cs`).

### D23. Audio track: pure audio at fixed timeline times
- **Model.** `Project.AudioTrack` holds `AudioItem`s with `Start` in TIMELINE time plus source `In`/`Out`, `Volume`
  and `Muted`. Trimming or reordering video doesn't move them (the owner chose this over "follow the clip").
  `SourceMedia.IsAudioOnly` marks MP3/WAV/M4A etc. (no video stream; cover art doesn't count). `InsertMedia` refuses
  audio-only media and `AddAudio` refuses video. Old project files load with an empty track.
- **Import.** An audio file goes on the audio track at the playhead (at 0 if the playhead is at the end).
  `TrimAudioIn` keeps the item's end fixed. No proxy is made: playback uses the original file. The bin and timeline
  "thumbnail" is a `showwavespic` PNG of the whole file, cropped to each item's In/Out.
- **Export.** One input per audio item, each `adelay`ed to its start, then `amix` with `normalize=0` (the default
  would halve the clip audio) and `duration=first`, so the video decides the length. Muted items and items starting
  after the video's end are left out.
- **Mono.** Mono is copied to both channels at full level (`pan`), as LibVLC plays it: ffmpeg's default upmix is
  3 dB quieter. For this, `SourceMedia.AudioChannels` is probed (0 = unknown, the old behaviour).
- **Preview.** `AudioLanePlayer` runs one LibVLC player per item currently playing, started, stopped and drift-corrected
  from the engine tick. Stops run on the thread pool.
- **DirectSound output.** LibVLC uses `--aout=directsound`. The default `mmdevice` output sets volume on the whole
  app's audio session, so the video player and the audio-track players overwrote each other's volume every tick, and
  muting a clip garbled everything.

### D24. Playback runs on a real-time clock
The playhead used to follow the video player's time. The video stalls for a few hundred ms at every cut while the
next proxy loads, so the timeline ran slower than real time. The audio track drifted ahead and was pulled back,
which audibly repeated music. Now `Play` starts a stopwatch clock and `Tick` sets `Position` from it. The video
follows: at a cut it loads at the clock's time, and `KeepVideoInStep` seeks it forward once it is more than 250 ms
off (proxies are all-intra, so this is cheap). Adjacent halves of a split still play without a reload. The preview
now takes exactly as long as the export.
