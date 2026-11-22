using System.Diagnostics;
using System.Globalization;

namespace Flyweight.App.Playback;

/// <summary>
/// Debug-build diagnostics for playback glitches (seeks, loads, volume changes), written to
/// %LocalAppData%\Flyweight\playback.log and overwritten on each launch. Compiled out of Release builds.
/// </summary>
static class PlaybackLog
{
    static readonly object Gate = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyweight", "playback.log");
    static bool _started;

    [Conditional("DEBUG")]
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (!_started)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    File.WriteAllText(Path, $"Flyweight playback log, {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
                    _started = true;
                }
                File.AppendAllText(Path, $"{Clock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),9}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* diagnostics must never break playback */ }
    }
}
