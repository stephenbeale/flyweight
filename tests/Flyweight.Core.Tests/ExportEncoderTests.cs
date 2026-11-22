using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class ExportEncoderTests
{
    static ExportPlan Plan(ExportSettings s)
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = TimeSpan.FromSeconds(10), Fps = 59.94, Width = 1920, Height = 1080, HasAudio = true };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        return ExportPlan.Build(p, s, new Dictionary<Guid, string>(), "out.mp4");
    }

    static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Theory]
    [InlineData(1080, 30, 10_666)]
    [InlineData(1080, 59.94, 16_000)]
    [InlineData(1440, 60, 32_000)]
    [InlineData(2160, 25, 53_333)]
    [InlineData(2160, 60, 80_000)]
    public void Bitrate_is_YouTubes_recommendation_plus_a_third(int height, double fps, int kbps) =>
        Assert.Equal(kbps, ExportPlan.YouTubeBitrateKbps(height, fps));

    [Fact]
    public void Graphics_chip_first_with_x264_as_the_fallback()
    {
        var plan = Plan(new ExportSettings());
        Assert.Equal("h264_qsv", After(plan.Arguments, "-c:v"));
        Assert.Equal("16000k", After(plan.Arguments, "-b:v"));
        Assert.NotNull(plan.FallbackArguments);
        Assert.Equal("libx264", After(plan.FallbackArguments!, "-c:v"));
        Assert.Equal("veryfast", After(plan.FallbackArguments!, "-preset"));
        Assert.Equal("16000k", After(plan.FallbackArguments!, "-maxrate")); // capped: uncapped CRF 18 made 32 Mbit/s files
        // Same picture either way: only the encoder differs.
        Assert.Equal(After(plan.Arguments, "-filter_complex"), After(plan.FallbackArguments!, "-filter_complex"));
        Assert.Equal("out.mp4", plan.Arguments[^1]);
        Assert.Equal("out.mp4", plan.FallbackArguments![^1]);
    }

    [Fact]
    public void Cpu_only_has_no_fallback()
    {
        var plan = Plan(new ExportSettings { UseHardwareEncoder = false });
        Assert.Equal("libx264", After(plan.Arguments, "-c:v"));
        Assert.Null(plan.FallbackArguments);
    }
}
