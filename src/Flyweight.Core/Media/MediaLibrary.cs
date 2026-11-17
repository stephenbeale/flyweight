using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Flyweight.Core.Model;

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
        var state = GetState(media.Id);
        if (state.Status is ProxyStatus.Ready or ProxyStatus.Optimising) return;

        var dir = Path.Combine(CacheDirectory, CacheKey(media.OriginalPath));
        Directory.CreateDirectory(dir);
        var proxy = Path.Combine(dir, "proxy.mp4");
        var thumb = Path.Combine(dir, "thumb.jpg");

        if (File.Exists(proxy) && File.Exists(thumb))
        {
            Set(media.Id, s => { s.ProxyPath = proxy; s.ThumbnailPath = thumb; s.Progress = 1; s.Status = ProxyStatus.Ready; });
            return;
        }

        Set(media.Id, s => { s.Status = ProxyStatus.Queued; s.Progress = 0; s.Error = null; });
        await _jobs.WaitAsync(ct);
        try
        {
            Set(media.Id, s => s.Status = ProxyStatus.Optimising);
            var tmp = Path.Combine(dir, "proxy.partial.mp4");
            var fps = FfmpegRunner.Num(media.Fps > 0 ? media.Fps : 30);

            var args = new List<string>
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

            var thumbAt = TimeSpan.FromTicks(media.Duration.Ticks / 10);
            await FfmpegRunner.RunFfmpegAsync(new[]
            {
                "-ss", FfmpegRunner.Sec(thumbAt), "-i", proxy, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "4", thumb,
            }, ct: ct);

            Set(media.Id, s => { s.ProxyPath = proxy; s.ThumbnailPath = thumb; s.Progress = 1; s.Status = ProxyStatus.Ready; });
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
