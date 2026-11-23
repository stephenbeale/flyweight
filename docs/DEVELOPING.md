# Developing Flyweight

## Build and run

1. Visual Studio 2022 (17.8+) with the **.NET desktop development** workload (.NET 8).
2. Put `ffmpeg.exe` and `ffprobe.exe` in `src/Flyweight.App/ffmpeg/` (see the README there). If they're missing,
   ffmpeg on your `PATH` is used.
3. Open `Flyweight.sln`, set **Flyweight.App** as the startup project, press F5.

```
dotnet test     # core tests; the ffmpeg integration tests need ffmpeg on PATH
```

The repo's `nuget.config` pins nuget.org. Releases are built with `packaging/package-preview.ps1` (see
[ROADMAP.md](ROADMAP.md) for the release steps).

See also: [ROADMAP.md](ROADMAP.md) (status, next steps), [DECISIONS.md](DECISIONS.md) (why things are the way they
are) and [CLAUDE.md](../CLAUDE.md) (context for AI-assisted sessions).

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
