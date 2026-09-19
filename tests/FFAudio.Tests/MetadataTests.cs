using System;
using System.IO;
using System.Linq;

using FFAudio.Checks;

using Xunit;

namespace FFAudio.Tests;

// Metadata behavior independent of decoded PCM.
[Trait("Category", "RequiresNative")]
public class MetadataTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ffaudio-meta").FullName;

    public void Dispose() => TempDirectory.DeleteWhenReleased(_directory);

    private string TaggedFixture(int sampleRate = 44100, int frames = 4410, int channels = 2) =>
        SyntheticTaggedAiff.CreateFile(_directory, $"tagged-{channels}ch.aiff", sampleRate, frames, channels);

    private string UntaggedFixture() =>
        SyntheticHiResWav.CreateFile(_directory, "plain.wav", 96000, 4800, SyntheticHiResWav.Ramp24());

    private static string? Tag(Decoder decoder, string key) =>
        decoder.Tags.FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void A_tagged_file_reports_what_it_was_tagged_with()
    {
        using var decoder = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16);

        Assert.Equal(SyntheticTaggedAiff.Title, Tag(decoder, "title"));
        Assert.Equal(SyntheticTaggedAiff.Artist, Tag(decoder, "artist"));
        Assert.Equal(SyntheticTaggedAiff.Album, Tag(decoder, "album"));
    }

    [Fact]
    public void A_file_with_no_tags_reports_none_rather_than_failing()
    {
        using var decoder = Decoder.OpenPath(UntaggedFixture(), SampleFormat.S24);

        Assert.Empty(decoder.Tags);
    }

    // Cover art must be returned without transcoding or rescaling.
    [Fact]
    public void Cover_art_comes_back_exactly_as_the_file_holds_it()
    {
        using var decoder = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16);

        var art = decoder.TryReadCoverArt();

        Assert.NotNull(art);
        Assert.Equal("image/png", art.MimeType);
        Assert.Equal(SyntheticTaggedAiff.CoverPng(), art.Bytes);
    }

    // Audio-only builds read dimensions directly from the PNG header.
    [Fact]
    public void Cover_art_reports_the_dimensions_in_the_image_header()
    {
        using var decoder = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16);

        var art = decoder.TryReadCoverArt();

        Assert.NotNull(art);
        Assert.Equal(1, art.Width);
        Assert.Equal(1, art.Height);
    }

    // Missing cover art is an ordinary null result.
    [Fact]
    public void A_file_with_no_cover_art_says_so_without_throwing()
    {
        using var decoder = Decoder.OpenPath(UntaggedFixture(), SampleFormat.S24);

        Assert.Null(decoder.TryReadCoverArt());
    }

    // Channel count alone cannot describe channel order.
    [Fact]
    public void The_layout_describes_the_pcm_being_delivered()
    {
        using var stereo = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16);

        Assert.Equal(2, stereo.Format.Channels);
        Assert.Equal("stereo", stereo.Format.ChannelLayout);
    }

    // Report the delivered layout after downmixing.
    [Fact]
    public void A_downmix_reports_the_layout_it_produces_not_the_one_it_read()
    {
        using var mono = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16, sampleRate: 0, channels: 1);

        Assert.Equal(2, mono.Format.SourceChannels);
        Assert.Equal(1, mono.Format.Channels);
        Assert.Equal("mono", mono.Format.ChannelLayout);
    }

    [Fact]
    public void A_file_names_its_codec_and_its_container()
    {
        using var aiff = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16);
        using var wav = Decoder.OpenPath(UntaggedFixture(), SampleFormat.S24);

        Assert.Equal("pcm_s16be", aiff.Format.Codec);
        Assert.Equal("aiff", aiff.Format.Container);

        Assert.Equal("pcm_s24le", wav.Format.Codec);
        Assert.Equal("wav", wav.Format.Container);
    }

    // Metadata must work through the managed Stream path.
    [Fact]
    public void A_stream_carries_its_metadata_the_same_way_a_path_does()
    {
        using var file = File.OpenRead(TaggedFixture());
        using var decoder = Decoder.OpenStream(file, SampleFormat.S16);

        Assert.Equal(SyntheticTaggedAiff.Title, Tag(decoder, "title"));
        Assert.NotNull(decoder.TryReadCoverArt());
    }

    // Reading tags must not advance or seek the packet stream.
    [Fact]
    public void Asking_for_tags_mid_decode_does_not_disturb_the_audio()
    {
        var path = TaggedFixture();

        using var straight = Decoder.OpenPath(path, SampleFormat.S16);
        var expected = DecodeAll(straight);

        using var interrupted = Decoder.OpenPath(path, SampleFormat.S16);
        var buffer = new byte[4096];
        var first = interrupted.Read(buffer);
        _ = interrupted.Tags;
        _ = interrupted.TryReadCoverArt();

        var rest = new MemoryStream();
        rest.Write(buffer, 0, first);
        int read;
        while ((read = interrupted.Read(buffer)) > 0)
            rest.Write(buffer, 0, read);

        Assert.Equal(expected, rest.ToArray());
    }

    private static byte[] DecodeAll(Decoder decoder)
    {
        var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }
}
