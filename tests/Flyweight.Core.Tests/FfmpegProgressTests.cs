using Flyweight.Core.Media;

namespace Flyweight.Core.Tests;

public class FfmpegProgressTests
{
    static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData("out_time_us=0", 0.0)]
    [InlineData("out_time_us=60000000", 0.1)]     // 1 minute of 10 → 10%
    [InlineData("out_time_us=300000000", 0.5)]
    [InlineData("out_time_us=900000000", 1.0)]    // past the end: clamped
    [InlineData("progress=end", 1.0)]
    public void Progress_is_output_time_over_total(string line, double expected)
    {
        Assert.Equal(expected, FfmpegRunner.ParseProgress(line, TenMinutes)!.Value, 6);
    }

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time_us=-5")]
    [InlineData("frame=120")]
    [InlineData("progress=continue")]
    public void Other_lines_are_ignored(string line) =>
        Assert.Null(FfmpegRunner.ParseProgress(line, TenMinutes));
}
