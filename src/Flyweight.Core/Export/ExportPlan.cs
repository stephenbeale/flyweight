using System.Text;
using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;
using static Flyweight.Core.Media.FfmpegRunner;

namespace Flyweight.Core.Export;

/// <summary>
/// Turns the project into ONE ffmpeg command. Pure: no I/O, so it is unit-testable.
///
///   inputs:  one "-ss In -t Dur -i original" per clip (fast + accurate input seeking),
///            then one per audio-track item, then one per overlay-track clip,
///            then one transparent full-frame PNG per visible annotation
///   graph:   each clip → speed (setpts / atempo), fps, fit/pad to 16:9 (or zoom-crop), brightness/contrast curve,
///            48k stereo audio (silence if none)
///            → concat → amix the audio-track items and overlays' sound (adelay'd to their start) over the clip audio
///            → overlay each overlay-track clip, scaled to its box (OverlayLayout), bottom track first
///            → overlay each annotation PNG with enable='between(t,start,end)' (always on top)
///   output:  H.264 High / yuv420p / AAC 48k / +faststart  (YouTube's recommended upload)
///
/// Annotation PNGs are rendered by the UI using the SAME renderer as the preview, which is
/// what makes the export match the screen exactly.
/// </summary>
public sealed class ExportPlan
{
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string FilterGraph { get; init; }
    public required TimeSpan Duration { get; init; }
    public required double FrameRate { get; init; }

    /// <summary>
    /// The same export on the CPU (x264), used if the graphics chip's encoder can't start (no Intel GPU, old
    /// driver). Null when <see cref="Arguments"/> already use x264.
    /// </summary>
    public IReadOnlyList<string>? FallbackArguments { get; init; }

    /// <summary>Which encoder actually produced the file, once <see cref="RunAsync"/> has finished.</summary>
    public string UsedEncoder { get; private set; } = "";

    /// <param name="overlayPngs">Annotation id → path of a transparent PNG at the export size.</param>
    public static ExportPlan Build(Project p, ExportSettings settings, IReadOnlyDictionary<Guid, string> overlayPngs, string outputPath)
    {
        if (p.Timeline.Count == 0) throw new InvalidOperationException("The timeline is empty.");

        var (w, h) = settings.SizeFor(p.Shape);
        // Frame rate: the first VIDEO clip's (a still's nominal 30 fps shouldn't decide it).
        double fps = settings.FrameRate > 0 ? settings.FrameRate
                   : p.Timeline.Select(c => p.FindMedia(c.SourceId)).FirstOrDefault(m => m is { IsStill: false })?.Fps is > 0 and var f ? f : 30;
        string fpsStr = Num(Math.Round(fps, 3));

        var args = new List<string>();
        var graph = new StringBuilder();
        int input = 0;

        // 1. One input per clip.
        for (int i = 0; i < p.Timeline.Count; i++)
        {
            var clip = p.Timeline[i];
            if (clip.IsGap)
            {
                // A gap: black and silence, generated (no input file).
                if (clip.Duration < TimelineOps.MinClipLength)
                    throw new InvalidOperationException($"Gap {i + 1} is too short to export. Select it and delete it to close it.");
                var gd = Sec(clip.Duration);
                graph.Append($"color=c=black:s={w}x{h}:r={fpsStr}:d={gd},format=yuv420p,setsar=1[v{i}];\n");
                graph.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={gd}[a{i}];\n");
                continue;
            }
            var media = p.FindMedia(clip.SourceId) ?? throw new InvalidOperationException($"Clip {i + 1} refers to missing media.");
            // A zero/negative length would become trim=duration=0, which ffmpeg reads as "no
            // limit": with tpad's endless clone that is an export that never finishes.
            if (clip.SourceLength < TimelineOps.MinClipLength)
                throw new InvalidOperationException($"Clip {i + 1} ({media.DisplayName}) is too short to export. Delete it and re-import the file.");

            if (media.IsStill)
                // Decode the photo once a second, not at the output rate: the fps filter below repeats
                // frames. Decoding a 12 MP image 30×/s was the slow part of exporting stills.
                args.AddRange(new[] { "-loop", "1", "-framerate", "1", "-t", Sec(clip.Duration), "-i", media.OriginalPath });
            else
                args.AddRange(new[] { "-ss", Sec(clip.In), "-t", Sec(clip.SourceLength), "-i", media.OriginalPath });
            var d = Sec(clip.Duration); // length on the TIMELINE (after speed)
            double speed = media.IsStill ? 1 : clip.Speed;
            bool sped = Math.Abs(speed - 1) > 0.0005;

            // Zoom crops the original before scaling (sharp), and the tone curve runs at the output size (cheap).
            var fit = ClipLook.IsZoomed(clip) ? ClipLook.ZoomFilter(clip, w, h)
                    : ClipLook.BlursBars(p, clip, media) ? ClipLook.BlurFitFilter(w, h, $"bf{i}")
                    : Canvas.FitFilter(w, h);
            var pts = sped ? $"setpts=(PTS-STARTPTS)/{Num(speed)}" : "setpts=PTS-STARTPTS";
            graph.Append($"[{input}:v]{pts},fps={fpsStr},{ClipLook.MaskFilter(clip)}{fit},{ClipLook.ToneFilter(clip)}format=yuv420p,tpad=stop=-1:stop_mode=clone,trim=duration={d}[v{i}];\n");
            // A muted clip is treated like one with no audio: silence, and its track isn't decoded at all.
            var gain = clip.EffectiveVolume;
            if (media.HasAudio && gain > 0)
            {
                var vol = Math.Abs(gain - 1) < 0.001 ? "" : $"volume={Num(Math.Round(gain, 3))},";
                graph.Append($"[{input}:a]{AudioIn(media)}{(sped ? Tempo(speed) : "")}{vol}apad,atrim=duration={d}[a{i}];\n");
            }
            else
                graph.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={d}[a{i}];\n");
            input++;
        }

        // 2. Audio track items (music / narration): trimmed, gained, delayed to their timeline start.
        var total = TimelineOps.TotalDuration(p);
        var mix = new List<string>();
        foreach (var item in p.AudioTrack)
        {
            // Silent or entirely after the video ends: nothing to hear, so don't even decode it.
            if (item.EffectiveVolume <= 0 || item.Start >= total || item.Duration < TimelineOps.MinClipLength) continue;
            var media = p.FindMedia(item.SourceId) ?? throw new InvalidOperationException("An audio item refers to missing media.");
            args.AddRange(new[] { "-ss", Sec(item.In), "-t", Sec(item.Duration), "-i", media.OriginalPath });
            var vol = Math.Abs(item.EffectiveVolume - 1) < 0.001 ? "" : $"volume={Num(Math.Round(item.EffectiveVolume, 3))},";
            long delayMs = (long)Math.Round(item.Start.TotalMilliseconds);
            var label = $"m{mix.Count}";
            graph.Append($"[{input}:a]{AudioIn(media)}{vol}adelay={delayMs}:all=1[{label}];\n");
            mix.Add(label);
            input++;
        }

        // 2b. Overlay tracks (picture-in-picture / cutaways): each scaled to its box and shifted to its timeline start.
        // Bottom track first, so higher tracks are overlaid later (on top). Their own sound joins the mix.
        var overlays = new List<(string Label, int X, int Y, TimeSpan Start, TimeSpan End)>();
        foreach (var o in p.Overlays.OrderBy(o => o.Track))
        {
            if (o.Start >= total || o.Duration < TimelineOps.MinClipLength) continue; // after the video ends: never seen
            var media = p.FindMedia(o.SourceId) ?? throw new InvalidOperationException("An overlay refers to missing media.");
            if (media.IsAudioOnly) continue;
            var d = Sec(o.Duration); // length on the TIMELINE (after speed)
            if (media.IsStill) args.AddRange(new[] { "-loop", "1", "-framerate", "1", "-t", d, "-i", media.OriginalPath });
            else args.AddRange(new[] { "-ss", Sec(o.In), "-t", Sec(o.SourceLength), "-i", media.OriginalPath });
            double ospeed = media.IsStill ? 1 : o.Speed;
            bool osped = Math.Abs(ospeed - 1) > 0.0005;
            var (px, py, pw, ph) = OverlayLayout.PixelRect(o, media, w, h);
            var label = $"ov{overlays.Count}";
            // Zoom crops the ORIGINAL before scaling (sharp), like a main clip's zoom.
            graph.Append($"[{input}:v]{(osped ? $"setpts=(PTS-STARTPTS)/{Num(ospeed)}" : "setpts=PTS-STARTPTS")},fps={fpsStr},{OverlayLayout.CropFilter(o)}scale={pw}:{ph}:flags=bicubic,setsar=1,format=yuv420p," +
                         $"tpad=stop=-1:stop_mode=clone,trim=duration={d},setpts=PTS+{Sec(o.Start)}/TB[{label}];\n");
            var end = o.End < total ? o.End : total;
            overlays.Add((label, px, py, o.Start, end));
            if (media.HasAudio && o.EffectiveVolume > 0)
            {
                var vol = Math.Abs(o.EffectiveVolume - 1) < 0.001 ? "" : $"volume={Num(Math.Round(o.EffectiveVolume, 3))},";
                var al = $"m{mix.Count}";
                graph.Append($"[{input}:a]{AudioIn(media)}{(osped ? Tempo(ospeed) : "")}{vol}atrim=duration={d},adelay={(long)Math.Round(o.Start.TotalMilliseconds)}:all=1[{al}];\n");
                mix.Add(al);
            }
            input++;
        }

        // 3. Concat, then mix the audio track over the clips' sound. normalize=0 keeps every input
        // at its own level (amix's default would halve the clip audio as soon as music is added);
        // duration=first ends the mix with the video.
        for (int i = 0; i < p.Timeline.Count; i++) graph.Append($"[v{i}][a{i}]");
        if (mix.Count == 0)
            graph.Append($"concat=n={p.Timeline.Count}:v=1:a=1[vcat][aout]");
        else
            graph.Append($"concat=n={p.Timeline.Count}:v=1:a=1[vcat][acat];\n[acat]{string.Concat(mix.Select(m => $"[{m}]"))}" +
                         $"amix=inputs={mix.Count + 1}:duration=first:dropout_transition=0:normalize=0[aout]");

        // 4. Overlay-track video over the main track (eof_action=pass: nothing left behind once it ends).
        string last = "vcat";
        for (int j = 0; j < overlays.Count; j++)
        {
            var (label, x, y, s, e) = overlays[j];
            graph.Append($";\n[{last}][{label}]overlay={x}:{y}:eof_action=pass:enable='between(t,{Sec(s)},{Sec(e)})'[vo{j}]");
            last = $"vo{j}";
        }

        // 5. Annotation PNGs, in list order (later = on top), above everything.
        int k = 0;
        foreach (var a in p.Annotations)
        {
            if (!overlayPngs.TryGetValue(a.Id, out var png)) continue;
            if (TimelineOps.AnnotationRange(p, a) is not { } range) continue;

            var next = $"o{k++}";
            if (TimelineOps.FadeFor(p, a) is { } fade && (fade.In > 0 || fade.Out > 0))
            {
                // Fading: loop the PNG for the piece's length, fade its alpha in/out, then shift it to its start time.
                var len = range.End - range.Start;
                args.AddRange(new[] { "-loop", "1", "-framerate", fpsStr, "-t", Sec(len), "-i", png });
                var fades = (fade.In > 0 ? $",fade=t=in:st=0:d={Num(fade.In)}:alpha=1" : "") +
                            (fade.Out > 0 ? $",fade=t=out:st={Num(len.TotalSeconds - fade.Out)}:d={Num(fade.Out)}:alpha=1" : "");
                graph.Append($";\n[{input}:v]format=rgba{fades},setpts=PTS-STARTPTS+{Sec(range.Start)}/TB[af{k}]");
                graph.Append($";\n[{last}][af{k}]overlay=0:0:format=auto:eof_action=pass:enable='between(t,{Sec(range.Start)},{Sec(range.End)})'[{next}]");
            }
            else
            {
                args.AddRange(new[] { "-i", png });
                graph.Append($";\n[{last}][{input}:v]overlay=0:0:format=auto:enable='between(t,{Sec(range.Start)},{Sec(range.End)})'[{next}]");
            }
            last = next;
            input++;
        }
        graph.Append($";\n[{last}]format=yuv420p[vout]");

        var filter = graph.ToString();
        args.AddRange(new[] { "-filter_complex", filter, "-map", "[vout]", "-map", "[aout]" });
        int kbps = YouTubeBitrateKbps(Math.Min(w, h), fps); // by the short side: vertical 1080x1920 is "1080p"
        List<string> Finish(IEnumerable<string> video)
        {
            var a = new List<string>(args);
            a.AddRange(video);
            a.AddRange(new[]
            {
                "-r", fpsStr,
                "-c:a", "aac", "-b:a", "320k", "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart",
                "-write_tmcd", "0", // no camera timecode track: it crashed ffmpeg's mp4 writer on DJI proxies
                outputPath,
            });
            return a;
        }
        var x264 = Finish(X264Args(settings, kbps));
        return new ExportPlan
        {
            Arguments = settings.UseHardwareEncoder ? Finish(QsvArgs(kbps)) : x264,
            FallbackArguments = settings.UseHardwareEncoder ? x264 : null,
            FilterGraph = filter,
            Duration = total,
            FrameRate = fps,
        };
    }

    /// <summary>
    /// Video bitrate to aim for: YouTube's recommended upload rate for SDR, plus a third for headroom.
    /// YouTube re-encodes every upload, so more than this only makes the file bigger: uncapped CRF 18 made a
    /// 5½-minute brightened DJI clip 1.27 GB (32 Mbit/s); at 16 Mbit/s it's about 650 MB (D29).
    /// </summary>
    public static int YouTubeBitrateKbps(int height, double fps)
    {
        bool high = fps > 31;
        int recommended = height switch
        {
            >= 2160 => high ? 60_000 : 40_000,
            >= 1440 => high ? 24_000 : 16_000,
            _ => high ? 12_000 : 8_000,
        };
        return recommended * 4 / 3;
    }

    /// <summary>CPU encode: CRF 18 "veryfast" (it scored as well as "medium" on real footage at 2.6× the speed), capped.</summary>
    static string[] X264Args(ExportSettings s, int kbps) => new[]
    {
        "-c:v", "libx264", "-profile:v", "high", "-preset", s.Preset, "-crf", s.Crf.ToString(),
        "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-pix_fmt", "yuv420p",
    };

    /// <summary>
    /// Intel Quick Sync (the graphics chip's H.264 encoder): about 6.6× faster than x264 "medium" on an i5-10400T.
    /// Its constant-quality modes ignored the bitrate cap, so it runs at a target bitrate with a ceiling.
    /// </summary>
    static string[] QsvArgs(int kbps) => new[]
    {
        "-c:v", "h264_qsv", "-profile:v", "high",
        "-b:v", $"{kbps}k", "-maxrate", $"{kbps * 5 / 4}k", "-bufsize", $"{kbps * 5 / 2}k", "-pix_fmt", "nv12",
    };

    /// <summary>
    /// Start of every audio chain: reset timestamps, 48 kHz, float stereo. Mono is copied to both
    /// speakers at FULL level, as the preview (LibVLC) plays it: ffmpeg's default mono→stereo
    /// upmix is 3 dB quieter, so a mono voice-over would export quieter than it previewed.
    /// </summary>
    static string AudioIn(SourceMedia media) =>
        "asetpts=PTS-STARTPTS,aresample=48000," +
        (media.AudioChannels == 1 ? "pan=stereo|c0=c0|c1=c0," : "") +
        "aformat=sample_fmts=fltp:channel_layouts=stereo,";

    /// <summary>
    /// Speed the audio up/down keeping its pitch. One atempo stage handles 0.5–2, so 0.25 and 4 take two stages.
    /// Ends with a comma, like <see cref="AudioIn"/>.
    /// </summary>
    internal static string Tempo(double speed)
    {
        var stages = new List<string>();
        while (speed > 2.0005) { stages.Add("atempo=2"); speed /= 2; }
        while (speed < 0.4995) { stages.Add("atempo=0.5"); speed /= 0.5; }
        if (Math.Abs(speed - 1) > 0.0005) stages.Add($"atempo={Num(Math.Round(speed, 4))}");
        return string.Concat(stages.Select(s => s + ","));
    }

    /// <summary>Run the export; if the graphics chip's encoder fails, run it again on the CPU.</summary>
    public async Task RunAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            await FfmpegRunner.RunFfmpegAsync(Arguments, Duration, progress, ct);
            UsedEncoder = FallbackArguments is null ? "x264 (CPU)" : "Intel Quick Sync (graphics chip)";
        }
        catch (FfmpegException ex) when (FallbackArguments is not null && !ct.IsCancellationRequested && CpuRetryCouldHelp(ex))
        {
            progress?.Report(0);
            await FfmpegRunner.RunFfmpegAsync(FallbackArguments, Duration, progress, ct);
            UsedEncoder = "x264 (CPU)";
        }
    }

    /// <summary>
    /// Only retry on the CPU when the failure could be the graphics-chip encoder. A command that's too long, or a
    /// source file that's missing or unreadable, fails the same way on x264, so don't make the user wait twice.
    /// </summary>
    public static bool CpuRetryCouldHelp(FfmpegException ex) =>
        ex is not FfmpegCommandTooLongException &&
        !ex.StderrTail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) &&
        !ex.StderrTail.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase) &&
        !ex.StderrTail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);
}
