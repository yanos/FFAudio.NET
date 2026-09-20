using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

using FFAudio.Checks;

using Microsoft.Extensions.Logging;

using Xunit;

namespace FFAudio.Tests;

// End-to-end tests requiring a native built by native/build-all.sh.
[Trait("Category", "RequiresNative")]
public class DecoderTests : IDisposable
{
    private const int HiResRate = 96000;
    private const int Frames = 24000; // a quarter second at 96kHz

    private readonly string _directory = Directory.CreateTempSubdirectory("ffaudio").FullName;

    public void Dispose() => TempDirectory.DeleteWhenReleased(_directory);

    private string HiResFixture(string name = "hires.wav") =>
        SyntheticHiResWav.CreateFile(_directory, name, HiResRate, Frames, SyntheticHiResWav.Ramp24());

    private static byte[] DecodeAll(Decoder decoder)
    {
        var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }

    // Verify that 24-bit source precision reaches the caller intact.
    [Fact]
    public void A_24_bit_source_is_delivered_with_every_bit_intact()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.S24);
        var pcm = DecodeAll(decoder);

        Assert.Equal(24, decoder.Format.SourceBitDepth);
        Assert.Equal(HiResRate, decoder.Format.SampleRate);
        Assert.Equal(Frames * 6, pcm.Length);

        var expected = SyntheticHiResWav.Ramp24();
        for (var frame = 0; frame < Frames; frame++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                var offset = frame * 6 + channel * 3;
                Assert.Equal(expected(frame), SyntheticHiResWav.ReadInt24(pcm.AsSpan(offset, 3)));
            }
        }
    }

    // Lossless compressed formats must preserve meaningful low bits, not only
    // advertise a high-resolution source in their headers.
    [Theory]
    [InlineData("hires-48k.flac", "flac", "flac")]
    [InlineData("hires-48k-alac.m4a", "alac", "mov,mp4,m4a,3gp,3g2,mj2")]
    [InlineData("hires-48k.wv", "wavpack", "wv")]
    public void Lossless_formats_preserve_48kHz_24_bit_stereo_audio(
        string fileName, string codec, string container)
    {
        using var referenceSource = BundledFormatFixtures.Open(BundledFormatFixtures.HighFidelityReference);
        using var reference = Decoder.OpenStream(referenceSource, SampleFormat.S24);
        var expected = DecodeAll(reference);
        var samplesWithLowBits = expected.Where((value, index) => index % 3 == 0 && value != 0).Count();

        using var source = BundledFormatFixtures.Open(fileName);
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24);
        var actual = DecodeAll(decoder);

        Assert.Equal(codec, decoder.Format.Codec);
        Assert.Equal(container, decoder.Format.Container);
        Assert.Equal(48000, decoder.Format.SourceSampleRate);
        Assert.Equal(24, decoder.Format.SourceBitDepth);
        Assert.Equal(2, decoder.Format.SourceChannels);
        Assert.Equal(SampleFormat.S24, decoder.Format.SampleFormat);
        Assert.Equal(6, decoder.Format.BytesPerFrame);
        Assert.True(samplesWithLowBits > expected.Length / 3 / 2,
            $"Only {samplesWithLowBits} of {expected.Length / 3} reference samples use their low byte");
        Assert.Equal(expected, actual);
    }

    // S16 conversion should discard the source's low eight bits.
    [Fact]
    public void The_same_source_asked_for_as_16_bit_loses_the_low_bits()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.S16);
        var pcm = DecodeAll(decoder);

        Assert.Equal(Frames * 4, pcm.Length);

        var expected = SyntheticHiResWav.Ramp24();
        var differing = 0;
        for (var frame = 0; frame < Frames; frame++)
        {
            var delivered = BitConverter.ToInt16(pcm, frame * 4);
            if (delivered != (short)(expected(frame) >> 8))
                differing++;
        }

        // Conversion may round rather than truncate by one step.
        Assert.True(differing < Frames / 100, $"{differing} of {Frames} frames did not match a 16-bit truncation");
    }

    // FFmpeg represents 24-bit input as left-aligned S32 with a padding low byte.
    [Fact]
    public void An_S32_delivery_of_a_24_bit_source_leaves_the_low_byte_empty()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.S32);
        var pcm = DecodeAll(decoder);

        Assert.Equal(Frames * 8, pcm.Length);

        var occupied = 0;
        for (var offset = 0; offset < pcm.Length; offset += 4)
        {
            if (pcm[offset] != 0)
                occupied++;
        }

        Assert.Equal(0, occupied);
    }

    // Packed S24 must equal S32 with only its padding byte removed.
    [Fact]
    public void S24_is_S32_with_the_padding_dropped()
    {
        var path = HiResFixture();

        using var packed = Decoder.OpenPath(path, SampleFormat.S24);
        var s24 = DecodeAll(packed);

        using var wide = Decoder.OpenPath(path, SampleFormat.S32);
        var s32 = DecodeAll(wide);

        Assert.Equal(s24.Length / 3, s32.Length / 4);

        var dropped = new byte[s24.Length];
        for (int source = 0, destination = 0; source < s32.Length; source += 4, destination += 3)
        {
            dropped[destination] = s32[source + 1];
            dropped[destination + 1] = s32[source + 2];
            dropped[destination + 2] = s32[source + 3];
        }

        Assert.Equal(s24, dropped);
    }

    // A float significand represents every 24-bit sample exactly.
    [Fact]
    public void F32_carries_a_24_bit_source_with_no_rounding_at_all()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.F32);
        var pcm = DecodeAll(decoder);

        Assert.Equal(Frames * 8, pcm.Length);

        var expected = SyntheticHiResWav.Ramp24();
        var differing = 0;
        for (var frame = 0; frame < Frames; frame++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                var delivered = BitConverter.ToSingle(pcm, frame * 8 + channel * 4);
                if (delivered != expected(frame) / 8388608f)
                    differing++;
            }
        }

        Assert.Equal(0, differing);
    }

    [Fact]
    public void Paths_deliver_F32_by_default()
    {
        using var decoder = Decoder.OpenPath(HiResFixture());
        var pcm = DecodeAll(decoder);

        Assert.Equal(SampleFormat.F32, decoder.Format.SampleFormat);
        Assert.Equal(8, decoder.Format.BytesPerFrame);
        Assert.Equal(Frames * 8, pcm.Length);
    }

    [Fact]
    public void Streams_deliver_F32_by_default()
    {
        using var source = File.OpenRead(HiResFixture());
        using var decoder = Decoder.OpenStream(source);
        var pcm = DecodeAll(decoder);

        Assert.Equal(SampleFormat.F32, decoder.Format.SampleFormat);
        Assert.Equal(8, decoder.Format.BytesPerFrame);
        Assert.Equal(Frames * 8, pcm.Length);
    }

    // Advertised frame widths must match the bytes actually emitted.
    [Theory]
    [InlineData(SampleFormat.S16, 2)]
    [InlineData(SampleFormat.S24, 3)]
    [InlineData(SampleFormat.S32, 4)]
    [InlineData(SampleFormat.F32, 4)]
    public void Every_format_delivers_the_width_it_advertises(SampleFormat format, int bytesPerSample)
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), format);
        var pcm = DecodeAll(decoder);

        Assert.Equal(format, decoder.Format.SampleFormat);
        Assert.Equal(2, decoder.Format.Channels);
        Assert.Equal(bytesPerSample * 2, decoder.Format.BytesPerFrame);
        Assert.Equal(Frames * decoder.Format.BytesPerFrame, pcm.Length);
    }

    [Fact]
    public void The_source_format_is_reported_separately_from_the_delivered_one()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.S16, sampleRate: 48000, channels: 2);

        Assert.Equal(HiResRate, decoder.Format.SourceSampleRate);
        Assert.Equal(24, decoder.Format.SourceBitDepth);
        Assert.Equal(48000, decoder.Format.SampleRate);
        Assert.Equal(SampleFormat.S16, decoder.Format.SampleFormat);
        Assert.InRange(decoder.Format.Duration!.Value, TimeSpan.FromMilliseconds(245), TimeSpan.FromMilliseconds(255));
    }

    [Fact]
    public void Resampling_to_the_session_rate_halves_a_96kHz_source()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.S16, sampleRate: 48000);
        var pcm = DecodeAll(decoder);

        // Allow for swresample filter delay and its flushed tail.
        Assert.InRange(pcm.Length / 4, Frames / 2 - 64, Frames / 2 + 64);
    }

    [Fact]
    public void A_stream_decodes_to_the_same_bytes_as_a_path()
    {
        var path = HiResFixture();
        using var fromPath = Decoder.OpenPath(path, SampleFormat.S24);
        var expected = DecodeAll(fromPath);

        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var fromStream = Decoder.OpenStream(source, SampleFormat.S24);

        Assert.Equal(expected, DecodeAll(fromStream));
    }

    // Forward-only streams must decode without attempted seeks.
    [Fact]
    public void A_forward_only_stream_still_decodes()
    {
        var path = HiResFixture();
        using var fromPath = Decoder.OpenPath(path, SampleFormat.S24);
        var expected = DecodeAll(fromPath);

        using var source = new ForwardOnlyStream(File.ReadAllBytes(path));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24);

        Assert.Equal(expected, DecodeAll(decoder));
    }

    [Fact]
    public void A_forward_only_stream_refuses_to_seek()
    {
        using var source = new ForwardOnlyStream(File.ReadAllBytes(HiResFixture()));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24);

        Assert.Throws<DecodeException>(() => decoder.Seek(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Seeking_lands_at_or_before_the_request_and_says_where()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.S24);

        var landed = decoder.Seek(TimeSpan.FromMilliseconds(100));
        Assert.InRange(landed, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));

        // PCM has no keyframes, so this fixture lands exactly.
        var remaining = DecodeAll(decoder).Length / 6;
        Assert.InRange(remaining, Frames - (int)(0.100 * HiResRate) - 64, Frames);
    }

    [Fact]
    public void Seeking_back_to_the_start_replays_the_same_samples()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.S24);

        var first = new byte[6 * 512];
        Assert.Equal(first.Length, ReadFully(decoder, first));

        decoder.Seek(TimeSpan.Zero);

        var again = new byte[first.Length];
        Assert.Equal(again.Length, ReadFully(decoder, again));
        Assert.Equal(first, again);
    }

    [Fact]
    public void Reading_past_the_end_answers_zero_rather_than_repeating()
    {
        using var decoder = Decoder.OpenPath(HiResFixture(), SampleFormat.S24);
        DecodeAll(decoder);

        var buffer = new byte[4096];
        Assert.Equal(0, decoder.Read(buffer));
        Assert.Equal(0, decoder.Read(buffer));
    }

    [Fact]
    public void A_source_that_is_not_audio_fails_to_open()
    {
        var path = Path.Combine(_directory, "not-audio.wav");
        File.WriteAllText(path, "this is not a wav file, whatever its name says");

        var exception = Assert.Throws<DecodeException>(() => Decoder.OpenPath(path, SampleFormat.S16));

        // FFmpeg's own AVERROR, so a caller branches on documented numbers.
        Assert.True(exception.Code < 0, $"error code was {exception.Code}");
    }

    [Fact]
    public void A_missing_file_fails_to_open_with_the_reason()
    {
        var exception = Assert.Throws<DecodeException>(
            () => Decoder.OpenPath(Path.Combine(_directory, "absent.wav"), SampleFormat.S16));

        // Preserve FFmpeg's specific diagnosis.
        Assert.Contains("No such file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(exception.Code < 0, $"error code was {exception.Code}");
    }

    // A mid-stream failure must surface after already-decoded audio, without spinning.
    [Fact]
    public void A_stream_that_fails_mid_track_faults_rather_than_ending_quietly()
    {
        const int failureFrames = Frames * 40;
        var path = SyntheticHiResWav.CreateFile(
            _directory, "failing.wav", HiResRate, failureFrames, SyntheticHiResWav.Ramp24());
        var bytes = File.ReadAllBytes(path);
        var source = new FailingStream(bytes);
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24);
        source.FailAfterMoreBytes(bytes.Length / 10);

        var produced = 0;
        var buffer = new byte[16384];
        var thrown = Assert.Throws<DecodeException>(() =>
        {
            int read;
            while ((read = decoder.Read(buffer)) > 0)
                produced += read;
        });

        // Depending on how much a demuxer buffered, it can report either the
        // callback's I/O error or the truncation that failure caused.
        Assert.True(thrown.Code < 0, $"error code was {thrown.Code}");
        Assert.InRange(produced, 1, failureFrames * 6 - 1);
        Assert.InRange(source.Reads, 1, 200);
    }

    [Fact]
    public void A_right_format_hint_opens_the_stream_as_named()
    {
        var path = HiResFixture();
        using var fromPath = Decoder.OpenPath(path, SampleFormat.S24);
        var expected = DecodeAll(fromPath);

        var logger = new RecordingLogger();
        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24, formatHint: "wav", logger: logger);

        Assert.Equal("wav", decoder.Format.Container);
        Assert.Empty(logger.Entries);
        Assert.Equal(expected, DecodeAll(decoder));
    }

    // A wrong hint should fall back to probing and log the metadata error.
    [Fact]
    public void A_wrong_format_hint_falls_back_to_probing_and_says_so()
    {
        var path = HiResFixture();
        using var fromPath = Decoder.OpenPath(path, SampleFormat.S24);
        var expected = DecodeAll(fromPath);

        var logger = new RecordingLogger();
        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24, formatHint: "flac", logger: logger);

        Assert.Equal("wav", decoder.Format.Container);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("flac", warning.Message);
        Assert.Equal(expected, DecodeAll(decoder));
    }

    [Fact]
    public void A_wrong_format_hint_falls_back_with_no_logger_to_tell()
    {
        var path = HiResFixture();
        using var fromPath = Decoder.OpenPath(path, SampleFormat.S24);
        var expected = DecodeAll(fromPath);

        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24, formatHint: "flac");

        Assert.Equal("wav", decoder.Format.Container);
        Assert.Equal(expected, DecodeAll(decoder));
    }

    [Fact]
    public void A_stream_the_decoder_owns_is_closed_with_it_and_a_borrowed_one_is_not()
    {
        var bytes = File.ReadAllBytes(HiResFixture());

        var owned = new MemoryStream(bytes);
        Decoder.OpenStream(owned, SampleFormat.S16, ownsStream: true).Dispose();
        Assert.False(owned.CanRead);

        using var borrowed = new MemoryStream(bytes);
        Decoder.OpenStream(borrowed, SampleFormat.S16).Dispose();
        Assert.True(borrowed.CanRead);
    }

    // Failed opens must dispose owned streams because no Decoder is returned.
    [Fact]
    public void A_failed_open_closes_a_stream_the_decoder_owns()
    {
        var owned = new MemoryStream("this is not audio, whatever it claims"u8.ToArray());

        Assert.Throws<DecodeException>(() => Decoder.OpenStream(owned, SampleFormat.S16, ownsStream: true));
        Assert.False(owned.CanRead);
    }

    [Fact]
    public void The_decoder_says_it_is_available() =>
        Assert.True(Decoder.IsAvailable);

    [Fact]
    public void The_native_library_matches_the_abi_this_build_expects() =>
        // Detect managed/native format-struct layout mismatches.
        Decoder.OpenPath(HiResFixture(), SampleFormat.S16).Dispose();

    private static int ReadFully(Decoder decoder, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = decoder.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(buffer.Length, bytes.Length - _position);
            if (take <= 0)
                return 0;
            bytes.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingStream(byte[] bytes) : Stream
    {
        private int _position;
        private int _failAfter = int.MaxValue;

        public int Reads { get; private set; }

        public void FailAfterMoreBytes(int byteCount) =>
            _failAfter = Math.Min(bytes.Length, _position + byteCount);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => _position = (int)value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            if (_position >= _failAfter)
                throw new IOException("the connection went away");

            // Stop exactly at the failure boundary so a large native read
            // cannot consume bytes that the simulated source never served.
            var take = Math.Min(buffer.Length, Math.Min(bytes.Length, _failAfter) - _position);
            if (take <= 0)
                return 0;
            bytes.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = (int)(origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => bytes.Length + offset,
            });
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
