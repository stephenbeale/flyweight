using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class ProxyArgsTests
{
    static SourceMedia Video(bool audio = true) => new()
    {
        OriginalPath = @"E:\clips\DJI_0229.MP4", Duration = TimeSpan.FromMinutes(14), Fps = 59.94, Width = 1920, Height = 1080, HasAudio = audio,
    };

    [Fact]
    public void Proxy_keeps_only_first_video_and_audio_with_no_timecode_track()
    {
        var a = MediaLibrary.ProxyArgs(Video(), "p.mp4", hwDecode: true);
        Assert.Contains("0:v:0", a);
        Assert.Contains("0:a:0", a);
        // DJI files carry a timecode track that crashed ffmpeg's mp4 writer at the end of a long proxy.
        int i = a.IndexOf("-write_tmcd");
        Assert.True(i >= 0 && a[i + 1] == "0");
        Assert.Contains("-map_metadata", a);
        Assert.Equal("p.mp4", a[^1]);
    }

    [Fact]
    public void Gpu_decoding_is_optional_so_a_failed_proxy_can_retry_on_the_cpu()
    {
        Assert.Contains("-hwaccel", MediaLibrary.ProxyArgs(Video(), "p.mp4", hwDecode: true));
        Assert.DoesNotContain("-hwaccel", MediaLibrary.ProxyArgs(Video(), "p.mp4", hwDecode: false));
    }

    [Fact]
    public void Silent_videos_and_stills_map_no_audio()
    {
        Assert.DoesNotContain("0:a:0", MediaLibrary.ProxyArgs(Video(audio: false), "p.mp4", true));
        var still = new SourceMedia { OriginalPath = "a.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Fps = 30 };
        var s = MediaLibrary.ProxyArgs(still, "p.mp4", true);
        Assert.DoesNotContain("0:a:0", s);
        Assert.DoesNotContain("-hwaccel", s);
    }
}
