# Roadmap & status

_Last updated: 2026-11-17_

## Where things stand

**v0.1 MVP: scaffolded, not yet run on Windows.**

| Area | State |
|---|---|
| Core model, timeline ops, undo/redo, save/load | ✅ Done, unit-tested |
| Probe, proxy generation + cache, thumbnails | ✅ Done, integration-tested with real ffmpeg |
| Export (concat, trim, fit/pad 16:9, audio, timed overlays) | ✅ Done, integration-tested down to output pixels |
| WinForms UI (preview, timeline, bin, properties, export dialog) | 🟡 Written and compiles; **never run** |
| LibVLC playback engine | 🟡 Written and compiles; **never run**: highest risk |

## Next steps (in order)

1. **First run on Windows.** Add ffmpeg/ffprobe to `src/Flyweight.App/ffmpeg/`, F5, then work through the smoke test below and fix what breaks.
2. **Playback polish.** Smooth playhead, clip-boundary transitions, scrubbing feel (`PlaybackEngine`).
3. **Layout/DPI polish.** Splitter sizes, dark theme consistency (ToolStrip, PropertyGrid), high-DPI scaling.
4. **Success test:** 10 real phone clips (incl. 4K HEVC + a portrait clip), add 5 annotations, export a ~2 min
   1080p video in under 15 min total, with the export matching the preview exactly. No freezes, no crashes.

### Smoke-test checklist (first Windows run)
- [ ] App launches; LibVLC initialises (no missing native DLL errors)
- [ ] Import 2–3 clips (drag-drop and Import…); bin shows "Optimising n%" then thumbnails
- [ ] Preview shows frames; Space plays across clip boundaries with audio; ←/→ step frames
- [ ] Timeline: click/drag to scrub, drag to reorder, drag edges to trim, S splits, Del deletes
- [ ] Draw arrow / box / text; move and resize; edit text inline; change style in Properties
- [ ] Drag an annotation bar to retime; trim its clip and confirm the annotation stays on the footage
- [ ] Undo/redo everything above
- [ ] Save, close, reopen (proxies come from cache instantly)
- [ ] Kill the process mid-edit and relaunch: recovery prompt restores work
- [ ] Export 1080p: progress bar and cancel work; output plays, and annotations match the preview

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

## Explicitly not planned (for now)
Vertical / 9:16 output · multi-track video compositing · real-time GPU effects · AI features · transitions library
