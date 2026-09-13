using System;
using System.IO;
using System.Linq;

using FFAudio.Checks;

using Xunit;

namespace FFAudio.Tests;

// What a file says about itself, as opposed to what it decodes to.
//
// These exist because the alternative to answering them is that every
// consumer reaches past the façade into FFmpeg for them, and then has two
// routes to FFmpeg to build, ship and keep in step - which is the outcome
// this library exists to prevent. Flower will keep reading tags with TagLib#
// and that is fine; a general audio library that decodes a file and cannot
// say its title is one nobody adopts.
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

    // The bytes, unaltered. A façade that re-encoded or rescaled cover art
    // would be making a decision that belongs to whatever is going to draw it.
    [Fact]
    public void Cover_art_comes_back_exactly_as_the_file_holds_it()
    {
        using var decoder = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16);

        var art = decoder.TryReadCoverArt();

        Assert.NotNull(art);
        Assert.Equal("image/png", art.MimeType);
        Assert.Equal(SyntheticTaggedAiff.CoverPng(), art.Bytes);
    }

    // Read out of the PNG's own header rather than by decoding it, which is
    // the only route available: the shipped FFmpeg is audio-only and has no
    // decoder that could size a picture. That parse is also what keeps
    // avformat_find_stream_info from giving up on the art stream and warning
    // "Could not find codec parameters ... unspecified size" on every
    // art-bearing file opened - so a zero here is the noise coming back.
    //
    // Worth knowing about this assertion: a development build linked against
    // a full FFmpeg gets these dimensions from find_stream_info regardless, so
    // it is the slim builds - the phones, and CI's static desktop ones - where
    // this genuinely guards the parser.
    [Fact]
    public void Cover_art_reports_the_dimensions_in_the_image_header()
    {
        using var decoder = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16);

        var art = decoder.TryReadCoverArt();

        Assert.NotNull(art);
        Assert.Equal(1, art.Width);
        Assert.Equal(1, art.Height);
    }

    // Null rather than an exception: most music files have no embedded art,
    // and a caller asking is not making a mistake.
    [Fact]
    public void A_file_with_no_cover_art_says_so_without_throwing()
    {
        using var decoder = Decoder.OpenPath(UntaggedFixture(), SampleFormat.S24);

        Assert.Null(decoder.TryReadCoverArt());
    }

    // The whole point of the layout string: `channels` is a number, and a
    // number cannot say which channel is which.
    [Fact]
    public void The_layout_describes_the_pcm_being_delivered()
    {
        using var stereo = Decoder.OpenPath(TaggedFixture(), SampleFormat.S16);

        Assert.Equal(2, stereo.Format.Channels);
        Assert.Equal("stereo", stereo.Format.ChannelLayout);
    }

    // Delivered, not source: a caller that asked for a downmix has to be told
    // about the channels it is going to get, not the ones the file had.
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

    // Metadata is answered off the container, so it has to work on a stream
    // the caller supplied as much as on a path - that is the overload the
    // streaming half of this library exists for.
    [Fact]
    public void A_stream_carries_its_metadata_the_same_way_a_path_does()
    {
        using var file = File.OpenRead(TaggedFixture());
        using var decoder = Decoder.OpenStream(file, SampleFormat.S16);

        Assert.Equal(SyntheticTaggedAiff.Title, Tag(decoder, "title"));
        Assert.NotNull(decoder.TryReadCoverArt());
    }

    // Reading tags must not disturb the decode. They come out of the format
    // context rather than the packet stream, so asking mid-track is a
    // question about the file and not a seek.
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
