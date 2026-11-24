FLYWEIGHT {VERSION}: early preview
====================================

A simple, lightweight Windows video editor for long YouTube videos: trim and split clips,
draw arrows, boxes and text on top, put a webcam or second video over the main one, zoom,
crop and brighten, change speed, add music, and export an MP4 ready for YouTube.

INSTALL
-------
1. Unzip this folder anywhere (e.g. Documents\Flyweight). Keep all the files together.
2. Double-click Flyweight.exe.
3. Windows may say "Windows protected your PC" (the app isn't code-signed yet).
   Click "More info", then "Run anyway". You only need to do this once.

Nothing else to install: .NET, the video player (LibVLC) and ffmpeg are all included.
Needs Windows 10 or 11, 64-bit.

GETTING STARTED
---------------
- New: name your project and pick the output size (1080p is right for most videos).
- Import (or drag files in): videos, photos and music. Big files show "Optimising" for a few
  minutes while Flyweight makes a smooth editing copy; you can keep working meanwhile.
- Double-click a file in the Media bin to add it to the timeline.
- Click a clip to see its Properties: volume, speed (type any value, e.g. 1.1), zoom, crop,
  brightness. Click a group's heading to fold it away.
- Overlays: select a file in the Media bin and press O to put it over the main video at the
  playhead. In the preview, drag it to move it, drag its corners to resize and its edges to crop.
- Deleting or trimming a clip keeps the overlays and music after it in sync.
- A / R / T: draw an arrow, box or text on the video.
- S splits at the playhead (Shift+S splits every track at once), Del deletes, Ctrl+A selects all clips, Ctrl+Z undoes.
- Export for YouTube: makes the MP4 (and a text file for title, description and chapters),
  then opens YouTube Studio for the upload.

Your work autosaves; if Flyweight closes unexpectedly it offers to recover it.

FEEDBACK
--------
This is an early preview, so things may break. Please report anything confusing, slow or
crashy: https://github.com/stephenbeale/flyweight/issues
(or just tell whoever sent you this).

LICENCES
--------
Flyweight is MIT-licensed (About\LICENSE.txt). It includes ffmpeg (GPL v3) and LibVLC (LGPL 2.1);
see About\THIRD-PARTY-NOTICES.txt for their licences and where to get their source code.
