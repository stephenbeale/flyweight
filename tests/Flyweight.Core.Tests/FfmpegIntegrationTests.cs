using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>
/// End-to-end tests that run the real ffmpeg. They generate synthetic clips, so no fixtures
/// are needed. Skipped automatically if ffmpeg isn't installed.
/// </summary>
public sealed class FfmpegIntegrationTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// True if ffmpeg runs. Locally the tests self-skip without it; CI sets
    /// FLYWEIGHT_REQUIRE_FFMPEG=1 so a broken ffmpeg install fails loudly instead of skipping.
    /// </summary>
    static bool FfmpegAvailable()
    {
        // Task.Run: blocking one of xUnit's few test threads here deadlocks parallel test classes.
        try { Task.Run(() => FfmpegRunner.RunFfmpegAsync(["-version"])).Wait(); return true; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("FLYWEIGHT_REQUIRE_FFMPEG") == "1")
                throw new InvalidOperationException("ffmpeg is required (FLYWEIGHT_REQUIRE_FFMPEG=1) but could not run.", ex);
            return false;
        }
    }

    async Task<string> MakeClip(string name, int w, int h, double seconds, string color, bool audio, double fps = 30)
    {
        var path = Path.Combine(_dir, name);
        var args = new List<string> { "-f", "lavfi", "-i", $"color=c={color}:s={w}x{h}:r={FfmpegRunner.Num(fps)}:d={FfmpegRunner.Num(seconds)}" };
        if (audio) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={FfmpegRunner.Num(seconds)}"]);
        // Long GOP like a phone, to prove the export doesn't depend on keyframes.
        args.AddRange(["-c:v", "libx264", "-g", "250", "-pix_fmt", "yuv420p"]);
        if (audio) args.AddRange(["-c:a", "aac"]);
        args.Add(path);
        await FfmpegRunner.RunFfmpegAsync(args);
        return path;
    }

    /// <summary>Read one RGB pixel from a video at time t.</summary>
    async Task<(int R, int G, int B)> Pixel(string video, double t, int x, int y)
    {
        var raw = Path.Combine(_dir, $"px-{Guid.NewGuid():N}.rgb");
        await FfmpegRunner.RunFfmpegAsync([
            "-ss", FfmpegRunner.Num(t), "-i", video, "-frames:v", "1",
            "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw,
        ]);
        var b = await File.ReadAllBytesAsync(raw);
        return (b[0], b[1], b[2]);
    }

    [Fact]
    public async Task Probe_reads_size_fps_duration_audio()
    {
        if (!FfmpegAvailable()) return;
        var path = await MakeClip("probe.mp4", 640, 360, 2, "blue", audio: true, fps: 25);
        var m = await MediaProbe.ProbeAsync(path);
        Assert.Equal(640, m.Width);
        Assert.Equal(360, m.Height);
        Assert.Equal(25, m.Fps, 1);
        Assert.InRange(m.Duration.TotalSeconds, 1.9, 2.2);
        Assert.True(m.HasAudio);
    }

    [Fact]
    public async Task Proxy_is_padded_16x9_and_cached()
    {
        if (!FfmpegAvailable()) return;
        var vertical = await MakeClip("vertical.mp4", 360, 640, 1, "green", audio: false);
        var lib = new MediaLibrary(Path.Combine(_dir, "cache"));
        var m = await lib.ProbeAsync(vertical);
        await lib.EnsureProxyAsync(m);

        var st = lib.GetState(m.Id);
        Assert.Equal(ProxyStatus.Ready, st.Status);
        var proxy = await MediaProbe.ProbeAsync(st.ProxyPath!);
        Assert.Equal((960, 540), (proxy.Width, proxy.Height));
        Assert.True(File.Exists(st.ThumbnailPath));

        // Second library instance (e.g. reopening the project) hits the cache instantly.
        var lib2 = new MediaLibrary(Path.Combine(_dir, "cache"));
        await lib2.EnsureProxyAsync(m);
        Assert.Equal(st.ProxyPath, lib2.GetState(m.Id).ProxyPath);
    }

    [Fact]
    public async Task Export_concats_trims_pads_and_overlays_annotations_at_the_right_time()
    {
        if (!FfmpegAvailable()) return;
        var red = await MakeClip("red.mp4", 1280, 720, 4, "red", audio: true);
        var blue = await MakeClip("blue.mp4", 480, 640, 3, "blue", audio: false, fps: 25); // vertical, silent, other fps

        var p = new Project();
        var mRed = await MediaProbe.ProbeAsync(red);
        var mBlue = await MediaProbe.ProbeAsync(blue);
        p.Media.AddRange([mRed, mBlue]);
        var c1 = TimelineOps.Append(p, mRed);
        TimelineOps.Append(p, mBlue);
        TimelineOps.TrimIn(p, c1.Id, TimeSpan.FromSeconds(1)); // red now 1–4s → 3s long. Total 6s.

        // White box over the top-left quarter, visible timeline 1.0–2.0s.
        var box = TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(1), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        TimelineOps.RetimeAnnotation(p, box.Id, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        // Stand-in for the UI's GDI+ renderer: a 1920×1080 PNG, opaque white top-left, transparent elsewhere.
        var png = Path.Combine(_dir, "box.png");
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", "color=c=white@1.0:s=960x540,format=rgba",
            "-vf", "pad=1920:1080:0:0:color=black@0.0", "-frames:v", "1", png,
        ]);

        var output = Path.Combine(_dir, "out.mp4");
        var plan = ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast" },
            new Dictionary<Guid, string> { [box.Id] = png }, output);

        var reports = new List<double>();
        await plan.RunAsync(new SyncProgress(reports.Add));

        var outInfo = await MediaProbe.ProbeAsync(output);
        Assert.Equal((1920, 1080), (outInfo.Width, outInfo.Height));
        Assert.InRange(outInfo.Duration.TotalSeconds, 5.9, 6.15);
        Assert.Equal(30, outInfo.Fps, 1); // first clip's rate
        Assert.True(outInfo.HasAudio);
        Assert.Contains(1.0, reports);

        static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
        static bool IsWhite((int R, int G, int B) c) => c.R > 200 && c.G > 200 && c.B > 200;
        static bool IsBlue((int R, int G, int B) c) => c.B > 200 && c.R < 60 && c.G < 60;
        static bool IsBlack((int R, int G, int B) c) => c.R < 40 && c.G < 40 && c.B < 40;

        Assert.True(IsRed(await Pixel(output, 0.5, 100, 100)), "before annotation: red");
        Assert.True(IsWhite(await Pixel(output, 1.5, 100, 100)), "during annotation: white box");
        Assert.True(IsRed(await Pixel(output, 1.5, 1500, 900)), "outside box: red");
        Assert.True(IsRed(await Pixel(output, 2.5, 100, 100)), "after annotation: red");
        Assert.True(IsBlue(await Pixel(output, 4.5, 960, 540)), "second clip: blue centre");
        Assert.True(IsBlack(await Pixel(output, 4.5, 50, 540)), "vertical clip: black side bars");
    }

    [Fact]
    public async Task Export_draws_overlay_tracks_in_their_box_at_their_time_with_separated_audio()
    {
        if (!FfmpegAvailable()) return;
        var red = await MakeClip("main.mp4", 1280, 720, 4, "red", audio: true);
        var blue = await MakeClip("cam.mp4", 640, 360, 3, "blue", audio: true);
        var green = await MakeClip("cut.mp4", 640, 360, 3, "green", audio: false);

        var p = new Project();
        var mRed = await MediaProbe.ProbeAsync(red);
        var mBlue = await MediaProbe.ProbeAsync(blue);
        var mGreen = await MediaProbe.ProbeAsync(green);
        var main = TimelineOps.Append(p, mRed);
        // Blue picture-in-picture, right half, 1.0–2.0 s; green full-frame cutaway on V3, 3.0–3.5 s.
        var pip = TimelineOps.AddOverlay(p, mBlue, TimeSpan.FromSeconds(1));
        TimelineOps.TrimOverlayOut(p, pip.Id, TimeSpan.FromSeconds(1));
        TimelineOps.SetOverlayLayout(p, pip.Id, 0.75, 0.5, 0.5);
        var cut = TimelineOps.AddOverlay(p, mGreen, TimeSpan.FromSeconds(3), track: 2);
        TimelineOps.TrimOverlayOut(p, cut.Id, TimeSpan.FromSeconds(0.5));
        TimelineOps.PlaceOverlay(p, cut.Id, OverlayPosition.FullFrame);
        Assert.NotNull(TimelineOps.SeparateAudio(p, main.Id)); // the main clip's sound now comes from the audio track

        var output = Path.Combine(_dir, "overlay.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, new Dictionary<Guid, string>(), output).RunAsync();

        var info = await MediaProbe.ProbeAsync(output);
        Assert.InRange(info.Duration.TotalSeconds, 3.9, 4.15); // the main track sets the length
        Assert.True(info.HasAudio);

        static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
        static bool IsBlue((int R, int G, int B) c) => c.B > 200 && c.R < 60 && c.G < 60;
        static bool IsGreen((int R, int G, int B) c) => c.G > 90 && c.R < 60 && c.B < 60; // lavfi "green" is #008000

        Assert.True(IsRed(await Pixel(output, 0.5, 1440, 540)), "before the overlay: main picture");
        Assert.True(IsBlue(await Pixel(output, 1.5, 1440, 540)), "during: overlay in its box (right half)");
        Assert.True(IsRed(await Pixel(output, 1.5, 400, 540)), "during: main picture outside the box");
        Assert.True(IsRed(await Pixel(output, 2.5, 1440, 540)), "after the overlay: main picture again");
        Assert.True(IsGreen(await Pixel(output, 3.25, 100, 100)), "full-frame cutaway covers everything");

        // Save frame (D33) puts the overlay in too, in the same place.
        var png = Path.Combine(_dir, "frame.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, FrameExport.LocateFrame(p, TimeSpan.FromSeconds(1.5))!.Value, 1920, 1080, png));
        Assert.True(IsBlue(await Pixel(png, 0, 1440, 540)), "saved frame: overlay");
        Assert.True(IsRed(await Pixel(png, 0, 400, 540)), "saved frame: main picture");
    }

    [Fact]
    public async Task Zoomed_overlay_exports_and_saves_only_the_cropped_part()
    {
        if (!FfmpegAvailable()) return;
        var red = await MakeClip("main2.mp4", 1280, 720, 2, "red", audio: false);
        // Overlay source: left half blue, right half yellow.
        var split = Path.Combine(_dir, "split.mp4");
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", "color=c=blue:s=640x360:r=30:d=2,drawbox=x=320:y=0:w=320:h=360:color=yellow:t=fill",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", split,
        ]);

        var p = new Project();
        TimelineOps.Append(p, await MediaProbe.ProbeAsync(red));
        var o = TimelineOps.AddOverlay(p, await MediaProbe.ProbeAsync(split), TimeSpan.Zero);
        TimelineOps.SetOverlayLayout(p, o.Id, 0.5, 0.5, 0.5); // box = middle of the frame, 960×540 at 1080p
        TimelineOps.SetOverlayZoom(p, o.Id, 2);
        TimelineOps.SetOverlayPan(p, o.Id, 1, 0.5);            // the right half's middle: all yellow

        static bool IsYellow((int R, int G, int B) c) => c.R > 200 && c.G > 200 && c.B < 80;
        static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;

        var output = Path.Combine(_dir, "zoomed.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, new Dictionary<Guid, string>(), output).RunAsync();
        Assert.True(IsYellow(await Pixel(output, 1, 520, 540)), "box's left edge shows the crop (yellow), not the source's left (blue)");
        Assert.True(IsYellow(await Pixel(output, 1, 1400, 540)), "box's right edge: yellow");
        Assert.True(IsRed(await Pixel(output, 1, 300, 540)), "outside the box: main picture");

        var png = Path.Combine(_dir, "zoomed.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, FrameExport.LocateFrame(p, TimeSpan.FromSeconds(1))!.Value, 1920, 1080, png));
        Assert.True(IsYellow(await Pixel(png, 0, 520, 540)), "saved frame: cropped the same way");
    }

    [Fact]
    public async Task Cropped_clip_and_overlay_export_only_what_is_kept()
    {
        if (!FfmpegAvailable()) return;
        // Main source: left half green, right half blue. Cropping off the left half leaves only blue.
        var main = Path.Combine(_dir, "halves.mp4");
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", "color=c=green:s=1280x720:r=30:d=2,drawbox=x=640:y=0:w=640:h=720:color=blue:t=fill",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", main,
        ]);
        // Overlay source: a white band down the left quarter, the rest red. Cropping the left quarter leaves only red.
        var band = Path.Combine(_dir, "band.mp4");
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", "color=c=red:s=640x360:r=30:d=2,drawbox=x=0:y=0:w=160:h=360:color=white:t=fill",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", band,
        ]);

        var p = new Project();
        var clip = TimelineOps.Append(p, await MediaProbe.ProbeAsync(main));
        TimelineOps.SetClipCrop(p, clip.Id, CropSide.Left, 0.45); // blacks out the left 45%, in place
        TimelineOps.SetClipCrop(p, clip.Id, CropSide.Left, 0.5);  // clamped to 45%
        var o = TimelineOps.AddOverlay(p, await MediaProbe.ProbeAsync(band), TimeSpan.Zero);
        TimelineOps.SetOverlayLayout(p, o.Id, 0.5, 0.25, 0.4);
        TimelineOps.SetOverlayCrop(p, o.Id, CropSide.Left, 0.25);
        var (bx, by, bw, bh) = OverlayLayout.PixelRect(o, p.FindMedia(o.SourceId), 1920, 1080);
        Assert.Equal(576, bw); // 40% of 1920 = 768 for the whole picture; 3/4 of it kept

        static bool IsBlue((int R, int G, int B) c) => c.B > 200 && c.R < 60 && c.G < 60;
        static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
        static bool IsBlack((int R, int G, int B) c) => c.R < 40 && c.G < 40 && c.B < 40;
        static bool IsGreen((int R, int G, int B) c) => c.G > 90 && c.R < 60 && c.B < 60;

        var output = Path.Combine(_dir, "cropped.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, new Dictionary<Guid, string>(), output).RunAsync();
        // Blacked out in place: nothing moves, the left 45% (864 px at 1080p) is black, the right half still blue.
        Assert.True(IsBlack(await Pixel(output, 1, 100, 800)), "main: cropped edge is black");
        Assert.True(IsBlue(await Pixel(output, 1, 1200, 800)), "main: the kept right part (blue)");
        Assert.True(IsGreen(await Pixel(output, 1, 900, 800)), "main: green just right of the cut, where it always was: it didn't slide");
        Assert.True(IsRed(await Pixel(output, 1, bx + 6, by + bh / 2)), "overlay: its left edge is red, the white band was cropped off");

        var png = Path.Combine(_dir, "cropped.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, FrameExport.LocateFrame(p, TimeSpan.FromSeconds(1))!.Value, 1920, 1080, png));
        Assert.True(IsBlack(await Pixel(png, 0, 100, 800)), "saved frame: same bar");
        Assert.True(IsRed(await Pixel(png, 0, bx + 6, by + bh / 2)), "saved frame: same overlay crop");
    }

    async Task<string> MakeImage(string name, int w, int h, string color)
    {
        var path = Path.Combine(_dir, name);
        await FfmpegRunner.RunFfmpegAsync(["-f", "lavfi", "-i", $"color=c={color}:s={w}x{h}", "-frames:v", "1", path]);
        return path;
    }

    [Theory]
    [InlineData("still.png")]
    [InlineData("still.jpg")]
    public async Task Images_import_as_8s_stills_with_proxy_and_thumbnail(string name)
    {
        if (!FfmpegAvailable()) return;
        var img = await MakeImage(name, 800, 1000, "yellow");
        var lib = new MediaLibrary(Path.Combine(_dir, "cache"));
        var m = await lib.ProbeAsync(img);
        Assert.True(m.IsStill);
        Assert.False(m.HasAudio);
        Assert.Equal((800, 1000), (m.Width, m.Height));
        Assert.Equal(SourceMedia.StillMaxDuration, m.Duration);

        var p = new Project();
        p.Media.Add(m);
        Assert.Equal(TimelineOps.DefaultStillLength, TimelineOps.Append(p, m).Duration);

        await lib.EnsureProxyAsync(m);
        var st = lib.GetState(m.Id);
        Assert.Equal(ProxyStatus.Ready, st.Status);
        Assert.True(File.Exists(st.ThumbnailPath));
        var proxy = await MediaProbe.ProbeAsync(st.ProxyPath!);
        Assert.Equal((960, 540), (proxy.Width, proxy.Height));
        Assert.InRange(proxy.Duration.TotalSeconds, SourceMedia.StillMaxDuration.TotalSeconds - 1, SourceMedia.StillMaxDuration.TotalSeconds + 1);
    }

    /// <summary>RMS loudness (0–1) of a video's audio between two times, mixed to mono.</summary>
    async Task<double> Rms(string video, double from, double to)
    {
        var raw = Path.Combine(_dir, $"pcm-{Guid.NewGuid():N}.raw");
        await FfmpegRunner.RunFfmpegAsync([
            "-ss", FfmpegRunner.Num(from), "-t", FfmpegRunner.Num(to - from), "-i", video,
            "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", raw,
        ]);
        var b = await File.ReadAllBytesAsync(raw);
        double sum = 0;
        int n = b.Length / 2;
        for (int i = 0; i < n; i++) { double s = BitConverter.ToInt16(b, i * 2) / 32768.0; sum += s * s; }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }

    [Fact]
    public async Task Export_applies_clip_volume_and_mute_to_the_audio()
    {
        if (!FfmpegAvailable()) return;
        var tone = await MakeClip("tone.mp4", 640, 360, 6, "red", audio: true);
        var m = await MediaProbe.ProbeAsync(tone);

        // Three 2 s clips of the same tone: 100%, muted, 50%.
        var p = new Project();
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        TimelineOps.SplitAt(p, TimeSpan.FromSeconds(2));
        TimelineOps.SplitAt(p, TimeSpan.FromSeconds(4));
        TimelineOps.SetClipMuted(p, p.Timeline[1].Id, true);
        TimelineOps.SetClipVolume(p, p.Timeline[2].Id, 0.5);

        var output = Path.Combine(_dir, "volume-out.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast" }, new Dictionary<Guid, string>(), output).RunAsync();

        // Measure the middle of each clip, away from AAC edge effects at the cuts.
        var loud = await Rms(output, 0.5, 1.5);
        var silent = await Rms(output, 2.5, 3.5);
        var half = await Rms(output, 4.5, 5.5);
        Assert.True(loud > 0.05, $"100% clip should be audible (rms {loud:0.000})");
        Assert.True(silent < loud * 0.01, $"muted clip should be silent (rms {silent:0.0000} vs {loud:0.000})");
        Assert.InRange(half / loud, 0.45, 0.55);
    }

    async Task<string> MakeTone(string name, double seconds, int hz = 440)
    {
        var path = Path.Combine(_dir, name);
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", $"sine=frequency={hz}:duration={FfmpegRunner.Num(seconds)}", path,
        ]);
        return path;
    }

    [Theory]
    [InlineData("song.mp3")]
    [InlineData("voice.wav")]
    [InlineData("music.m4a")]
    public async Task Audio_files_import_as_audio_only_with_a_waveform(string name)
    {
        if (!FfmpegAvailable()) return;
        var path = await MakeTone(name, 3);
        var lib = new MediaLibrary(Path.Combine(_dir, "cache"));
        var m = await lib.ProbeAsync(path);
        Assert.True(m.IsAudioOnly);
        Assert.True(m.HasAudio);
        Assert.False(m.IsStill);
        Assert.InRange(m.Duration.TotalSeconds, 2.9, 3.2);

        await lib.EnsureProxyAsync(m);
        var st = lib.GetState(m.Id);
        Assert.Equal(ProxyStatus.Ready, st.Status);
        Assert.Equal(m.OriginalPath, st.ProxyPath); // played as-is, no proxy transcode
        Assert.True(File.Exists(st.ThumbnailPath), "waveform image");
    }

    [Fact]
    public async Task Export_mixes_the_audio_track_over_clip_audio_at_its_timeline_time()
    {
        if (!FfmpegAvailable()) return;
        var silent = await MakeClip("silent.mp4", 640, 360, 4, "gray", audio: false);
        var tone = await MakeTone("music.mp3", 10);

        var p = new Project();
        var mVideo = await MediaProbe.ProbeAsync(silent);
        var mTone = await MediaProbe.ProbeAsync(tone);
        p.Media.AddRange([mVideo, mTone]);
        TimelineOps.Append(p, mVideo);
        TimelineOps.AddAudio(p, mTone, TimeSpan.FromSeconds(2)); // 10 s of music from 0:02, video ends at 0:04

        var output = Path.Combine(_dir, "mixed.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast" }, new Dictionary<Guid, string>(), output).RunAsync();

        var info = await MediaProbe.ProbeAsync(output);
        Assert.InRange(info.Duration.TotalSeconds, 3.9, 4.15); // music is cut at the video's end
        var before = await Rms(output, 0.5, 1.5);
        var during = await Rms(output, 2.5, 3.5);
        var source = await Rms(tone, 0.5, 1.5); // the music file itself, measured the same way
        Assert.True(before < 0.001, $"silent before the music starts (rms {before:0.0000})");
        Assert.InRange(during / source, 0.9, 1.1); // full level, not scaled down by the mix
    }

    [Fact]
    public async Task Export_shows_a_gap_as_black_and_keeps_later_clips_and_tracks_at_their_time()
    {
        if (!FfmpegAvailable()) return;
        var red = await MakeClip("gap-red.mp4", 1280, 720, 2, "red", audio: true, fps: 25);
        var blue = await MakeClip("gap-blue.mp4", 1280, 720, 2, "blue", audio: true, fps: 25);
        var green = await MakeClip("gap-green.mp4", 640, 360, 1, "green", audio: false, fps: 25);

        var p = new Project();
        var mRed = await MediaProbe.ProbeAsync(red);
        var mBlue = await MediaProbe.ProbeAsync(blue);
        var mGreen = await MediaProbe.ProbeAsync(green);
        var a = TimelineOps.Append(p, mRed);                    // 0–2
        TimelineOps.Append(p, mBlue);                           // 2–4
        var o = TimelineOps.AddOverlay(p, mGreen, TimeSpan.FromSeconds(0.5)); // full frame over the (soon) gap
        TimelineOps.PlaceOverlay(p, o.Id, OverlayPosition.FullFrame);
        TimelineOps.Lift(p, a.Id);                              // red → 2 s gap; nothing moves

        var output = Path.Combine(_dir, "gap-out.mp4");
        var plan = ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast" }, new Dictionary<Guid, string>(), output);
        await plan.RunAsync();

        var info = await MediaProbe.ProbeAsync(output);
        Assert.InRange(info.Duration.TotalSeconds, 3.9, 4.15);
        var gap = await Pixel(output, 0.2, 960, 540);
        Assert.True(gap.R < 30 && gap.G < 30 && gap.B < 30, "the gap is black");
        var ov = await Pixel(output, 1.0, 960, 540);
        Assert.True(ov.G > 100 && ov.R < 60 && ov.B < 60, "the overlay still shows over the gap at its time");
        var later = await Pixel(output, 3.0, 960, 540);
        Assert.True(later.B > 200 && later.R < 60, "the next clip stays at its time");
    }

    [Fact]
    public async Task Export_holds_a_still_for_its_clip_length_after_video()
    {
        if (!FfmpegAvailable()) return;
        var red = await MakeClip("red2.mp4", 1280, 720, 2, "red", audio: true, fps: 25);
        var img = await MakeImage("blue.png", 1200, 1600, "blue"); // portrait screenshot

        var p = new Project();
        var mRed = await MediaProbe.ProbeAsync(red);
        var mImg = await MediaProbe.ProbeAsync(img);
        p.Media.AddRange([mImg, mRed]);
        var still = TimelineOps.Append(p, mImg);
        TimelineOps.TrimOut(p, still.Id, TimeSpan.FromSeconds(3)); // 8s default → 3s to keep the test fast
        TimelineOps.Append(p, mRed);

        var output = Path.Combine(_dir, "still-out.mp4");
        var plan = ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast" }, new Dictionary<Guid, string>(), output);
        var run = plan.RunAsync();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(2)));
        Assert.True(finished == run, "export of a still must finish (a 0-length trim used to run forever)");
        await run;

        var outInfo = await MediaProbe.ProbeAsync(output);
        Assert.InRange(outInfo.Duration.TotalSeconds, 4.9, 5.15);
        Assert.Equal(25, outInfo.Fps, 1); // the video's rate, not the still's nominal 30

        var c = await Pixel(output, 1.5, 960, 540);
        Assert.True(c.B > 200 && c.R < 60 && c.G < 60, "still: blue centre");
        var bar = await Pixel(output, 1.5, 50, 540);
        Assert.True(bar.R < 40 && bar.G < 40 && bar.B < 40, "portrait still: black side bars");
        var v = await Pixel(output, 4.0, 960, 540);
        Assert.True(v.R > 200 && v.G < 60 && v.B < 60, "then the video: red");
    }

    [Fact]
    public async Task Freeze_frame_grabs_full_resolution_frame_from_the_original()
    {
        if (!FfmpegAvailable()) return;
        // 1s green then 1s magenta: grab at 1.5s must be magenta, at full 1280×720.
        var green = await MakeClip("g.mp4", 1280, 720, 1, "green", audio: false);
        var magenta = await MakeClip("m.mp4", 1280, 720, 1, "magenta", audio: false);
        var both = Path.Combine(_dir, "both.mp4");
        await FfmpegRunner.RunFfmpegAsync(["-i", green, "-i", magenta, "-filter_complex", "[0:v][1:v]concat=n=2:v=1[v]", "-map", "[v]", "-c:v", "libx264", "-g", "250", both]);

        var lib = new MediaLibrary(Path.Combine(_dir, "cache"));
        var m = await lib.ProbeAsync(both);
        var png = await lib.ExtractFrameAsync(m, TimeSpan.FromSeconds(1.5));
        var still = await lib.ProbeAsync(png);
        Assert.True(still.IsStill);
        Assert.Equal((1280, 720), (still.Width, still.Height));
        var c = await Pixel(png, 0, 640, 360);
        Assert.True(c.R > 200 && c.G < 60 && c.B > 200, $"expected magenta, got {c}");
    }

    [Fact]
    public async Task Export_zooms_and_brightens_clips_like_the_preview()
    {
        if (!FfmpegAvailable()) return;
        // 1: left half red, right half blue. 2: portrait green (pillarboxed). 3: dark grey.
        var halves = Path.Combine(_dir, "halves.mp4");
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", "color=c=red:s=640x720:r=30:d=1", "-f", "lavfi", "-i", "color=c=blue:s=640x720:r=30:d=1",
            "-filter_complex", "[0:v][1:v]hstack=inputs=2[v]", "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", halves,
        ]);
        var portrait = await MakeClip("portrait.mp4", 360, 640, 1, "green", audio: false);
        var grey = await MakeClip("grey.mp4", 1280, 720, 1, "0x404040", audio: false);

        var p = new Project();
        var mH = await MediaProbe.ProbeAsync(halves);
        var mP = await MediaProbe.ProbeAsync(portrait);
        var mG = await MediaProbe.ProbeAsync(grey);
        p.Media.AddRange([mH, mP, mG]);
        var c1 = TimelineOps.Append(p, mH);
        var c2 = TimelineOps.Append(p, mP);
        var c3 = TimelineOps.Append(p, mG);
        TimelineOps.SetClipZoom(p, c1.Id, 2);
        TimelineOps.SetClipPan(p, c1.Id, 1, 0.5);      // right half only → all blue
        TimelineOps.SetClipZoom(p, c2.Id, 2);           // centred: the pillarboxed clip fills more of the frame
        TimelineOps.SetClipBrightness(p, c3.Id, 0.5);

        var output = Path.Combine(_dir, "look-out.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast" }, new Dictionary<Guid, string>(), output).RunAsync();
        Assert.Equal((1920, 1080), ((await MediaProbe.ProbeAsync(output)).Width, (await MediaProbe.ProbeAsync(output)).Height));

        static bool IsBlue((int R, int G, int B) c) => c.B > 200 && c.R < 60 && c.G < 60;
        static bool IsGreen((int R, int G, int B) c) => c.G > 90 && c.R < 60 && c.B < 60; // lavfi "green" is 0,128,0
        static bool IsBlack((int R, int G, int B) c) => c.R < 40 && c.G < 40 && c.B < 40;

        Assert.True(IsBlue(await Pixel(output, 0.5, 100, 540)), "zoomed into the right half: left edge is blue");
        Assert.True(IsBlue(await Pixel(output, 0.5, 1800, 540)), "and the right edge too");
        // Portrait fills canvas x 0.342–0.658; zoomed 2× about the centre that becomes 0.184–0.816 of the output.
        Assert.True(IsGreen(await Pixel(output, 1.5, 420, 540)), "zoomed portrait reaches x=420");
        Assert.True(IsBlack(await Pixel(output, 1.5, 250, 540)), "bars still outside it");
        var g = await Pixel(output, 2.5, 960, 540);
        int expected = ClipLook.Tone(0x40, ClipLook.Gamma(0.5), ClipLook.ContrastGain(0));
        Assert.True(expected > 100, "the curve should lift 64 a lot");
        Assert.InRange(g.G, expected - 5, expected + 5);
    }

    [Fact]
    public async Task Export_plays_a_2x_clip_in_half_the_time_with_its_audio()
    {
        if (!FfmpegAvailable()) return;
        // 2 s green then 2 s magenta, with a tone. At 2× it lasts 2 s and is magenta from 1 s.
        var green = await MakeClip("sg.mp4", 640, 360, 2, "green", audio: true);
        var magenta = await MakeClip("sm.mp4", 640, 360, 2, "magenta", audio: true);
        var both = Path.Combine(_dir, "gm.mp4");
        await FfmpegRunner.RunFfmpegAsync([
            "-i", green, "-i", magenta, "-filter_complex", "[0:v][0:a][1:v][1:a]concat=n=2:v=1:a=1[v][a]",
            "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", both,
        ]);
        var p = new Project();
        var m = await MediaProbe.ProbeAsync(both);
        p.Media.Add(m);
        var c = TimelineOps.Append(p, m);
        TimelineOps.SetClipSpeed(p, c.Id, 2);

        var output = Path.Combine(_dir, "fast.mp4");
        var plan = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), output);
        await plan.RunAsync(); // graphics chip if this machine has one, else the x264 fallback
        Assert.NotEqual("", plan.UsedEncoder);

        var info = await MediaProbe.ProbeAsync(output);
        Assert.InRange(info.Duration.TotalSeconds, 1.9, 2.15);
        var early = await Pixel(output, 0.5, 320, 180);
        var late = await Pixel(output, 1.5, 320, 180);
        Assert.True(early.G > 90 && early.R < 60, $"0.5 s = 1 s of footage: green, got {early}");
        Assert.True(late.R > 200 && late.B > 200 && late.G < 60, $"1.5 s = 3 s of footage: magenta, got {late}");
        Assert.True(await Rms(output, 0.2, 1.8) > 0.05, "the sped-up audio is there");
    }

    [Fact]
    public async Task Export_plays_a_2x_overlay_in_half_the_time_and_save_frame_matches()
    {
        if (!FfmpegAvailable()) return;
        // Overlay footage: 2 s green then 2 s magenta. At 2x from 1 s on the timeline it shows 1–3 s,
        // green until 2 s (footage 2 s) and magenta after; the red main picture is around it.
        var green = await MakeClip("og.mp4", 640, 360, 2, "green", audio: false);
        var magenta = await MakeClip("om.mp4", 640, 360, 2, "magenta", audio: false);
        var both = Path.Combine(_dir, "ogm.mp4");
        await FfmpegRunner.RunFfmpegAsync([
            "-i", green, "-i", magenta, "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
            "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", both,
        ]);
        var red = await MakeClip("om-main.mp4", 1280, 720, 4, "red", audio: false);
        var p = new Project();
        TimelineOps.Append(p, await MediaProbe.ProbeAsync(red));
        var o = TimelineOps.AddOverlay(p, await MediaProbe.ProbeAsync(both), TimeSpan.FromSeconds(1));
        TimelineOps.PlaceOverlay(p, o.Id, OverlayPosition.FullFrame);
        TimelineOps.SetOverlaySpeed(p, o.Id, 2);
        Assert.Equal(TimeSpan.FromSeconds(3), o.End);

        var output = Path.Combine(_dir, "fast-overlay.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, new Dictionary<Guid, string>(), output).RunAsync();

        static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
        static bool IsGreen((int R, int G, int B) c) => c.G > 90 && c.R < 60 && c.B < 60;
        static bool IsMagenta((int R, int G, int B) c) => c.R > 200 && c.B > 200 && c.G < 60;
        Assert.True(IsRed(await Pixel(output, 0.5, 960, 540)), "before the overlay");
        Assert.True(IsGreen(await Pixel(output, 1.5, 960, 540)), "1.5 s = footage 1 s: green");
        Assert.True(IsMagenta(await Pixel(output, 2.5, 960, 540)), "2.5 s = footage 3 s: magenta");
        Assert.True(IsRed(await Pixel(output, 3.5, 960, 540)), "after the overlay (it ended at 3 s, not 5 s)");

        var png = Path.Combine(_dir, "fast-overlay.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, FrameExport.LocateFrame(p, TimeSpan.FromSeconds(2.5))!.Value, 1920, 1080, png));
        Assert.True(IsMagenta(await Pixel(png, 0, 960, 540)), "Save frame shows the same footage as the export");
    }

    sealed class SyncProgress(Action<double> a) : IProgress<double>
    {
        public void Report(double value) => a(value);
    }
}
