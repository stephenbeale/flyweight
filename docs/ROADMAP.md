# Roadmap & status

_Last updated: 2026-11-19_

## Where things stand

**v0.1 MVP: first Windows run in progress (launch + video import pass; the rest of the smoke test is pending).**

| Area | State |
|---|---|
| Core model, timeline ops, undo/redo, save/load | ✅ Done, unit-tested |
| Probe, proxy generation + cache, thumbnails | ✅ Done, integration-tested with real ffmpeg |
| Export (concat, trim, fit/pad 16:9, audio, timed overlays) | ✅ Done, integration-tested down to output pixels |
| WinForms UI (preview, timeline, bin, properties, export dialog) | 🟡 Written and compiles; **never run** |
| LibVLC playback engine | 🟡 Written and compiles; **never run**: highest risk |
| CI (GitHub Actions: Ubuntu tests + ffmpeg, Windows app build) | ✅ Done (`.github/workflows/ci.yml`) |
| Output size chosen at New project (`Project.OutputResolution`) | ✅ Done; export dialog defaults to it |

## Next steps (in order)

0. ~~Add GitHub Actions CI~~ ✅ (D14). CI sets `FLYWEIGHT_REQUIRE_FFMPEG=1` so integration tests fail rather than skip.
1. ~~Output size chosen at project creation~~ ✅ (D15). Old project files load as 1080p.
2. **First run on Windows.** Add ffmpeg/ffprobe to `src/Flyweight.App/ffmpeg/`, F5, then work through the smoke test below and fix what breaks.
3. **Playback polish.** Smooth playhead, clip-boundary transitions, scrubbing feel (`PlaybackEngine`).
4. **Layout/DPI polish.** Splitter sizes, dark theme consistency (ToolStrip, PropertyGrid), high-DPI scaling.
5. **Success test:** 10 real phone clips (incl. 4K HEVC + a portrait clip), add 5 annotations, export a ~2 min
   1080p video in under 15 min total, with the export matching the preview exactly. No freezes, no crashes.

### Smoke-test checklist (first Windows run)
- [x] App launches; LibVLC initialises (no missing native DLL errors)
- [x] Import 2–3 clips (drag-drop and Import…); bin shows "Optimising n%" then thumbnails
- [ ] Preview shows frames; Space plays across clip boundaries with audio; ←/→ step frames
- [ ] Timeline: click/drag to scrub, drag to reorder, drag edges to trim, S splits, Del deletes
- [ ] Draw arrow / box / text; move and resize; edit text inline; change style in Properties
- [ ] Drag an annotation bar to retime; trim its clip and confirm the annotation stays on the footage
- [ ] Undo/redo everything above
- [ ] Save, close, reopen (proxies come from cache instantly)
- [ ] Kill the process mid-edit and relaunch: recovery prompt restores work
- [ ] Export 1080p: progress bar and cancel work; output plays, and annotations match the preview
- [ ] Export frame (Ctrl+Shift+E / right-click preview): PNG lands next to the largest video, includes annotations,
      appears highlighted in Media; dragging it to the timeline works (D18)
- [ ] Export dialog and freeze frames default to the largest video's folder (D18)
- [ ] Chapters (D20): M adds a named marker flag; drag/double-click/Del work; Ctrl+Shift+C pastes "0:00 Intro…" into
      Notepad; "YouTube chapters…" shows the rule check; video export writes "<name> chapters.txt" beside the MP4

### First Windows run: status (2026-11-19, branch `fix/first-windows-run`)
- Launch and video import passed. **Not yet tested:** playback (step 2, highest risk), timeline edits, annotations,
  undo/redo, save/reopen, crash recovery, export, the New-project size dialog, and the new stills / freeze frame /
  toolbar fix in the real app.
- **Bug found + fixed:** images imported as 0-0.04s videos, giving zero-length clips. `trim=duration=0` means
  "unlimited" in ffmpeg, so export never finished ("15 min for a 28s clip") and some images had no thumbnail.
  Fix: stills are detected at probe time (`SourceMedia.IsStill`) and handled properly (D17).
- **Toolbar text** was pale grey on the light strip (inherited form ForeColor); now explicit black. Not yet visually verified.
- **Freeze frame added** (F key / toolbar "Freeze"): grabs the frame under the playhead and inserts it as a 5s still.
- **Open items:**
  1. The owner's open project has images imported the OLD way: delete and re-import them (export now errors clearly on them).
  2. Finish the smoke test above, starting with playback.
  3. ~~Freeze-frame PNGs live in `%LocalAppData%\Flyweight\frames`~~: now saved next to the largest video (D18).
- Dev notes: launch the GUI outside the tool sandbox (`Start-Process bin\Debug\net8.0-windows\Flyweight.exe`); close the app
  before rebuilding Debug. Repo `nuget.config` pins nuget.org because the machine-wide config has a private feed (wf-lmx) that 401s.

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
- Audio: background music track, clip volume, fades
- Blurred-background fill for portrait clips
- Title cards
- Captions (.srt) export

## Later (v0.3+)
- Preview quality setting (540p / 720p proxies)
- Opt-in "save disk space" mode: keep an output-size copy instead of 4K originals (D15)
- Export from a high-quality mid-size copy if export speed becomes a pain point (D15)
- Zoom/crop (Ken Burns, zoom into a screen region) that uses 4K headroom
- Cross-project media library / proxy-cache manager → SQLite + Dapper (DECISIONS D13)

## Explicitly not planned (for now)
Vertical / 9:16 output · multi-track video compositing · real-time GPU effects · AI features · transitions library
