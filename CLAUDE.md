# CLAUDE.md: context for AI-assisted sessions

Read this first. Then `docs/ROADMAP.md` (what's done / next) and `docs/DECISIONS.md` (why things are the way they are).

## What this is

**Flyweight**: a simple, lightweight Windows video editor for long-form YouTube content.
Owner: product-minded C#/.NET developer. Priorities: **stability and smoothness over features**.
The pitch is "a simple editor with great annotations (arrows, boxes, text) that doesn't lag or crash",
unlike Clipchamp / DaVinci Resolve.

Scope is deliberately narrow. **Out of scope unless the owner asks:** vertical/9:16 output, multi-track
compositing, real-time GPU effects, AI features, transitions.

## Layout

```
Flyweight.sln
src/Flyweight.Core/        net8.0, NO UI, NO System.Drawing. Unit-testable on any OS.
  Model/                   Project, SourceMedia, Clip, Annotation, ProjectSerializer (.flyweight JSON)
  Editing/                 TimelineOps (pure edit functions), ProjectSession (undo/redo, dirty, save)
  Media/                   FfmpegRunner/Locator, MediaProbe, MediaLibrary (proxies), Canvas, ExportSettings
  Export/                  ExportPlan: project → one ffmpeg command
src/Flyweight.App/         WinForms, net8.0-windows. Code-only UI (no .Designer.cs files).
  Playback/PlaybackEngine  LibVLC → memory buffer (video callbacks), timeline playback across clips
  Rendering/               AnnotationRenderer (shared by preview + export), OverlayRenderer (export PNGs)
  Controls/                PreviewCanvas, TimelineControl (owner-drawn), MediaBinControl
  Forms/                   ExportForm, AnnotationProperties (PropertyGrid view-models)
  MainForm.cs              layout, commands, keyboard shortcuts, autosave/crash recovery
  ffmpeg/                  drop ffmpeg.exe + ffprobe.exe here (git-ignored)
tests/Flyweight.Core.Tests/  xUnit; FfmpegIntegrationTests runs real ffmpeg (self-skips if absent)
```

## Build & test

- Windows: open `Flyweight.sln` in VS 2022 (.NET desktop workload), startup project **Flyweight.App**, F5.
- `dotnet test`: core tests. Integration tests need `ffmpeg`/`ffprobe` on PATH (or in the app's `ffmpeg/` folder).
- The App project can't be built on Linux with distro-packaged SDKs (they lack the WindowsDesktop SDK).
  Microsoft's official SDK + `EnableWindowsTargeting` (already set) can compile it. Run it only on Windows.

## Rules that keep the design sound (don't break these)

1. **Core stays UI-free.** No `System.Drawing`/WinForms in `Flyweight.Core`. Colours are hex strings.
2. **All timeline edits go through `TimelineOps`** and are applied via `ProjectSession.Execute` (one undo step)
   or `BeginEdit` → `Preview` → `CommitEdit` (mouse drags = one undo step). Never mutate the project elsewhere.
3. **Project model = pure serialisable data.** Runtime state (proxy paths, progress, thumbnails) lives in
   `MediaLibrary.MediaState`, NOT the model, because undo swaps the whole Project object.
4. **Annotation times are SOURCE time of their clip** (`ClipId`), visibility clamped to the clip's In/Out.
   Coordinates are normalised 0–1 on the 16:9 canvas. Sizes are "pixels at 1080p" and scaled.
5. **One renderer.** Preview and export both call `AnnotationRenderer.Draw`. Any visual change goes there,
   or the export will stop matching the preview.
6. **Preview uses proxies, export uses originals.** Proxies are 960×540, CFR, all-intra (`-g 1`), padded with
   `Canvas.FitFilter`, the same fit/pad as export.
7. **ffmpeg runs out of process** via `FfmpegRunner` with `ArgumentList` (no string quoting) and invariant-culture
   numbers (`FfmpegRunner.Sec/Num`). Never build ffmpeg args with string concatenation of paths.
8. **Never call LibVLC player methods from LibVLC callback threads** (deadlocks). Queue to the thread pool/UI.
9. Export output settings are fixed for YouTube (H.264 High, yuv420p, AAC 48k, +faststart). Don't add codec knobs to the UI.

## Conventions

- C# 12, nullable enabled, file-scoped namespaces, `record`/`sealed` where sensible.
- UI is built in code (no designer files) to keep diffs reviewable.
- Add/adjust tests in `tests/` for any change to Core behaviour; export changes should extend
  `FfmpegIntegrationTests` (it checks real output pixels).
- Commit messages: imperative summary line, then bullet details.
- **Backslash escapes have been mangled before.** Text written through a shell on Windows turned `\n` and `\f` into
  real line breaks/form feeds (a C# string that wouldn't compile, `bin\Debug\net8.0-windows` and `...\frames` paths
  in docs). Write files with the Edit/Write tools, not shell echo/heredocs, and grep for control characters
  (`[\x00-\x08\x0b\x0c\x0e-\x1f]`) before committing.

## Working style: show progress visually

The owner wants **screenshots in the chat as you go**:
- After any visible UI change, run the app and share a screenshot (before/after where useful). On Windows, launch
  the GUI outside the tool sandbox (see `docs/ROADMAP.md` dev notes) and capture the window.
- Share rendered outputs too: exported frames/PNGs, a frame grabbed from an exported video, icon/design drafts.
- In an environment that can't run the app (e.g. a Linux cloud session), say so plainly and share whatever visual
  output you can produce instead (ffmpeg-rendered frames, rendered SVGs), never a mock-up presented as the real app.

## Owner's dev setup

Windows; repos live in `C:\Users\sjbeale\source\repos\`. Typical start: `cd C:\Users\sjbeale\source\repos\; claude;`
