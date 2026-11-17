using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Flyweight.Core.Media;

/// <summary>Finds ffmpeg/ffprobe: an "ffmpeg" folder next to the app, then the app folder, then PATH.</summary>
public static class FfmpegLocator
{
    public static string? OverrideDirectory { get; set; }

    public static string Ffmpeg => Resolve("ffmpeg");
    public static string Ffprobe => Resolve("ffprobe");

    static string Resolve(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in new[] { OverrideDirectory, Path.Combine(AppContext.BaseDirectory, "ffmpeg"), AppContext.BaseDirectory })
        {
            if (dir is null) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return exe; // fall back to PATH
    }
}

public sealed class FfmpegException(string message, string stderrTail) : Exception(message + Environment.NewLine + stderrTail)
{
    public string StderrTail { get; } = stderrTail;
}

/// <summary>
/// Runs ffmpeg/ffprobe as a separate process. Out-of-process is deliberate: a bad file can
/// crash ffmpeg without taking the editor (and the user's unsaved work) down with it.
/// </summary>
public static class FfmpegRunner
{
    /// <summary>
    /// Run ffmpeg. If <paramref name="expectedDuration"/> is given, progress (0..1) is reported
    /// by parsing ffmpeg's -progress output. Cancelling kills the process.
    /// </summary>
    public static async Task RunFfmpegAsync(IEnumerable<string> args, TimeSpan? expectedDuration = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        all.AddRange(args);

        await RunAsync(FfmpegLocator.Ffmpeg, all, line =>
        {
            if (progress is null || expectedDuration is not { } total || total <= TimeSpan.Zero) return;
            // ffmpeg prints out_time_us (and, confusingly, out_time_ms which is also microseconds).
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
            {
                progress.Report(Math.Clamp(us / 10.0 / total.Ticks, 0, 1));
            }
            else if (line == "progress=end")
            {
                progress.Report(1);
            }
        }, ct);
    }

    /// <summary>Run ffprobe and return its stdout.</summary>
    public static async Task<string> RunFfprobeAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await RunAsync(FfmpegLocator.Ffprobe, args, line => sb.AppendLine(line), ct);
        return sb.ToString();
    }

    static async Task RunAsync(string exe, IEnumerable<string> args, Action<string> onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a); // ArgumentList = no quoting bugs with spaces in paths

        using var proc = new Process { StartInfo = psi };
        var stderr = new Queue<string>();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdoutLine(e.Data); };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) { stderr.Enqueue(e.Data); if (stderr.Count > 30) stderr.Dequeue(); }
        };

        try { proc.Start(); }
        catch (Exception ex)
        {
            throw new FfmpegException($"Could not start '{exe}'. Put ffmpeg.exe and ffprobe.exe in the app's 'ffmpeg' folder or on PATH.", ex.Message);
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine(); // must drain stderr or ffmpeg blocks when the pipe fills

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        proc.WaitForExit(); // flush async output handlers

        if (proc.ExitCode != 0)
        {
            string tail;
            lock (stderr) tail = string.Join(Environment.NewLine, stderr);
            throw new FfmpegException($"{Path.GetFileName(exe)} failed (exit code {proc.ExitCode}).", tail);
        }
    }

    /// <summary>Seconds formatted for ffmpeg, independent of the user's locale (no "1,5").</summary>
    public static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
