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
///            then one per audio-track item, then one transparent full-frame PNG per visible annotation
///   graph:   each clip → fps, fit/pad to 16:9 (or zoom-crop), brightness/contrast curve,
///            48k stereo audio (silence if none)
///            → concat → amix the audio-track items (adelay'd to their start) over the clip audio
///            → overlay each PNG with enable='between(t,start,end)'
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

    /// <param name="overlayPngs">Annotation id → path of a transparent PNG at the export size.</param>
    public static ExportPlan Build(Project p, ExportSettings settings, IReadOnlyDictionary<Guid, string> overlayPngs, string outputPath)
    {
        if (p.Timeline.Count == 0) throw new InvalidOperationException("The timeline is empty.");

        var (w, h) = settings.Size;
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
            var media = p.FindMedia(clip.SourceId) ?? throw new InvalidOperationException($"Clip {i + 1} refers to missing media.");
            // A zero/negative length would become trim=duration=0, which ffmpeg reads as "no
            // limit": with tpad's endless clone that is an export that never finishes.
            if (clip.Duration < TimelineOps.MinClipLength)
                throw new InvalidOperationException($"Clip {i + 1} ({media.DisplayName}) is too short to export. Delete it and re-import the file.");

            if (media.IsStill)
                // Decode the photo once a second, not at the output rate: the fps filter below repeats
                // frames. Decoding a 12 MP image 30×/s was the slow part of exporting stills.
                args.AddRange(new[] { "-loop", "1", "-framerate", "1", "-t", Sec(clip.Duration), "-i", media.OriginalPath });
            else
                args.AddRange(new[] { "-ss", Sec(clip.In), "-t", Sec(clip.Duration), "-i", media.OriginalPath });
            var d = Sec(clip.Duration);

            // Zoom crops the original before scaling (sharp), and the tone curve runs at the output size (cheap).
            var fit = ClipLook.IsZoomed(clip) ? ClipLook.ZoomFilter(clip, w, h) : Canvas.FitFilter(w, h);
            graph.Append($"[{input}:v]setpts=PTS-STARTPTS,fps={fpsStr},{fit},{ClipLook.ToneFilter(clip)}format=yuv420p,tpad=stop=-1:stop_mode=clone,trim=duration={d}[v{i}];\n");
            // A muted clip is treated like one with no audio: silence, and its track isn't decoded at all.
            var gain = clip.EffectiveVolume;
            if (media.HasAudio && gain > 0)
            {
                var vol = Math.Abs(gain - 1) < 0.001 ? "" : $"volume={Num(Math.Round(gain, 3))},";
                graph.Append($"[{input}:a]{AudioIn(media)}{vol}apad,atrim=duration={d}[a{i}];\n");
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

        // 3. Concat, then mix the audio track over the clips' sound. normalize=0 keeps every input
        // at its own level (amix's default would halve the clip audio as soon as music is added);
        // duration=first ends the mix with the video.
        for (int i = 0; i < p.Timeline.Count; i++) graph.Append($"[v{i}][a{i}]");
        if (mix.Count == 0)
            graph.Append($"concat=n={p.Timeline.Count}:v=1:a=1[vcat][aout]");
        else
            graph.Append($"concat=n={p.Timeline.Count}:v=1:a=1[vcat][acat];\n[acat]{string.Concat(mix.Select(m => $"[{m}]"))}" +
                         $"amix=inputs={mix.Count + 1}:duration=first:dropout_transition=0:normalize=0[aout]");

        // 4. Annotation overlays, in list order (later = on top).
        string last = "vcat";
        int k = 0;
        foreach (var a in p.Annotations)
        {
            if (!overlayPngs.TryGetValue(a.Id, out var png)) continue;
            if (TimelineOps.AnnotationRange(p, a) is not { } range) continue;

            args.AddRange(new[] { "-i", png });
            var next = $"o{k++}";
            graph.Append($";\n[{last}][{input}:v]overlay=0:0:format=auto:enable='between(t,{Sec(range.Start)},{Sec(range.End)})'[{next}]");
            last = next;
            input++;
        }
        graph.Append($";\n[{last}]format=yuv420p[vout]");

        var filter = graph.ToString();
        args.AddRange(new[]
        {
            "-filter_complex", filter,
            "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-profile:v", "high", "-preset", settings.Preset, "-crf", settings.Crf.ToString(),
            "-pix_fmt", "yuv420p", "-r", fpsStr,
            "-c:a", "aac", "-b:a", "320k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart",
            "-write_tmcd", "0", // no camera timecode track: it crashed ffmpeg's mp4 writer on DJI proxies
            outputPath,
        });

        return new ExportPlan
        {
            Arguments = args,
            FilterGraph = filter,
            Duration = total,
            FrameRate = fps,
        };
    }

    /// <summary>
    /// Start of every audio chain: reset timestamps, 48 kHz, float stereo. Mono is copied to both
    /// speakers at FULL level, as the preview (LibVLC) plays it: ffmpeg's default mono→stereo
    /// upmix is 3 dB quieter, so a mono voice-over would export quieter than it previewed.
    /// </summary>
    static string AudioIn(SourceMedia media) =>
        "asetpts=PTS-STARTPTS,aresample=48000," +
        (media.AudioChannels == 1 ? "pan=stereo|c0=c0|c1=c0," : "") +
        "aformat=sample_fmts=fltp:channel_layouts=stereo,";

    public Task RunAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        FfmpegRunner.RunFfmpegAsync(Arguments, Duration, progress, ct);
}
