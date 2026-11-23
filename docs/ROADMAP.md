# Roadmap & status

_Last updated: 2026-11-23 (feature/multitrack)_

## Where things stand

### 2026-11-23: `feature/overlay-zoom` (D35): overlay zoom (crop) + split overlays / audio items
PR #12 (overlay tracks, Separate audio, Insert at playhead, playback fixes) is merged to `main` (6bcfa79).
Overlay zoom: Properties → Zoom (crop) or scroll over it in the preview. S splits the selected overlay / audio item.
Owner verified zoom + split in-app. Then **crop for all videos** (D36): Crop sliders for main clips and overlays.
233 tests pass (incl. real-ffmpeg crop pixels for both). Crop not yet verified by the owner in-app.

### 2026-11-23: `feature/multitrack` (D34): overlay video tracks, Separate audio, Insert at playhead
**Built (owner asked for them):** picture-in-picture / cutaway video on overlay tracks V2–V5 (drag in time, between
tracks and in the preview; Place / Size / Across / Down / Volume / Mute in Properties; exported and in Save frame);
**Separate audio** (clip or overlay right-click: its sound goes on the audio track, the clip is muted); **Insert at
playhead** (bin / clip right-click, or I with a file selected in Media: splits the clip there). 220 tests pass incl.
a real-ffmpeg overlay export checked by pixels. Smoke-tested in the debug build with generated footage.
**Not yet verified by the owner in-app.** Known limits: overlays have no speed/zoom/tone; the main track sets the
length; audio items have no speed (so Separate audio needs 1×).
**Fixed after owner testing:** lag and overlays not pausing on play/pause with several videos (see D34). The audio-track
re-seek fix there may also be the old **audio crackle** (item 1 below): worth re-checking.
### 2026-11-22 session close: PRs #5-#10 merged to `main` (e839f98)
**Shipped today:** #5 clip zoom + brightness/contrast (D25), proxy fixes (D26), Remove from project, named New project
(D27); #6 clip speed (D28), Quick Sync export + YouTube bitrate cap + safe overwrite + "Your video is ready" step (D29),
multi-select / Ctrl+A, typed slider values, recent projects, Save as, dark rounded UI (D30); #7 chapter markers (M; C still works),
YouTube details + `<video> - YouTube.txt` (D31), Ctrl+C / Ctrl+V clips, packaging script; #8 per-user Inno Setup
installer, single-file folder, only the first window owns the session lock/autosave (D32); #9 stable download link;
#10 Save frame as image (D33).
**Releases:** `v0.2.0-preview` (pre-release, zip) and `v0.2.1-preview` (Latest: Setup.exe + zip + version-free
`Flyweight-Setup.exe` / `Flyweight-win-x64.zip`). Stable link:
`https://github.com/stephenbeale/flyweight/releases/latest/download/Flyweight-Setup.exe`.
**Save frame (PR #10) is NOT in a release yet.**
**Owner verified in-app:** brightness (preview + export), file-clash export prompt, clip speed, Ctrl+A select-all,
Save frame matched the preview, the installer ran.

**Still open:**
1. **Audio crackle during playback:** undiagnosed; proxy audio verified clean. Debug builds write
   `%LocalAppData%\Flyweight\playback.log` (about 40 KB of real playback captured). Next step: read it.
2. **Not yet verified by the owner in-app:** chapter markers (M), YouTube details / `.txt`, Ctrl+C / Ctrl+V, typed
   slider values, the lag-when-maximised fix, zoom.
3. **Release 0.2.2 to ship Save frame:** run `pwsh packaging\package-preview.ps1 -Version 0.2.2-preview`, publish as a
   normal (non-pre) release with BOTH versioned and version-free assets (`Flyweight-Setup.exe`,
   `Flyweight-win-x64.zip`) so the stable link updates.
4. **Ideas queued:** dark themed dialogs (Export, New project, Freeze, Chapters); delete the remote branches
   `feature/youtube-chapters` and `feature/frame-export` (now in `main`); ~~default video exports / freeze frames next to the
   footage~~ ✅ (2026-11-23); ~~long filter graphs hitting the Windows command-line limit~~ ✅ (2026-11-23).
**Testing tip:** a second instance shares `%LocalAppData%\Flyweight`; test installs into a scratch `/DIR` and avoid
launching while the owner's instance is open.

**v0.1 MVP: smoke-tested on Windows and merged to `main` (PR #3, `da4cf87`). The owner has exported a real video.**
`feature/audio` (clip volume/mute D22, audio track D23, real-time playback clock D24) is merged to `main` via PR.
**Next session: start a new branch and FIRST finish the audio-track tests below** (session ended 2026-11-20 mid-test),
then ducking/fades or clip speed.

### Audio track: owner testing status (2026-11-20, build 21:21)
Passed:
- Import MP3; waveform; drag; trim; stays put when video is trimmed.
- Music plays straight through cuts (after the D24 clock fix).
- Shift+M / Ctrl+M mute (M is now chapter marker: see the shortcut note in DECISIONS); volume per clip and per item; 150% boost; normal playback sound (after the D23 DirectSound fix).
- Waveform-only look with a hover tooltip.

Still to test (test files are in `C:\Users\sjbeale\Music\Flyweight test audio\`):
1. Overlap: import `test-beeps-mono.wav` inside the music. It should go on a second row, both should play, and the
   beeps should be as loud as the music (mono at full level). Last time the owner "couldn't open the file" from the
   hidden temp folder, so it was copied to Music; if it still fails, it's an import bug.
2. Right-click menu: Move to playhead, Jump to its start, Mute (ticked), Remove.
3. Del removes the item; the file stays in the bin.
4. Bin double-click on audio puts it on the audio track at the playhead.
5. Undo/redo of a drag, trim, volume, mute and delete (one step each).
6. Save and reopen keeps the items, trims, volumes and mute.
7. Export: music starts at the same moment and loudness as the preview; muted items are silent; music stops at
   the video's end.

| Area | State |
|---|---|
| Core model, timeline ops, undo/redo, save/load | ✅ Done, unit-tested (97 core tests incl. real-ffmpeg integration tests) |
| Probe, proxy generation + cache, thumbnails | ✅ Done, integration-tested with real ffmpeg |
| Export (concat, trim, fit/pad 16:9, audio, timed overlays) | ✅ Done, integration-tested down to output pixels; real export verified by the owner |
| WinForms UI (preview, timeline, bin, style panel, export dialog) | ✅ Runs on Windows; smoke-tested |
| LibVLC playback engine | ✅ Runs on Windows; smoke-tested (polish still possible, see Known risks) |
| CI (GitHub Actions: Ubuntu tests + ffmpeg, Windows app build) | ✅ Done (`.github/workflows/ci.yml`) |
| Output size chosen at New project (`Project.OutputResolution`) | ✅ Done; export dialog defaults to it |

## Open questions for next session

**Agreed order (unchanged):** first fix any bugs the owner hits during the real editing session, then
`feature/audio` (clip volume/mute, narration + music track, ducking), then clip speed. Reverse is parked.
Royalty-free media search is parked. Details under "v0.2 candidates" below.

### 1. Sharing with friends (undecided)
Not shareable yet:
- No release build.
- ffmpeg isn't bundled (the owner's PC has it via Chocolatey).
- The exe is unsigned, so friends hit SmartScreen and must choose "Run anyway".
- Only tested on the owner's PC.

Plan: a "v0.1 preview" zip on a GitHub Release:
- Self-contained win-x64 publish (.NET + LibVLC included).
- Bundled ffmpeg/ffprobe, with their licence notices (LGPL/GPL obligations).
- A short README.
- A feedback route (GitHub Issues).

Options offered for timing:
1. After the owner's own real-editing session and fixes (recommended).
2. First thing next session, before audio.
3. Later, as v0.2 after audio + speed.

### 2. App name (undecided, deferred to next time)
The owner thinks "Flyweight" is too common. It is also the name of a well-known software design pattern, which
hurts search. They want something that says lean / light / fast / nimble.
- **Rejected so far:** Kestrel and Dart (already taken), Cutman (gendered), Jab and Bantam (fun but not relevant to
  editing), Featherweight (likely crowded).
- **Current shortlist:** Whittle (shave long footage down; favourite), Nib (pen point: light + annotations),
  Featherframe (light + video frame).
- **Other ideas:** Pare, Kerf, Quill (likely taken: Quill.js), Callout, Markcut.
- **Next step:** check availability (existing apps, domains, trademarks, Microsoft Store) for the shortlist before
  choosing.
- Renaming is easiest before sharing with friends; it could ship with the preview zip.
- The icon is a boxing glove in a film frame (D16). It may need revisiting if the name drops the boxing theme.
- **Owner's logo idea for Whittle:** a flaming jet engine (Sir Frank Whittle invented the jet engine), so the name
  reads as both "whittle footage down" and "jet-fast". Possible twist: a film frame in the intake. Check the name
  isn't tied up by companies/trademarks using the surname before committing.

## Next steps (in order)

0. ~~Add GitHub Actions CI~~ ✅ (D14). CI sets `FLYWEIGHT_REQUIRE_FFMPEG=1` so integration tests fail rather than skip.
1. ~~Output size chosen at project creation~~ ✅ (D15). Old project files load as 1080p.
2. ~~First run on Windows~~ ✅ Smoke test complete (2026-11-19), checklist below.
3. **Playback polish.** Smooth playhead, clip-boundary transitions, scrubbing feel (`PlaybackEngine`).
   Since D24 the playhead runs on a real-time clock and the video catches up after each cut by skipping a few
   frames. Truly seamless cuts would need the next clip preloaded in a second player.
4. **Layout/DPI polish.** Splitter sizes, dark theme consistency (ToolStrip, PropertyGrid), high-DPI scaling.
5. **Success test:** 10 real phone clips (incl. 4K HEVC + a portrait clip), add 5 annotations, export a ~2 min
   1080p video in under 15 min total, with the export matching the preview exactly. No freezes, no crashes.

### Smoke-test checklist (first Windows run)
- [x] App launches; LibVLC initialises (no missing native DLL errors)
- [x] Import 2–3 clips (drag-drop and Import…); bin shows "Optimising n%" then thumbnails
- [x] Preview shows frames; Space plays across clip boundaries with audio; ←/→ step frames
- [x] Timeline: click/drag to scrub, drag to reorder, drag edges to trim, S splits, Del deletes
  (edge trim ripples: later clips close the gap. Owner is fine with that for now.)
- [x] Draw arrow / box / text; move and resize; edit text inline; change style in Properties
  (follow-ups done: opacity shown as % with slider, picking a fill colour ticks Filled, resize cursor on handles)
- [x] Drag an annotation bar to retime; trim its clip and confirm the annotation stays on the footage
  (now also: runs across cuts as one linked bar, D21)
- [x] Undo/redo everything above
- [x] Save, close, reopen (proxies come from cache instantly)
- [x] Kill the process mid-edit and relaunch: recovery prompt restores work
- [x] New project: output-size dialog appears
- [x] Export progress bar moves and Cancel stops cleanly (no half-written file)
- **Smoke test complete (2026-11-19).** Next: `feature/audio`, then clip speed (see v0.2 "NEXT").
- [x] Export 1080p: output plays, and annotations match the preview (owner, 2026-11-19, build 20:46)
- [ ] Chapters (D31): C adds a named marker flag; drag, double-click and Del work; Ctrl+Shift+C pastes "0:00 Intro…";
      export writes "<name> - YouTube.txt" (title, description + chapters, tags) beside the MP4

### First Windows run: historical note (2026-11-19, PR #3 `fix/first-windows-run`, merged as `da4cf87`)
- The first Windows run and full smoke test were done on 2026-11-19 and the fixes merged to `main`. CI was green
  (App build (Windows) + Core tests). The remote `fix/first-windows-run` branch has not been deleted.
- Biggest finds: `Flyweight.App` was missing from `Flyweight.sln`, so builds and CI never built the app and the exe
  was stale (fixed, D20). Images imported as 0-length videos (fixed via `SourceMedia.IsStill`, D17).
- Features/fixes from that day: custom style panel replacing the PropertyGrid (D18); thumbnails first in their own
  queue + preview stand-in while optimising (D19); old image entries auto-upgraded on load (D17); insert before/after
  from the timeline and bin right-click menus; linked annotations across cuts shown as one bar with fixed-length
  moves (D21); freeze frame (F key / toolbar) that can copy on-screen annotations (tickbox, default on); hover summary
  on annotation bars; prev/next clip buttons and Up/Down; timeline follows the playhead; no re-seek on annotation
  edits (lag fix); shortcuts suppressed only while typing; stills decoded at 1 fps on export; debug build time in the
  window title; timecode label auto-sizes.
- Freeze-frame PNGs live in `%LocalAppData%\Flyweight\frames`, not next to the project file (D17).
- **Process note:** the Bash tool on this machine collapses `\\` to `\` (so `\n`/`\f` become control characters), which
  corrupted a C# string and doc paths once. Write/edit files only with the Write/Edit tools, write commit messages to a
  file and use `git commit -F`, and check docs for stray control characters afterwards.
- **Dev notes:**
  - Start menu and desktop shortcuts "Flyweight (dev)" point at `src\Flyweight.App\bin\Debug\net8.0-windows\Flyweight.exe`,
    i.e. always the latest dev build. **Close the app before rebuilding Debug.**
  - To launch from a Claude session, start the GUI outside the tool sandbox (`Start-Process` on that exe).
  - Repo `nuget.config` pins nuget.org because the machine-wide config has a private feed (wf-lmx) that 401s.
  - Export speed options not applied yet (hwaccel auto for decode; x264 preset fast): only if the owner reports slow exports.
  - The owner will do real editing with the app next; note any bugs or friction and feed them back.

## Known risks / likely first bugs
- `PlaybackEngine`: VLC `Time` granularity, the pause-on-first-frame trick, `:start-time` on load, and Ended-state handling.
- GDI+ text metrics may differ slightly between preview size and 4K export (same code, different hinting).
- Long timelines with many annotations → very long `-filter_complex` (Windows command line limit ~32k chars).
  Fix: write the graph to a file (`-filter_complex_script`, or `-/filter_complex` on ffmpeg 7.1+).
- Thumbnails/bitmaps: watch for GDI handle leaks over long sessions.

## v0.2 candidates (owner to prioritise)
- Annotation fade in/out
- Blur/redact box; numbered callouts; freehand pen
- ~~**Chapter markers** → YouTube description timestamps~~ ✅ (D20): M to add, Ctrl+Shift+C to copy, .txt beside exports
- Thumbnail export (current frame + annotations at 1280×720)
- **NEXT after the smoke test (owner's order, 2026-11-19):**
  1. `feature/audio`:
     - ✅ Clip volume/mute (0–200%): Properties panel slider + tickbox, M key, clip right-click menu (D22).
     - ✅ Audio track (D23): import MP3/WAV/M4A/AAC/FLAC/OGG/Opus/WMA. Each item is placed at the playhead at a
       fixed timeline time; drag to move, drag ends to trim; per-item volume/mute. Overlapping items stack into
       rows. Mixed over the clip audio in preview and export. Final owner tests are still pending (see top).
       Possible later: a per-item "Lock to video" so narration follows its clip (the owner chose fixed time first).
     - Optional ducking of clip audio under narration; audio fades. Mic recording later.
  - Idea: "Export selection" (export only the selected clips) for quick checks.
  2. Clip speed (e.g. 0.25×–4×; setpts/atempo; timeline/annotation maths must respect speed).
  3. Reverse: parked (ffmpeg `reverse` buffers the whole clip; would be short clips only).
- Audio fades
- Blurred-background fill for portrait clips
- Title cards
- Captions (.srt) export
- **Royalty-free media search** (owner request, 2026-11-19, parked until after the smoke test): an in-app "Find
  media" panel searching Pexels/Pixabay (photos, video, Pixabay music) and Freesound (sound effects); preview,
  then Add downloads into the project + bin. Keep credit details per item (author, source, licence, URL) and a
  "Copy credits" button for the YouTube description. Needs free API keys (entered once in settings). Audio depends
  on the background-music track above.

## Later (v0.3+)
- Preview quality setting (540p / 720p proxies)
- Opt-in "save disk space" mode: keep an output-size copy instead of 4K originals (D15)
- Export from a high-quality mid-size copy if export speed becomes a pain point (D15)
- Zoom/crop (Ken Burns, zoom into a screen region) that uses 4K headroom
- Cross-project media library / proxy-cache manager → SQLite + Dapper (DECISIONS D13)

## Explicitly not planned (for now)
Vertical / 9:16 output · real-time GPU effects · AI features · transitions library
