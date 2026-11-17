Put `ffmpeg.exe` and `ffprobe.exe` here (an **LGPL** build if you plan to distribute, e.g.
the "lgpl" Windows builds from https://github.com/BtbN/FFmpeg-Builds/releases — note those
use OpenH264 rather than x264; for a GPL build with libx264 the export is better quality but
GPL terms apply to distribution).

They are copied next to Flyweight.exe on build. If this folder is empty, Flyweight falls back
to whatever `ffmpeg` is on your PATH.
