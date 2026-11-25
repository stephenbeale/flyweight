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
out). **M** (C still works; mute is Shift+M / Ctrl+M, see the shortcut note below), toolbar "Chapter", or right-click the preview adds a named marker at the playhead. Markers
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
- **Stable download link.** Each release also carries `Flyweight-Setup.exe` and `Flyweight-win-x64.zip` (version-free
  copies made by the packaging script), and is published as a normal release, not a pre-release, because GitHub's
  `/releases/latest/` skips pre-releases. So this link always gets the newest installer:
  https://github.com/stephenbeale/flyweight/releases/latest/download/Flyweight-Setup.exe. "Preview" stays in the
  title, version and README.

### D33. Save frame as image
Rebuilt on today's code from the old `feature/frame-export` branch (1d97f85), which predated zoom, brightness and
speed. **Save frame** (toolbar, Ctrl+Shift+E, right-click the preview; "Save this frame as…" picks the place) writes a
PNG at the project's output size. `FrameExport.BuildArgs` reads the exact frame from the ORIGINAL file and applies
the clip's zoom (`ClipLook.ZoomFilter`) and tone (`ClipLook.ToneFilter`), the same filters as the video export.
`FrameRenderer` then draws the annotations visible at that moment with `AnnotationRenderer`, so the image matches the
preview and the export. It is saved next to the largest video in the project, as "<project> frame 1m23s.png", never
overwriting, and added to the media bin (not the timeline) and highlighted. The old branch also moved video exports
and freeze frames next to the footage by default; that part was NOT brought in.

### Shortcut note (2026-11-23): M = chapter marker, Shift+M / Ctrl+M = mute
Owner decision. M for markers is the convention in Premiere, Resolve and Final Cut, so YouTubers expect it; mute
moved to Shift+M (Ctrl+M unchanged). C stays as an alias for adding a marker.

### Support link (2026-11-23): "Buy Steve a coffee", never obtrusive
Owner request: a donation option via https://buymeacoffee.com/stephenbeale that doesn't get in the way. It appears
in exactly one place in the app: a one-line footnote link in the "Your video is ready" dialog after a successful
export (`ExportForm.BuyMeACoffeeUrl`), plus a Support section in the README. No pop-ups, no nag timers, nothing during
editing, no feature gating. Don't add it anywhere else without the owner asking.
2026-11-24, owner: "make the enjoying flyweight an actual button", then "suggest a £20 donation" with "a yellow
background". The "Your video is ready" dialog is now our own dark `ExportDoneForm` (a TaskDialog can't colour buttons):
Open YouTube Studio (default), Show in Explorer, Close, then below a divider "Enjoying Flyweight?" and a yellow
"☕ Buy Steve a coffee" button. It opens the page and leaves the dialog open. Same place only. (Briefly labelled
"· £20"; the owner then asked for the number to come off, so the button names no amount.)

### Icon update (2026-11-23): black fly with red boxing gloves, in the film frame
Supersedes D16's gold glove. The owner chose a black fly silhouette, vertical, on a white picture area inside the
same film frame (sprocket holes both sides), wearing red boxing gloves. Fly = the name, gloves = "punches above its
weight", film frame = video. The red gloves are what keep it recognisable at 16 px. Still no red + white play button.
Kept for later in `assets/concepts/`: `film-fly.svg` (same, no gloves; the owner likes it too),
`glove-in-film-frame.svg` (the previous icon) and earlier drafts, with comparison images.
Source `assets/icon.svg` → `assets/icon-128.png`/`icon-512.png` and `src/Flyweight.App/flyweight.ico` (16–256 px),
which the app, window and installer all use. Re-render all of them if the SVG changes.

### Exports and freeze frames default next to the footage; long filter graphs go through a file (2026-11-23)
**Default folders.** The video export dialog now suggests the folder of the project's largest video (the main
recording), named after the project file, else "<main video> - edit.mp4" (`FrameExport.DefaultVideoPath`), the same
rule Save frame uses (D33). Freeze frames are saved there too ("Match freeze 0m12s.png", never overwriting) instead of
`%LocalAppData%\Flyweight\frames`, so they travel with the footage. This closes the D17 portability limitation.
**Long projects.** Windows limits a command line to ~32k characters, and a project with many clips/annotations can
produce a longer ffmpeg filter graph. `FfmpegRunner` now writes any graph over 4,000 characters to a temp file and
passes it as `-/filter_complex FILE` (ffmpeg 7+, incl. gyan.dev nightlies) or `-filter_complex_script FILE` (older),
chosen by parsing `ffmpeg -version` once. If the whole command is still too long it fails with a clear message
instead of a cryptic one.


### D34. Overlay video tracks, Separate audio, Insert at playhead (2026-11-23)
Owner request ("I NEED to add concurrent video tracks and separate audio from video"). This lifts the old
"multi-track video compositing" out-of-scope line, kept deliberately small:
- **Model.** `Project.Overlays` holds `OverlayClip`s: `Track` (1 = V2, higher draws on top, max 4), `Start` in
  TIMELINE time + source `In`/`Out` (like `AudioItem`, D23, so rippling the main track never moves them), and a
  placement: `Scale` (fraction of canvas width) and centre `X`/`Y` (0–1), keeping the source's own shape. Own
  `Volume`/`Muted`. Old files load with none. The MAIN track still sets the video's length; an overlay past the end
  is cut off. No speed, zoom or tone on overlays (yet).
- **One layout.** `OverlayLayout` (Core) is the only place that turns an overlay into a rectangle; the preview, the
  export and Save frame all use it, so they agree (the overlay version of rule 5). Quick placements: corners, centre,
  Full frame (cutaway). Pixel sizes are rounded to even numbers for yuv420p.
- **Export.** Each overlay is its own input (`-ss In -t len`), scaled to its box, `tpad`/`trim`'d to length, then
  `setpts=PTS+start/TB` and `overlay=x:y:eof_action=pass:enable='between(t,s,e)'` on top of the concatenated main
  track, bottom track first, and BEFORE the annotation PNGs (annotations stay on top). Its sound joins the `amix`.
  Save frame (`FrameExport.BuildArgs`) overlays the frames showing at that moment the same way.
- **Preview.** `OverlayLanePlayer`: one extra LibVLC player per overlay on screen (max 4), each decoding its 960×540
  proxy into its own buffer (same video-callback pattern as `PlaybackEngine`, rule 8). PreviewCanvas crops the proxy's
  letterbox (`Fit` by the media's aspect) and draws it into the overlay's box, under the annotations. Playing: drift
  >250 ms is re-seeked. Paused: each player is seeked to the playhead's exact frame (throttled 40 ms). No proxy yet:
  it plays the original (stretched, so drawn whole) or shows an "Optimising…" box.
- **UI.** Overlay rows sit ABOVE the main track (V2 just above, V3 above that), so the main track moves down by one
  row per track. Drag to move in time; drag up/down to change track (above the top row = a new track one higher than
  the highest *other* overlay, so it can't run away while rows are added); drag ends to trim. In the preview, drag an
  overlay to place it (annotations win the click: they're on top). Properties: Place, Size, Across, Down, Volume, Mute.
  Media bin: "Add as overlay at playhead" (O) uses the lowest free track.
- **Separate audio.** `TimelineOps.SeparateAudio` adds an `AudioItem` for the clip's own file/In/Out/volume at its
  timeline start and mutes the clip. Audio items may now come from VIDEO files (the export just reads their audio
  stream; the lane player already used `:no-video`); the timeline shows the file name instead of a waveform for those.
  Refused for stills, silent clips and clips not at 1× (audio items have no speed). Overlays can do the same.
- **Insert at playhead.** `TimelineOps.InsertMediaAt` splits the clip under the playhead (same rule as freeze frames:
  no split within 100 ms of a cut, shared via `OpenGapAt`) and inserts the whole file there; past the end it appends.
  Bin menu, clip menu, and I with a file selected in Media.
- **Smooth play/pause with several videos (2026-11-23, owner: "laggy on play pause with multiple videos", "play pause
  doesn't work for all videos").** (1) Decoder threads used to post a repaint per decoded frame; with overlays that
  flooded the message queue and Windows only delivers WM_TIMER when the queue is empty, so the playhead clock stalled
  (1.8 s of progress in 4 s of play). Now decoders only set a flag and the 15 ms timer repaints once per tick
  (`PlaybackEngine.FrameReady`, raised on the UI thread). (2) Overlays were paused only on the tick where play
  switched to pause, so one still opening then kept running; play/pause is now enforced every tick (rapid-toggle test:
  15,417 → 0 changed pixels while paused). (3) Drift checks on LibVLC's coarse `Time` re-seeked overlays and audio
  items about every second, and each seek lost time; `PlayerClock` estimates the real position like the main engine
  does (re-seeks went from ~10 per 4 s to none). (4) Resuming no longer counts paused time as video progress (it
  re-seeked the main video on every play). Overlays at ≤ 50% size decode at 480×270; drawing copies the frame out
  under the lock first so the decoder never waits for GDI+.

### D35. Overlay zoom (crop) and splitting overlays / audio items (2026-11-23)
Owner: "add zoom possibility for overlaid videos, like cropping" and "split either video on the timeline".
- **Zoom = crop.** `OverlayClip.Zoom` (1–4) + `PanX`/`PanY`, like a main clip's zoom (D25): the overlay's box keeps its
  size and place, and less of the source fills it. `OverlayLayout.CropRect` is the one formula (source-relative,
  same shape as the source); the export and Save frame crop the ORIGINAL with it before scaling (even-pixel `crop`
  expressions, so yuv420p sources never get odd sizes), the preview crops the decoded proxy picture. Old files load
  unzoomed. Properties: Zoom (crop) section; in the preview, scroll over an overlay to zoom (a burst of wheel notches
  is one undo step). A zoomed small overlay decodes at full size so the magnified part stays sharp.
- **Split.** S / Split cuts the SELECTED overlay (`SplitOverlayAt`) or audio item (`SplitAudioAt`) at the playhead,
  else the main clip as before. The right half is a copy (placement, zoom, volume, track, stacking order) and becomes
  the selection, so S again keeps cutting the same track. Clicking an overlay's or audio item's body now also moves
  the playhead there (as clips always did), so you can click where to cut and press S. Free-shape cropping (e.g. only
  trimming the sides of a webcam) isn't there yet: the crop keeps the source's shape.

### D36. Crop for every video: main clips and overlays (2026-11-23)
Owner: "I want to be able to crop all videos though, not just zoom or pan". `CropLeft/Top/Right/Bottom` (fraction of
the source, 0–0.45 each so at least 10% remains) on both `Clip` and `OverlayClip`; old files load uncropped. Unlike
zoom, crop changes the picture's SHAPE.
- **Nothing slides (owner: "it feels like it is going the wrong way, can't it crop without sliding the image"):** a crop
  only hides edges; the picture stays exactly where it was. (The first version re-fitted what was left, so it moved/grew.)
- **Main clips:** the cut edges are blacked out IN PLACE on the original (`ClipLook.MaskFilter`: `drawbox` fills), then
  the usual fit, zoom and tone. Zoom afterwards if you want it to fill the frame. Preview: `PreviewCanvas.Masked`. Split, freeze
  frame and Reset picture carry / clear the crop (`CopyLook`).
- **Overlays:** only the cropped EDGE of the box moves (`SetOverlayCrop` shifts the centre by half the change), so the
  picture stays put on screen and trimming a webcam's sides gives a narrower box; `Scale` is the width the uncropped picture would have (now up to 400%, so a cropped
  picture can fill the frame; Full frame computes that). Zoom then works inside the cropped area: `CropRect` composes
  crop then zoom, and the one crop filter serves both.
- **UI:** Crop section (Left / Right / Top / Bottom sliders) in both Properties panels. Dragging crop handles in the
  preview isn't there yet.

### D37. Drag an overlay's edges to crop, corners to resize; collapsible Properties cards (2026-11-24)
Owner: "the crop on overlaid videos is still weird" / "sliding crop is almost the inverse of what I am doing", and
"improve UI on sidebar and media bin... rounded edges, boxes for various properties which are collapsible by group".
- **Why crop felt inverse:** zoom works INSIDE the crop (D35/D36), so on a zoomed overlay every crop change also
  re-mapped the zoom and the picture slid/grew. And the Right/Bottom sliders move their edge the opposite way to the
  slider. **Fix:** `TimelineOps.TrimOverlayEdge(side, edge)` puts one edge at a canvas position and solves crop + pan
  together (zoom and size fixed) so what's on screen doesn't move. Past a crop limit it stops at the furthest point where
  only that edge moves (bisection) rather than moving the other edge. The sliders still set the crop of the source.
- **Preview grips** on the selected overlay: white bars at the edge midpoints crop (via `TrimOverlayEdge`), corner
  squares resize with the shape kept and the opposite corner fixed, the middle still moves it. Drawn in
  `OverlayLanePlayer.DrawGrips`, hit-tested in `PreviewCanvas.OverlayGripAt`.
- **Properties:** `PropertySheet` + `PropertyGroup` (rounded card, click the header to collapse; collapsed groups are
  remembered in `%LocalAppData%\Flyweight\ui-state.json`). The label column is measured from the longest label at the
  current DPI (fixes "Brightnes" at 150%). Hand-drawn slider (thin track, accent fill, round thumb; the wheel only
  changes it when focused, so scrolling the panel is safe), rounded value boxes, toggle switches for tick boxes,
  rounded colour swatches, dark drop-downs (`Theme.DarkCombo` paints the closed box; Windows' theme ignores BackColor).
- **Media bin:** owner-drawn ListView cards (rounded thumbnail, name, details, VIDEO/AUDIO/IMAGE badge, thin progress
  bar while optimising, hover/selected states), a header with a file count, an empty-state message, and files can be
  dropped onto the bin itself. Side panels are sized for display scaling (capped at a quarter of the window each).

### Icon update (2026-11-24): the fly without gloves, on yellow
Supersedes the 2026-11-23 icon. Owner: "change the logo to have no boxing gloves and put a yellow background on it".
`assets/icon.svg` is now `film-fly.svg` (no gloves) with the picture area behind the fly in yellow `#ffd21f` (the wing
veins match it); the film frame stays dark. The black fly on yellow is what keeps it readable at 16 px.
Kept in `assets/concepts/`: `film-fly-gloves.svg` (the previous icon), `film-fly-yellow-tile.svg` (the alternative
with the outer tile yellow instead), and `yellow-comparison.png`. Rendered with headless Edge to a 512 px PNG, then
scaled to `assets/icon-128.png`/`icon-512.png` and a PNG-entry `flyweight.ico` (16–256 px).

### D38. Deleting a clip keeps the other tracks in sync (2026-11-24)
Owner: "when I delete, it should keep any other tracks in sync by default". Overlays and audio-track items sit at fixed
timeline times (D23/D34), so a ripple delete on the main track used to leave everything after the cut out of step.
`TimelineOps.Delete(p, id, keepTracksInSync = true)` now also calls `CutRangeFromTracks(from, to)`: items after the
deleted clip slide left by its length; the part of an item inside the deleted stretch is cut out (an item spanning it
is split in two, each half staying with its footage); slivers under `MinClipLength` are dropped. Applies to Delete,
multi-select delete and Remove from project. `keepTracksInSync: false` keeps the old behaviour for callers that need it
(no UI switch yet).
- **Trims too (same day, owner):** dragging a clip's edge keeps the tracks in sync. `TimelineControl` takes a
  `TimelineOps.CaptureTracks` snapshot when the drag starts and `TrimIn`/`TrimOut(..., syncFrom)` rebuild the tracks
  from it on every mouse move, then cut (`CutRangeFromTracks`) or open (`InsertGapInTracks`) the difference at the
  trimmed edge. From a snapshot, so dragging back and forth never loses an item. Speed changes don't move the tracks.
- **Typed speed:** `SpeedBox` replaces the preset drop-down: type any value 0.25–4 ("1.08", "1.08x", "108%"),
  ↑/↓ step through the presets, ▾ lists them.
- **Follow-ups (same day):** speed presets now step by 0.05 near 1× (0.85–1.3) and 0.1 a little further out, since
  "1.25 is quite fast"; slider value boxes are narrower (42 units, "100%" with no space) so the tracks keep their length.

### YouTube thumbnail preset (2026-11-24)
Right-click the preview → "Save as YouTube thumbnail": the frame under the playhead (zoom, brightness, annotations,
same renderer as Save frame) at exactly 1280×720, saved as a JPEG next to the footage ("Trip thumbnail 1m23s.jpg")
and added to the Media bin. JPEG, not PNG: a busy 1280×720 PNG can exceed YouTube's 2 MB thumbnail limit (random
pixels: 2.8 MB) while the best-quality JPEG of the same frame is ~1.7 MB. `YouTubeThumbnail.SaveJpegUnderLimitAsync`
still steps the quality down if a frame ever doesn't fit, so the file is always accepted.


### D39. Overlay speed; Shift+S splits every track (2026-11-24)
Owner: "there is no speed option on overlaid videos" and "make split all tracks possible on Shift+S".
- **Overlay speed** works like a main clip's (D28): `OverlayClip.Speed` (0.25–4, old files load as 1, stills stay 1),
  In/Out stay in source time and `Duration` = source length ÷ speed. Every timeline→footage conversion for overlays goes
  through `OverlayClip.SourceAt` / `SourceSpan`: split, trims (front trim keeps the END fixed), the delete/trim sync
  (D38), Save frame and the preview (VLC plays at the rate; `PlayerClock.Rate` so the drift check expects it). Export:
  `setpts=(PTS-STARTPTS)/speed` and `atempo` (pitch kept), reading all of the source span. Split copies now use
  `Clone()` so new fields (like Speed) can't be dropped. Separate audio refuses a sped-up overlay (audio items are 1×).
  Speed box in the overlay's Overlay group; the timeline label shows "2× · V2 · …".
- **Shift+S** (`TimelineOps.SplitAllAt`): cuts the main clip, every overlay and every audio item under the playhead in
  one undo step; items within `MinClipLength` of their ends stay whole. Also in the clip right-click menu.

### D40. Blurred bars behind portrait clips (2026-11-25)
Owner asked (2026-11-24) for the black bars around portrait phone clips to be filled with a blurred copy, like TV
news / YouTube. `Project.Background` (`BackgroundFill.Black` / `Blur`) is project-wide: `ProjectSession.New` starts new
projects on Blur (New project dialog tickbox, on by default); files without the field load as Black, so old projects
don't change. Toggle: clip right-click menu, "Blurred bars behind portrait clips (whole project)" (one undo step).
- **Applies when** `ClipLook.BlursBars`: the clip isn't zoomed or cropped (those keep their own framing, and a crop
  blacks out edges in place) and its picture isn't already 16:9 (within 1%). 4:3 and portrait photos count too.
  Overlays are unaffected.
- **Cheap blur, the same recipe both sides:** scale the picture to FILL a tiny 64×36 frame (centre crop), blur it
  there, dim to 60%, scale back up, then fit the sharp picture on top exactly where the black-bar fit puts it (so
  annotations and overlays don't move). Export/Save frame/thumbnail: `ClipLook.BlurFitFilter` (split → area scale +
  crop → `gblur=sigma=2` → `colorchannelmixer` → bicubic upscale, overlaid by the normal fit), labels prefixed per
  clip (`bf0`, `bf1`…). Preview: `PreviewCanvas.Blurred` draws the proxy's picture region into 64×36, runs
  `ClipLook.BlurSmall` (two 5-wide box passes each way ≈ sigma 2, plus the dim) and upscales. Blurring ~2,300 pixels
  instead of 8 million keeps 4K exports fast and playback smooth. Preview and export aren't bit-identical (GDI+ vs
  ffmpeg scaling) but look the same; the sharp picture itself is untouched.
- Proxies and thumbnails keep black bars (`Canvas.FitFilter`): the preview adds the blur at draw time, so toggling the
  setting never re-encodes anything.

### D41. Vertical 9:16 projects for YouTube Shorts (2026-11-25)
Owner: "can it also have vertical video for yt as a canvas/output option?" (previously out of scope; approved).
- **`Project.Shape`** (`CanvasShape.Landscape` / `Vertical`), old files load as Landscape. Chosen at New project; can be
  changed later via the preview right-click menu "Frame shape" (owner chose this over fixed-at-creation). Positions are
  normalised, so a change keeps everything at the same fraction of the frame: arrows/boxes/overlays may need moving, so
  the app warns first when there are any. One undo step (`TimelineOps.SetCanvasShape`).
- **One aspect source:** `Canvas.Aspect(shape)`; `ExportSettings.SizeFor(shape)` turns 1920×1080 → 1080×1920 etc.
  `OverlayLayout.Rect/Place` take the canvas aspect (PixelRect derives it from W×H; the preview from its canvas rect).
  `ClipLook.BlursBars` compares the clip to the canvas shape, and the blur works in 36×64 when vertical.
  Annotation sizes are "pixels at 1080p" of the SHORT side (landscape unchanged). Bitrate cap by the short side.
- **Proxies stay 16:9** and shared between projects: the vertical preview cuts the picture out of the 960×540 proxy frame
  (`PreviewCanvas.Upright`) and fits it into 540×960 before blur/crop/tone/zoom run. Nothing is re-encoded when switching.
  A portrait clip's preview is therefore 304×540 of real pixels: fine for editing, and export uses the originals anyway.
- **YouTube thumbnail preset is hidden** in vertical projects (Shorts don't use 16:9 custom thumbnails); Save frame
  saves at the vertical size. Export shows a hint when a vertical video is over 3:00 (YouTube's Shorts limit).
