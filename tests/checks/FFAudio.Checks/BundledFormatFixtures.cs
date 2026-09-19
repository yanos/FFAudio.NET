using System;
using System.Collections.Generic;
using System.IO;

namespace FFAudio.Checks;

// Small tagged files covering the principal compressed codec/container
// families enabled in the default LGPL "slim" FFmpeg build.
public static class BundledFormatFixtures
{
    public const string Title = "LGPL format fixture";
    public const string Artist = "FFAudio.NET";
    public const string HighFidelityReference = "hires-48k.wav";

    public static IReadOnlyList<BundledFormatFixture> All { get; } =
    [
        new("tagged.flac", "flac", "flac", 44100),
        new("tagged.mp3", "mp3", "mp3", 44100),
        new("tagged-aac.m4a", "aac", "mov,mp4,m4a,3gp,3g2,mj2", 44100),
        new("tagged-alac.m4a", "alac", "mov,mp4,m4a,3gp,3g2,mj2", 44100),
        new("tagged-vorbis.ogg", "vorbis", "ogg", 44100),
        new("tagged-opus.ogg", "opus", "ogg", 48000),
        new("tagged.wv", "wavpack", "wv", 44100),
    ];

    public static IReadOnlyList<BundledFormatFixture> HighFidelity { get; } =
    [
        new("hires-48k.flac", "flac", "flac", 48000),
        new("hires-48k-alac.m4a", "alac", "mov,mp4,m4a,3gp,3g2,mj2", 48000),
        new("hires-48k.wv", "wavpack", "wv", 48000),
    ];

    public static Stream Open(string fileName)
    {
        var resourceName = $"{typeof(BundledFormatFixtures).Namespace}.Fixtures.{fileName}";
        return typeof(BundledFormatFixtures).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded fixture {resourceName} was not found.");
    }
}

public sealed record BundledFormatFixture(
    string FileName,
    string Codec,
    string Container,
    int SampleRate);
