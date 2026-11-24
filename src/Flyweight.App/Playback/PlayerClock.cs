using System.Diagnostics;

namespace Flyweight.App.Playback;

/// <summary>
/// Where a playing LibVLC player really is. Its <c>Time</c> only updates every few hundred ms, so read raw it nearly
/// always looks a few hundred ms behind: drift checks on the raw value re-seeked overlays and audio-track items about
/// every second, each seek cost real time, and they never caught up (audible / visible hitches). Like the main
/// engine's estimate: the last reported time plus how long ago it was reported. Only trusted once the reported time
/// has CHANGED since the last seek, i.e. we've seen a fresh value.
/// </summary>
sealed class PlayerClock
{
    static readonly TimeSpan MaxExtrapolation = TimeSpan.FromSeconds(1);
    long _seekedAt = long.MinValue;
    long _last = long.MinValue;
    bool _fresh;
    readonly Stopwatch _since = new();

    /// <summary>Playback rate (a sped-up overlay advances faster than the wall clock).</summary>
    public double Rate { get; set; } = 1;

    /// <summary>Call after seeking or (re)starting the player.</summary>
    public void Reset(long rawTimeNow)
    {
        _seekedAt = _last = rawTimeNow;
        _fresh = false;
        _since.Restart();
    }

    /// <summary>The estimated position in ms, or null while there's no fresh reading to go on.</summary>
    public long? Estimate(long rawTime)
    {
        if (rawTime != _last)
        {
            _fresh |= rawTime != _seekedAt;
            _last = rawTime;
            _since.Restart();
        }
        if (!_fresh) return null;
        var extra = _since.Elapsed < MaxExtrapolation ? _since.Elapsed : MaxExtrapolation;
        return _last + (long)(extra.TotalMilliseconds * Rate);
    }
}
