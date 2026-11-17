# Flyweight

**A small but mighty video editor that punches above its weight.**

A simple, lightweight video editor for Windows, focused on long-form content (YouTube).
Cut your clips, put them in order, draw **arrows, boxes and text** on them, and export a
YouTube-ready MP4, without the lag and crashes of heavyweight editors.

> Status: MVP scaffold. Core editing/export logic is covered by tests; the WinForms UI compiles
> but needs a first run-through on Windows. See [docs/ROADMAP.md](docs/ROADMAP.md) for status and next steps,
> [docs/DECISIONS.md](docs/DECISIONS.md) for the reasoning behind the design, and [CLAUDE.md](CLAUDE.md) for AI-session context.

## Features (v0.1)

- Import MP4/MOV/MKV etc. by drag-drop or **Import…**, including phone footage (HEVC, VFR, portrait)
- Background **"optimise"** step: each clip gets an edit-friendly proxy, so scrubbing stays smooth
- Single-track timeline: reorder (drag), trim (drag edges), split (**S**), ripple delete (**Del**)
- **Annotations**: arrow, box, text. Draw on the preview, move/resize, style in the Properties panel,
  set timing by dragging the bars under the clip
- Unlimited undo/redo, autosave every 10s, crash recovery
- **Export for YouTube**: 1080p / 1440p / 4K, H.264 High + AAC, `+faststart`

## Getting started

1. Visual Studio 2022 (17.8+) with the **.NET desktop development** workload (.NET 8).
2. Download `ffmpeg.exe` and `ffprobe.exe` and put them in `src/Flyweight.App/ffmpeg/`
   (see the README there for which build to use). If omitted, ffmpeg on your `PATH` is used.
3. Open `Flyweight.sln`, set **Flyweight.App** as the startup project, press F5.

```
dotnet test     # core tests; the ffmpeg integration tests run if ffmpeg is on PATH
```

## Keyboard

| Key | Action | Key | Action |
|---|---|---|---|
| Space | Play / pause | S | Split at playhead |
| ← / → | Previous / next frame | Del | Delete selected clip or annotation |
| Shift+← / → | ∓1 second | V / Esc | Select tool |
| J / K / L | Back 1s / pause / play | A / R / T | Arrow / box / text tool |
| Home / End | Start / end | Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+wheel | Zoom timeline | Ctrl+0 | Zoom to fit |
| Ctrl+I / O / S / E | Import / open / save / export | Enter (in text) | Finish editing (Shift+Enter = new line) |

## How it works

```
Flyweight.Core   (net8.0, no UI; unit-tested)
  Model/       Project → Media, Timeline (clips), Annotations. Pure JSON-serialisable data
  Editing/     TimelineOps (split/trim/move/delete/retime), ProjectSession (snapshot undo, autosave)
  Media/       ffmpeg/ffprobe runner, probe, MediaLibrary (proxy + thumbnail cache)
  Export/      ExportPlan: whole project → one ffmpeg command

Flyweight.App    (WinForms, net8.0-windows)
  Playback/    LibVLC decodes proxies into memory (not a VideoView), so we can paint over video
  Rendering/   AnnotationRenderer: ONE GDI+ routine used by preview AND export (WYSIWYG)
  Controls/    PreviewCanvas, TimelineControl (owner-drawn), MediaBinControl
```

Key decisions:

- **Edit decision list, never touch originals.** Clips are just (source, in, out). Export renders from
  the originals; preview uses proxies.
- **Proxies** are 960×540, constant frame rate, every frame a keyframe. That fixes slow/inaccurate
  scrubbing, variable-frame-rate phone video and HEVC decode cost in one step. Cached in
  `%LocalAppData%\Flyweight\cache`.
- **16:9 canvas.** Non-16:9 sources (portrait phone clips) are fitted with black bars, identically in
  proxy and export, so annotation coordinates (normalised 0–1) mean the same spot everywhere.
- **Annotations are anchored to a clip in source time**, so they stay on the footage they point at
  when you trim or reorder.
- **Export** renders each annotation to a transparent PNG with the preview's renderer, then ffmpeg
  overlays them with `enable='between(t,start,end)'`.
- **ffmpeg runs out of process**: a bad file can't crash the editor.

## Roadmap

- Fades on annotations, blur/redact box, numbered callouts, freehand pen
- Chapter markers → YouTube description timestamps; thumbnail frame export
- Audio: music track, volume, fades
- Blurred-background fill for portrait clips; title cards; transitions

## Licence

MIT. Flyweight calls ffmpeg as a separate program and uses LibVLC (LGPL); check their licences
before redistributing binaries.
