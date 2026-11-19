using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Flyweight.Core.Model;
using static Flyweight.Core.Media.FfmpegRunner;

namespace Flyweight.Core.Media;

public enum ProxyStatus { Queued, Optimising, Ready, Failed }

/// <summary>Runtime-only state for a source file. Deliberately NOT in the project model, so undo can't lose it.</summary>
public sealed class MediaState
{
    public ProxyStatus Status { get; internal set; } = ProxyStatus.Queued;
    public double Progress { get; internal set; }
    public string? ProxyPath { get; internal set; }
    public string? ThumbnailPath { get; internal set; }
    public string? Error { get; internal set; }
}

/// <summary>
/// Imports files and builds edit-friendly proxies in the background.
///
/// Proxy = 960×540, 16:9-padded, constant frame rate, EVERY frame a keyframe (-g 1).
/// That one step fixes: slow/inaccurate scrubbing (no long GOPs), variable-frame-rate phone
/// footage (forced CFR), and HEVC/10-bit decode cost (plain 8-bit H.264 at low res).
/// Proxies are cached by file identity, so re-opening a project is instant.
/// </summary>
public sealed class MediaLibrary
{
    readonly ConcurrentDictionary<Guid, MediaState> _states = new();
    readonly SemaphoreSlim _jobs = new(1, 1); // one transcode at a time keeps the UI machine responsive
    readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    readonly SemaphoreSlim _thumbJobs = new(2, 2); // thumbnails are ~1 s each; don't queue them behind transcodes

    public string CacheDirectory { get; }

    /// <summary>Raised (on a worker thread) whenever a media item's status or progress changes.</summary>
    public event EventHandler<Guid>? StateChanged;

    public MediaLibrary(string? cacheDirectory = null)
    {
        CacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyweight", "cache");
        Directory.CreateDirectory(CacheDirectory);
    }

    public MediaState GetState(Guid id) => _states.GetOrAdd(id, _ => new MediaState());

    /// <summary>Probe a file and return a new <see cref="SourceMedia"/>. Does not start the proxy.</summary>
    public Task<SourceMedia> ProbeAsync(string path, CancellationToken ct = default) => MediaProbe.ProbeAsync(path, ct);

    /// <summary>Make sure a proxy + thumbnail exist for this media. Safe to call repeatedly.</summary>
    public async Task EnsureProxyAsync(SourceMedia media, CancellationToken ct = default)
    {
        if (!_inFlight.TryAdd(media.Id, 0)) return; // already queued/working (e.g. import + project reload)
        try { await EnsureProxyCoreAsync(media, ct); }
        finally { _inFlight.TryRemove(media.Id, out _); }
    }

    async Task EnsureProxyCoreAsync(SourceMedia media, CancellationToken ct)
    {
        var state = GetState(media.Id);
        if (state.Status is ProxyStatus.Ready or ProxyStatus.Optimising) return;

        // Stills get their own key: images imported before D17 left ~0.04 s "video" proxies under the plain key.
        var dir = Path.Combine(CacheDirectory, CacheKey(media.OriginalPath) + (media.IsStill ? "-still" : ""));
        Directory.CreateDirectory(dir);
        var proxy = Path.Combine(dir, "proxy.mp4");
        var thumb = Path.Combine(dir, "thumb.jpg");

        if (File.Exists(proxy) && File.Exists(thumb))
        {
            Set(media.Id, s => { s.ProxyPath = proxy; s.ThumbnailPath = thumb; s.Progress = 1; s.Status = ProxyStatus.Ready; });
            return;
        }

        Set(media.Id, s => { s.Status = ProxyStatus.Queued; s.Progress = 0; s.Error = null; });

        // Thumbnail first, straight from the original, in its OWN small queue: one seek + decode takes
        // about a second, so the bin, timeline and preview get a picture without waiting behind
        // minutes of proxy transcodes.
        if (!File.Exists(thumb))
        {
            await _thumbJobs.WaitAsync(ct);
            try { await MakeThumbnailAsync(media, thumb, ct); }
            finally { _thumbJobs.Release(); }
        }
        if (File.Exists(thumb)) Set(media.Id, s => s.ThumbnailPath = thumb);

        await _jobs.WaitAsync(ct);
        try
        {
            Set(media.Id, s => s.Status = ProxyStatus.Optimising);
            var tmp = Path.Combine(dir, "proxy.partial.mp4");
            var fps = FfmpegRunner.Num(media.Fps > 0 ? media.Fps : 30);

            // Stills: loop the image, decoding it only once a second (decoding a 12 MP photo
            // 30×/s is what makes this slow). A static picture compresses to almost nothing, and
            // a keyframe every second is plenty for seeking because the other frames are trivial.
            var args = media.IsStill
                ? new List<string>
                {
                    "-loop", "1", "-framerate", "1", "-t", Sec(media.Duration), "-i", media.OriginalPath,
                    "-vf", $"{Canvas.FitFilter(Canvas.ProxyWidth, Canvas.ProxyHeight)},fps={fps},format=yuv420p",
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-g", fps, "-bf", "0",
                }
                : new List<string>
                {
                    "-i", media.OriginalPath,
                    "-vf", $"{Canvas.FitFilter(Canvas.ProxyWidth, Canvas.ProxyHeight)},fps={fps},format=yuv420p",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-g", "1", "-bf", "0",
                };
            args.AddRange(media.HasAudio
                ? new[] { "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2" }
                : new[] { "-an" });
            args.Add(tmp);

            var progress = new Progress<double>(p => Set(media.Id, s => s.Progress = p));
            await FfmpegRunner.RunFfmpegAsync(args, media.Duration, progress, ct);
            File.Move(tmp, proxy, overwrite: true);

            if (!File.Exists(thumb)) // original couldn't be thumbnailed: fall back to the proxy's first frame
            {
                try
                {
                    await FfmpegRunner.RunFfmpegAsync(new[]
                    {
                        "-i", proxy, "-frames:v", "1", "-update", "1", "-vf", "scale=320:-2", "-q:v", "4", thumb,
                    }, ct: ct);
                }
                catch (FfmpegException) { }
            }

            Set(media.Id, s =>
            {
                s.ProxyPath = proxy;
                s.ThumbnailPath = File.Exists(thumb) ? thumb : null;
                s.Progress = 1;
                s.Status = ProxyStatus.Ready;
            });
        }
        catch (OperationCanceledException)
        {
            Set(media.Id, s => s.Status = ProxyStatus.Queued);
            throw;
        }
        catch (Exception ex)
        {
            Set(media.Id, s => { s.Status = ProxyStatus.Failed; s.Error = ex.Message; });
        }
        finally
        {
            _jobs.Release();
        }
    }

    /// <summary>
    /// Save one full-resolution frame of the ORIGINAL file (not the 540p proxy) as a PNG, for a
    /// freeze frame. Stored under the app's data folder so it outlives the proxy cache.
    /// Returns the PNG path; probe it to get a still <see cref="SourceMedia"/>.
    /// </summary>
    public async Task<string> ExtractFrameAsync(SourceMedia media, TimeSpan sourceTime, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetDirectoryName(CacheDirectory)!, "frames");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(media.OriginalPath)} at {sourceTime.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}s {Guid.NewGuid().ToString("N")[..6]}.png");
        await FfmpegRunner.RunFfmpegAsync(new[]
        {
            // -ss before -i seeks fast; ffmpeg still decodes to the exact frame.
            "-ss", Sec(sourceTime), "-i", media.OriginalPath, "-frames:v", "1", "-update", "1", png,
        }, ct: ct);
        if (!File.Exists(png)) throw new FfmpegException($"Couldn't grab a frame from '{media.DisplayName}'.", "");
        return png;
    }

    /// <summary>
    /// 320×180 (16:9-padded) JPEG from the original. Written to a temp name then moved, so a reader
    /// never sees half a file. Failure is not fatal: the proxy step falls back to its first frame.
    /// </summary>
    static async Task MakeThumbnailAsync(SourceMedia media, string thumb, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(thumb)!, "thumb.partial.jpg");
        var args = new List<string>();
        if (!media.IsStill) args.AddRange(new[] { "-ss", Sec(TimeSpan.FromTicks(media.Duration.Ticks / 10)) });
        args.AddRange(new[]
        {
            "-i", media.OriginalPath, "-frames:v", "1", "-update", "1",
            "-vf", Canvas.FitFilter(320, 180), "-q:v", "4", tmp,
        });
        try
        {
            await FfmpegRunner.RunFfmpegAsync(args, ct: ct);
            if (File.Exists(tmp)) File.Move(tmp, thumb, overwrite: true);
        }
        catch (FfmpegException) { }
    }

    void Set(Guid id, Action<MediaState> change)
    {
        change(GetState(id));
        StateChanged?.Invoke(this, id);
    }

    /// <summary>Cache folder name from path + size + modified time, so an edited file gets a new proxy.</summary>
    static string CacheKey(string path)
    {
        var fi = new FileInfo(path);
        var id = $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(id)))[..16];
    }
}
