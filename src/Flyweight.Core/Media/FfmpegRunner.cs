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
    /// <paramref name="background"/>: run at below-normal CPU priority (proxy transcodes), so playback
    /// and the UI always win when they compete for the cores.
    /// </summary>
    public static async Task RunFfmpegAsync(IEnumerable<string> args, TimeSpan? expectedDuration = null,
        IProgress<double>? progress = null, CancellationToken ct = default, bool background = false)
    {
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        all.AddRange(args);

        await RunAsync(FfmpegLocator.Ffmpeg, all, line =>
        {
            if (progress is null || expectedDuration is not { } total) return;
            if (ParseProgress(line, total) is { } fraction) progress.Report(fraction);
        }, ct, background);
    }

    /// <summary>
    /// One line of ffmpeg's -progress output → fraction done (0–1), or null if the line isn't progress.
    /// ffmpeg prints out_time_us (and, confusingly, out_time_ms which is also microseconds).
    /// </summary>
    public static double? ParseProgress(string line, TimeSpan total)
    {
        if (line == "progress=end") return 1;
        if (total <= TimeSpan.Zero || !line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
            !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us < 0)
            return null;
        // 1 µs = 10 ticks. (This used to divide by 10, so every progress bar showed 1/100 of the truth.)
        return Math.Clamp(us * 10.0 / total.Ticks, 0, 1);
    }

    /// <summary>Run ffprobe and return its stdout.</summary>
    public static async Task<string> RunFfprobeAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await RunAsync(FfmpegLocator.Ffprobe, args, line => sb.AppendLine(line), ct);
        return sb.ToString();
    }

    static readonly object StartLock = new();

    /// <summary>
    /// Start the process, optionally at below-normal priority. On Windows the priority is INHERITED at
    /// creation: we briefly drop our own priority class while starting it. Setting it on the process
    /// afterwards isn't enough, because a launcher such as Chocolatey's ffmpeg.exe shim starts the real
    /// ffmpeg immediately at normal priority.
    /// </summary>
    static void Start(Process proc, bool background)
    {
        lock (StartLock) // every start goes through here, so nothing else inherits the lowered class by accident
        {
            if (!background) { proc.Start(); return; }
            if (!OperatingSystem.IsWindows())
            {
                proc.Start();
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }
                return;
            }
            var me = Process.GetCurrentProcess();
            var old = me.PriorityClass;
            try
            {
                me.PriorityClass = ProcessPriorityClass.BelowNormal;
                proc.Start();
            }
            finally { me.PriorityClass = old; }
        }
    }

    static async Task RunAsync(string exe, IEnumerable<string> args, Action<string> onStdoutLine, CancellationToken ct, bool background = false)
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

        try { Start(proc, background); }
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
