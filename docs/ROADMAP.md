# Roadmap & status

_Last updated: 2026-11-19 (end of day; branch `feature/audio`)_

## Where things stand

**v0.1 MVP: smoke-tested on Windows and merged to `main` (PR #3, `da4cf87`). The owner has exported a real video.**
Current branch: `feature/audio` (created from `main`, no feature commits yet). Next: audio, then clip speed (see below).

| Area | State |
|---|---|
| Core model, timeline ops, undo/redo, save/load | ✅ Done, unit-tested (62 core tests incl. real-ffmpeg integration tests) |
| Probe, proxy generation + cache, thumbnails | ✅ Done, integration-tested with real ffmpeg |
| Export (concat, trim, fit/pad 16:9, audio, timed overlays) | ✅ Done, integration-tested down to output pixels; real export verified by the owner |
| WinForms UI (preview, timeline, bin, style panel, export dialog) | ✅ Runs on Windows; smoke-tested |
| LibVLC playback engine | ✅ Runs on Windows; smoke-tested (polish still possible, see Known risks) |
| CI (GitHub Actions: Ubuntu tests + ffmpeg, Windows app build) | ✅ Done (`.github/workflows/ci.yml`) |
| Output size chosen at New project (`Project.OutputResolution`) | ✅ Done; export dialog defaults to it |

## Next steps (in order)

0. ~~Add GitHub Actions CI~~ ✅ (D14). CI sets `FLYWEIGHT_REQUIRE_FFMPEG=1` so integration tests fail rather than skip.
1. ~~Output size chosen at project creation~~ ✅ (D15). Old project files load as 1080p.
2. ~~First run on Windows~~ ✅ Smoke test complete (2026-11-19), checklist below.
3. **Playback polish.** Smooth playhead, clip-boundary transitions, scrubbing feel (`PlaybackEngine`).
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
- **Chapter markers** → copy YouTube description timestamps (`0:00 Intro…`)
- Thumbnail export (current frame + annotations at 1280×720)
- **NEXT after the smoke test (owner's order, 2026-11-19):**
  1. `feature/audio`: clip volume/mute (0–200%), one audio track for narration + music (import MP3/WAV/M4A,
     drag to position), per-item volume, optional ducking of clip audio under narration. Mic recording later.
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
Vertical / 9:16 output · multi-track video compositing · real-time GPU effects · AI features · transitions library
