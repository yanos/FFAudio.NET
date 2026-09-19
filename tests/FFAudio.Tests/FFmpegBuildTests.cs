using System;

using Xunit;

namespace FFAudio.Tests;

// Verify metadata reported by the loaded FFmpeg binary, including its license.
[Trait("Category", "RequiresNative")]
public class FFmpegBuildTests
{
    [Fact]
    public void The_binary_can_say_which_ffmpeg_is_inside_it()
    {
        Assert.False(string.IsNullOrWhiteSpace(FFmpegBuild.Version));
        Assert.False(string.IsNullOrWhiteSpace(FFmpegBuild.License));
    }

    // A real configure line exercises NativeText's grow-and-retry path.
    [Fact]
    public void The_configure_line_comes_back_whole()
    {
        var configuration = FFmpegBuild.Configuration;

        Assert.StartsWith("--", configuration);
        Assert.Equal(configuration, FFmpegBuild.Configuration);

        // Keep the fixture longer than NativeText's initial buffer.
        Assert.True(
            configuration.Length > 256,
            $"A configure line of {configuration.Length} characters fits the first buffer, " +
            "so nothing here grows one any more.");
    }

    [Fact]
    public void Redistributable_means_the_licence_says_lgpl()
    {
        Assert.Equal(
            FFmpegBuild.License.StartsWith("LGPL", StringComparison.Ordinal),
            FFmpegBuild.IsRedistributable);
    }

    // Package builds set FFAUDIO_REQUIRE_LGPL; developer FFmpeg builds may be GPL.
    [Fact]
    public void A_shipping_build_carries_an_lgpl_only_ffmpeg()
    {
        if (Environment.GetEnvironmentVariable("FFAUDIO_REQUIRE_LGPL") is not { Length: > 0 })
            Assert.Skip("FFAUDIO_REQUIRE_LGPL is not set; this build is not claiming to be shippable.");

        Assert.True(
            FFmpegBuild.IsRedistributable,
            $"FFmpeg here is under \"{FFmpegBuild.License}\", which cannot be shipped. " +
            $"It was configured with: {FFmpegBuild.Configuration}");

        Assert.DoesNotContain("--enable-gpl", FFmpegBuild.Configuration);
        Assert.DoesNotContain("--enable-nonfree", FFmpegBuild.Configuration);
    }
}
