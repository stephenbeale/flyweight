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

        // Long projects (many clips/annotations) make a filter graph too long for Windows' ~32k-character command
        // line. Pass long graphs through a temp file instead (D-note "long filter graphs" in DECISIONS).
        string? scriptFile = null;
        int fi = all.IndexOf("-filter_complex");
        if (fi >= 0 && fi + 1 < all.Count && all[fi + 1].Length > MaxInlineFilterLength)
        {
            scriptFile = Path.Combine(Path.GetTempPath(), $"flyweight-filter-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(scriptFile, all[fi + 1], ct);
            all = UseFilterScript(all, scriptFile, await SupportsSlashOptionsAsync());
        }
        if (all.Sum(a => a.Length + 3) > MaxCommandLineLength)
            throw new FfmpegException("This project is too big to export in one go (too many clips or annotations for Windows' command-line limit).", "");

        try
        {
            await RunAsync(FfmpegLocator.Ffmpeg, all, line =>
            {
                if (progress is null || expectedDuration is not { } total) return;
                if (ParseProgress(line, total) is { } fraction) progress.Report(fraction);
            }, ct, background);
        }
        finally
        {
            if (scriptFile is not null) try { File.Delete(scriptFile); } catch { /* temp file; harmless if left */ }
        }
    }

    /// <summary>Filter graphs longer than this go through a file. Normal projects stay well under it.</summary>
    public const int MaxInlineFilterLength = 4000;

    /// <summary>Windows' CreateProcess limit is 32,767 characters; leave headroom for the exe path.</summary>
    public const int MaxCommandLineLength = 30000;

    /// <summary>
    /// Replace "-filter_complex GRAPH" with the file form. ffmpeg 7+ reads any option's value from a file with
    /// "-/filter_complex FILE" (and deprecates -filter_complex_script); older builds only know -filter_complex_script.
    /// </summary>
    public static List<string> UseFilterScript(List<string> args, string scriptFile, bool slashOptions)
    {
        var result = new List<string>(args);
        int i = result.IndexOf("-filter_complex");
        if (i < 0 || i + 1 >= result.Count) return result;
        result[i] = slashOptions ? "-/filter_complex" : "-filter_complex_script";
        result[i + 1] = scriptFile;
        return result;
    }

    static Task<bool>? _slashOptions;

    /// <summary>True if the ffmpeg in use supports "-/option file" (version 7 or later, or a date-stamped nightly).</summary>
    public static Task<bool> SupportsSlashOptionsAsync() => _slashOptions ??= DetectSlashOptionsAsync();

    static async Task<bool> DetectSlashOptionsAsync()
    {
        var firstLine = "";
        try
        {
            await RunAsync(FfmpegLocator.Ffmpeg, new[] { "-hide_banner", "-version" },
                line => { if (firstLine.Length == 0) firstLine = line; }, CancellationToken.None);
        }
        catch { return false; }
        return ParseMajorVersion(firstLine) is >= 7;
    }

    /// <summary>"ffmpeg version 7.1-full_build-www.gyan.dev" → 7; "ffmpeg version 2025-01-02-git-…" → 2025; "n6.1.1" → 6.</summary>
    public static int? ParseMajorVersion(string versionLine)
    {
        const string prefix = "ffmpeg version ";
        if (!versionLine.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var v = versionLine[prefix.Length..].TrimStart('n', 'N');
        int end = 0;
        while (end < v.Length && char.IsDigit(v[end])) end++;
        return end > 0 && int.TryParse(v[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : null;
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
