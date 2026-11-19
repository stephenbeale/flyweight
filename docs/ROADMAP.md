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
- [x] Preview shows frames; Space plays across clip boundaries with audio; ←/→ step frames
- [x] Timeline: click/drag to scrub, drag to reorder, drag edges to trim, S splits, Del deletes
  (edge trim ripples: later clips close the gap. Owner is fine with that for now.)
- [x] Draw arrow / box / text; move and resize; edit text inline; change style in Properties
  (follow-ups done: opacity shown as % with slider, picking a fill colour ticks Filled, resize cursor on handles)
- [ ] Drag an annotation bar to retime; trim its clip and confirm the annotation stays on the footage
- [ ] Undo/redo everything above
- [ ] Save, close, reopen (proxies come from cache instantly)
- [ ] Kill the process mid-edit and relaunch: recovery prompt restores work
- [ ] Export 1080p: progress bar and cancel work; output plays, and annotations match the preview

### First Windows run: status (2026-11-19, branch `fix/first-windows-run`)
- Passed so far: launch, import, playback, timeline edits, drawing/styling annotations, freeze frame, toolbar text,
  insert before/after (timeline right-click), stills in old projects. **Not yet tested:** annotation retiming,
  full undo/redo pass, save/reopen, crash recovery, export, the New-project size dialog, bin right-click insert,
  the timecode "/ total" fix.
- **Session 2 (evening) fixes/features:** App project added to the solution (D20); custom style panel (D18);
  thumbnails first in their own queue + preview stand-in while optimising (D19); old image entries auto-upgraded
  on load (D17); resize cursor on handles; insert before/after from the timeline and bin right-click menus;
  timecode label auto-sizes so "/ total" isn't clipped at high DPI.
- **Process note:** the Bash tool collapses `\\` to `\`, which corrupted a C# string and doc paths last session.
  Write files with the Write/Edit tools.
- **Bug found + fixed:** images imported as 0-0.04s videos, giving zero-length clips. `trim=duration=0` means
  "unlimited" in ffmpeg, so export never finished ("15 min for a 28s clip") and some images had no thumbnail.
  Fix: stills are detected at probe time (`SourceMedia.IsStill`) and handled properly (D17).
- **Toolbar text** was pale grey on the light strip (inherited form ForeColor); now explicit black. Not yet visually verified.
- **Freeze frame added** (F key / toolbar "Freeze"): grabs the frame under the playhead and inserts it as a 5s still.
- **Open items:**
  1. ~~Old-way images in the owner's project~~: now upgraded automatically on load (D17).
  2. Finish the smoke test above, from annotation retiming onwards.
  3. Freeze-frame PNGs live in `%LocalAppData%\Flyweight\frames`, not next to the project file (D17).
- Dev notes: launch the GUI outside the tool sandbox (`Start-Process src\Flyweight.App\bin\Debug\net8.0-windows\Flyweight.exe`);
  close the app before rebuilding Debug. Repo `nuget.config` pins nuget.org because the machine-wide config has a private feed (wf-lmx) that 401s.

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
