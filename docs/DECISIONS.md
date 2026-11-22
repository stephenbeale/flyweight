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

### D19. Name stays "Flyweight"
Owner considered Bantam, Feather and Jet (2026-11-21) and chose to keep Flyweight: the name, tagline ("punches above
its weight") and glove icon tell one story. Bantam was the only real alternative (also a boxing weight, funnier);
Feather is crowded, and Jet loses the boxing link. Don't reopen unless the owner raises it.


### D25. Clip zoom and brightness/contrast: one set of maths for preview and export
- **Model.** `Clip.Zoom` (1-4), `PanX`/`PanY` (0-1: where the zoomed view sits, 0.5 = centred), `Brightness` and
  `Contrast` (-1 to +1). It is one static look per clip; animated zoom is not built. Old project files load as shot.
  Split halves and freeze frames copy the look. Edits go through `TimelineOps.SetClipZoom/Pan/Brightness/Contrast`.
- **Shared maths (`Core/Media/ClipLook`)**, the same idea as the one annotation renderer (rule 5):
  - *Zoom* works on the fitted 16:9 canvas. `ViewRect` is the visible part. The preview draws that part of the proxy.
    The export pads the ORIGINAL to 16:9 at its own resolution, crops the same rectangle and only then scales, so a
    zoomed 4K clip stays sharp.
  - *Brightness* is a gamma curve (it lifts dark and mid tones and keeps black and white). *Contrast* is a gain
    around mid-grey. `Tone()` is applied in RGB: the preview uses a 256-entry table, and the export uses `lutrgb`
    with the same expression and the same rounded (4 dp) constants. An integration test checks real exported pixels.
- **Annotations are not zoomed**: they stay where they were drawn on the canvas.

### D26. Proxies: robust, lower priority, and playable before they finish
- **Progress maths.** `ParseProgress` (out_time_us x 10 / ticks) used to divide by 10, so every progress figure
  (proxies AND export) showed 1/100 of the truth. Fixed and unit-tested.
- **Camera timecode.** DJI files carry a timecode track. The mp4 writer recreated it in the proxy and crashed at the
  end of a 14-minute file ("Assertion next_dts <= 0x7fffffff"). Proxies now map only `0:v:0` (+`0:a:0`) and use
  `-map_metadata -1 -write_tmcd 0`; export uses `-write_tmcd 0`.
- **Speed.** `ultrafast` plus `-hwaccel auto` was about 20% faster than `veryfast` on the CPU (1080p60 DJI, around 5x
  real time). If GPU decoding fails, the proxy retries once on the CPU. Decoding the original is the bottleneck, so
  bigger wins need a different approach (e.g. skipping proxies for easy files).
- **Priority.** Proxy transcodes run below normal priority so playback and the UI win. On Windows the priority is
  inherited at process creation (Chocolatey's ffmpeg shim starts the real ffmpeg straight away), so `FfmpegRunner`
  briefly lowers its own priority class while starting one, under a lock.
- **Play the original meanwhile.** While a video's proxy isn't ready, `PlaybackEngine` plays the original file
  (LibVLC scales it to 960x540). `PreviewCanvas` letterboxes non-16:9 originals back to their shape and shows an
  "Optimising n%" badge. When the proxy is done it switches over. Stills keep their stand-in picture.
- **Errors.** Status bar and bin show the first line only; the full ffmpeg log is in the bin item's tooltip.

### D27. Small UX rules from owner testing (2026-11-22)
- **New** asks for a name, a folder (default `Documents\Flyweight`) and the output size, and saves straight away, so
  Create always visibly does something.
- **Remove from project** (bin right-click, or Del while the bin has focus) removes the file with every clip, its
  annotations and every audio item using it, after a confirm that gives the count. It is one undo step, and the file
  on disk is untouched.
- `Flyweight.exe path.flyweight` opens that project.
- Debug builds write `%LocalAppData%\Flyweight\playback.log` (seeks, loads, volume sets, paint waits) to chase the
  audio crackle the owner reported. It is still open.

### D28. Clip speed: `Duration` means length on the timeline
`Clip.Speed` (0.25-4, default 1; stills are always 1). `In`/`Out` stay in SOURCE time, so annotations keep their
footage. `Clip.Duration` is now `SourceLength / Speed`, which keeps every timeline-layout caller correct. All
timeline/source conversion goes through `Clip.SourceAt`, `TimelineOffset` and `SourceSpan`; there is no hand-written
`In + (t - start)` any more. New annotations last 3 s on the TIMELINE at any speed. Export reads `SourceLength`, then
`setpts=(PTS-STARTPTS)/S` and an `atempo` chain (0.5-2 per stage, so 0.25 and 4 take two), which keeps pitch. Preview
uses `MediaPlayer.SetRate` (LibVLC keeps pitch), and `KeepVideoInStep` advances the video estimate by `extra x Speed`.
Trim drags convert the mouse's timeline delta with `SourceSpan`. Audio-track items stay at fixed timeline times (D23).

### D29. Export: graphics-chip encoder, YouTube bitrate cap, safe overwrite
- **Encoder.** Measured on 20 s of the owner's 1080p60 DJI footage (i5-10400T + UHD 630): x264 medium 48 s,
  veryfast 18.6 s, Intel Quick Sync 7.2 s. SSIM against the source was 0.967, 0.968 and 0.966. `ExportSettings.UseHardwareEncoder`
  (default on, no UI) tries `h264_qsv` first; `ExportPlan.FallbackArguments` (x264 veryfast) runs if it fails. It is
  the same H.264 High yuv420p file, so rule 9 holds. NVENC and AMF exist in the ffmpeg build but failed on this PC.
- **Bitrate.** Uncapped CRF 18 made a 5.5-minute brightened clip 1.27 GB (32 Mbit/s). The cap is YouTube's recommended
  SDR upload rate plus a third (`YouTubeBitrateKbps`): 16 Mbit/s for 1080p60. QSV's quality modes ignored `-maxrate`,
  so QSV uses a target bitrate plus `-maxrate`; x264 uses CRF 18 plus `-maxrate`/`-bufsize`.
- **Proxies.** Today's `ultrafast` proxies (D26) disabled deblocking and adaptive quantisation, so dark footage turned
  visibly blocky once brightened, and it saved almost no time (decode-bound: ~2.0 s per 10 s with any preset).
  Proxies are now `superfast` CRF 21, and the `-p2` cache key rebuilds the bad ones.
- **Existing file.** The export used to overwrite silently, and on cancel or failure it deleted the file at that path:
  the previous export. Now it asks (Keep both = "name (2).mp4" / Replace / Cancel), writes `name.exporting.mp4`, and
  moves it into place only on success.
- **Done.** "Your video is ready" shows size, time and encoder, and offers Open YouTube Studio (browser + Explorer
  with the file selected, ready to drag in) or Show in Explorer.

### D30. Editing comfort from owner testing (2026-11-22, evening)
- **Multi-select.** Ctrl+A selects all clips; Ctrl+click adds or removes one; Shift+click selects a range.
  `TimelineControl.SelectedClipId` stays the MAIN clip (what Properties shows), and `SelectedClipIds` is the set.
  Properties changes, M and Delete apply to every selected clip in one undo step. Split, Freeze and drag still act on one clip.
- **Typed values.** The slider read-out is a text box: type a number and press Enter (Esc cancels). It is clamped and
  counts as one undo step.
- **Recent projects.** Open ▾ lists the last 5, newest first, in `%LocalAppData%\Flyweight\recent.json`
  (`Core/Model/RecentProjects`). A project is added whenever it gets a file path. Save ▾ has Save as (Ctrl+Shift+S),
  which starts in the project's own folder and name.
- **Look.** `Controls/Theme` has the colours, rounded shapes, a dark `ToolRenderer` (also used for all menus via
  `ToolStripManager`) and `RoundButton`. Toolbar buttons only highlight while the pointer is really over them: after a
  click or a dialog WinForms leaves them "Selected" and doesn't repaint.

### D31. Chapter markers and the YouTube details file
Brought in from the unmerged `feature/youtube-chapters` branch (21 Nov; its separate "export frame" feature was left
out). **C** (M is now mute), toolbar "Chapter", or right-click the preview adds a named marker at the playhead. Markers
are flags on the ruler: click to jump, drag to move, double-click to rename, Del to delete. Like annotations they are
anchored to a clip in **source time** (`Marker.ClipId` + `SourceTime`), so they follow their footage through reorder,
trim, split and speed changes (`MarkerOps.TimelineTime` uses `Clip.TimelineOffset`), and go with a deleted clip.
`YouTubeChapters.Build` makes "0:00 Intro / 1:23 Setup" lines (h:mm:ss for an hour or more, floored to whole seconds),
adds a 0:00 "Intro" if missing, and warns (rather than silently fixing) when YouTube's rules aren't met: at least 3
chapters, each at least 10 s. Ctrl+Shift+C copies them; "Chapters…" shows the check, Copy and Save .txt.
**One file per export:** `Project.YouTube` (`YouTubeDetails`: Title, Description without chapters, Tags,
SaveTextFile) is edited in the export dialog, stored through the session (one undo step, only if changed), and
written as `<video> - YouTube.txt` (`YouTubeText`): TITLE / DESCRIPTION (with the marker chapters appended) / TAGS,
plus a "check before uploading" list (title 100, description 5000, tags 500 characters, no < >, chapter warnings).
Open YouTube Studio also opens this file.

### D32. Download = installer; tidy single-file folder; one window owns the session
- **Folder.** `packaging/package-preview.ps1` publishes single-file (`PublishSingleFile`, native .NET libraries
  included, compressed), so `Flyweight.exe` is the only file at the top. `libvlc\win-x64` stays as real files: a
  csproj target sets `ExcludeFromSingleFile`, because LibVLC loads its plugins from disk. `ffmpeg\` is beside it, and
  README / LICENSE / THIRD-PARTY-NOTICES are in `About\`. `VlcWindowsX86Enabled=false` drops the unused 32-bit LibVLC
  (~100 MB). The script fails if any other file lands at the top.
- **Installer.** `packaging/Flyweight.iss` (Inno Setup 6, installed per-user with winget) builds
  `Flyweight-<ver>-Setup.exe`. It is a per-user install (no admin, `%LOCALAPPDATA%\Programs\Flyweight`), with a Start
  menu entry, an optional desktop shortcut, an optional `.flyweight` association under HKCU, a ticked "Launch
  Flyweight" at the end (the owner's "offer to run"), and an Add/Remove Programs entry. Uninstall removes the proxy
  cache but never projects, videos or freeze frames. The zip is still built as an alternative.
- **Two windows.** The session lock holds the owning process id. A second Flyweight sees a live owner and neither
  offers to "recover" the first window's work, autosaves, nor deletes the lock and autosave when it closes. Previously
  it did all three. Opening a project via the command line or file association now takes the lock too.
