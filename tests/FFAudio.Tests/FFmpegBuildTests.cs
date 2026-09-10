using System;

using Xunit;

namespace FFAudio.Tests;

// Which FFmpeg is inside this binary, and whether it may be shipped.
//
// The licence is the one property of an FFmpeg build that cannot be corrected
// after the fact: a GPL-configured library linked into a released package is a
// licensing event, not a bug to fix in the next version. It is also the
// property least visible from the outside - the binary looks identical, every
// test passes, and every file decodes.
//
// So the answer comes from avutil rather than from the script that ran
// configure. The machine these tests usually run on is the case in point: its
// MacPorts FFmpeg reports "GPL version 2 or later", which is exactly right for
// development and must never be packaged.
[Trait("Category", "RequiresNative")]
public class FFmpegBuildTests
{
    [Fact]
    public void The_binary_can_say_which_ffmpeg_is_inside_it()
    {
        Assert.False(string.IsNullOrWhiteSpace(FFmpegBuild.Version));
        Assert.False(string.IsNullOrWhiteSpace(FFmpegBuild.License));
    }

    // The configure line is the half of the LGPL's relink route that the
    // binary can state for itself, and it is long - 951 characters for the
    // FFmpeg this was written against. That length is the point: it does not
    // fit the first buffer the façade is handed, so this is also the only
    // test that puts NativeText's grow-and-retry loop through more than one
    // iteration.
    [Fact]
    public void The_configure_line_comes_back_whole()
    {
        var configuration = FFmpegBuild.Configuration;

        Assert.StartsWith("--", configuration);
        Assert.Equal(configuration, FFmpegBuild.Configuration);

        // If this ever fails, the retry loop above stopped being exercised by
        // anything - not that the configure line is wrong.
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

    // The check a release has to pass, off by default because a developer's
    // machine is expected to fail it.
    //
    // Set FFAUDIO_REQUIRE_LGPL=1 wherever a package is built. Both halves are
    // asserted rather than only the licence string: avutil's answer is the
    // authority, and the configure line is what a person reads when the answer
    // is the wrong one.
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
