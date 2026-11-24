<img src="assets/icon-128.png" width="96" align="right" alt="Flyweight icon: a black fly wearing red boxing gloves, inside a film frame">

# Flyweight

**The video editor that punches above its weight.**

A simple, lightweight video editor for Windows, made for YouTube videos. Cut your clips, draw arrows, boxes and text
on them, add music, and export a YouTube-ready MP4, without the lag and crashes of heavyweight editors.

## Download

**[Download Flyweight for Windows](https://github.com/stephenbeale/flyweight/releases/latest/download/Flyweight-Setup.exe)**
(Windows 10 or 11, 64-bit). Everything it needs is included.

**No admin rights or can't install?** Get the
**[portable zip](https://github.com/stephenbeale/flyweight/releases/latest/download/Flyweight-win-x64.zip)** instead:
unzip it anywhere (Documents, a USB stick) and double-click `Flyweight.exe`. Nothing is installed.

It's an early preview, so expect rough edges. Windows may say "Windows protected your PC" because the app isn't
code-signed yet: click **More info**, then **Run anyway**.

## What it does

- **Edit:** trim, split, reorder and copy clips; freeze a frame; change speed, zoom and brightness
- **Annotate:** arrows, boxes and text, drawn straight onto the video
- **Sound:** clip volume and mute, plus a music or narration track
- **YouTube:** export at 1080p, 1440p or 4K; chapter markers become description timestamps; title, description
  and tags saved ready to paste into YouTube Studio
- **Save frame:** grab any frame as a PNG, e.g. for a thumbnail
- **Smooth and safe:** big phone and 4K files stay smooth to edit; autosave and crash recovery

## Keyboard

| Key | Action | Key | Action |
|---|---|---|---|
| Space | Play / pause | S | Split at playhead |
| ← / → | Previous / next frame | Del | Delete selection |
| A / R / T | Arrow / box / text | V / Esc | Select tool |
| M | Chapter marker | Ctrl+Shift+C | Copy YouTube chapters |
| F | Freeze frame | Ctrl+Shift+E | Save frame as image |
| Shift+M | Mute clip | Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+I | Import | Ctrl+E | Export for YouTube |

## Feedback

Something confusing, slow or broken? [Open an issue](https://github.com/stephenbeale/flyweight/issues).

## Support

Flyweight is free. If it saves you time, you can [buy Steve a coffee ☕](https://buymeacoffee.com/stephenbeale).

## Licence

MIT. The download includes ffmpeg (GPL v3) and LibVLC (LGPL 2.1).
Building from source: see [docs/DEVELOPING.md](docs/DEVELOPING.md).
