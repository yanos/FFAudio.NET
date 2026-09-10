using System;
using System.IO;
using System.Collections.Generic;

using FFAudio.Checks;


using Xunit;

namespace FFAudio.Tests;

// Exercises the façade end to end, against real FFmpeg libraries. Tagged
// because they need a native component that is built rather than restored
// (native/build-all.sh), so a machine without it filters them out instead of
// failing them.
//
// The first two tests are the whole argument for this library existing. Every
// other one is here so that argument keeps holding.
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

    // The claim the whole façade rests on: a 24-bit source arrives with all
    // 24 bits. Measured against LibVLC's amem seam on 2026-09-03, the same
    // file comes back as 16-bit int regardless of the format requested.
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

    // The contrast, so the test above is not just asserting that arithmetic
    // works. Asking for S16 loses the low byte of every sample - which is
    // exactly and only what LibVLC's amem seam can give, for every track, on
    // every platform, whatever format is asked of it.
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

        // Rounding, not truncation, so a handful of samples land one step off
        // the naive shift; the point is that the low byte is gone either way.
        Assert.True(differing < Frames / 100, $"{differing} of {Frames} frames did not match a 16-bit truncation");
    }

    // The four formats below are in the ABI and in SampleFormat, and until
    // these tests two of them were called for by nothing at all - no caller, no
    // test, no check anywhere. Plausible and unexercised is the state every
    // audio defect worth the name starts in, and the first consumer to ask for
    // S32 or F32 would have been the one to find out. These make "the façade
    // delivers four formats" a fact rather than a declaration.

    // The premise pack_s24 rests on, asserted rather than inferred from the
    // packing appearing to work. FFmpeg has no 24-bit sample format at all, so
    // a 24-bit source is decoded into S32 left-aligned - the three high bytes
    // are the whole sample and the low byte is padding. Were that ever not
    // true, S24 would go quietly wrong rather than fail.
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

    // And so the packing is a bandwidth decision rather than a lossy one: the
    // same source asked for both ways carries the same bits, and S24 is S32
    // with the padding dropped. This is the sentence pack_s24's comment makes
    // and nothing checked.
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

    // A float significand holds 24 bits exactly, which is both why
    // PcmSampleFormat stops at S24 and why F32 is worth exposing to someone
    // whose own pipeline is float: a 24-bit source survives it with no
    // rounding whatsoever. Exact equality rather than a tolerance, and that is
    // the claim - v/2^23 is a division by a power of two on a value that fits
    // the significand, so every step of it is exact.
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

    // BytesPerFrame is what every caller sizes its buffers and its ring by, so
    // a format whose advertised width disagreed with what ffaudio_decoder_read
    // actually writes would surface as a buffer bug in the caller rather than
    // as a failure here. Four formats, one piece of arithmetic, no resampling
    // asked for - so the frame count is exact rather than swresample's tail
    // either side of it.
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

        // Not exactly half: swresample's filter has a delay, and the tail it
        // holds is flushed rather than dropped, so the count lands within a
        // few frames either side.
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

    // The case that broke a whole AAC album on the phone: a stream the
    // platform would not let the demuxer seek. FFmpeg is told so up front and
    // reads it forwards instead of discarding the demuxer.
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

        // PCM has no keyframes, so this particular container lands exactly;
        // the contract the caller has to honour is the range above.
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

        Assert.Throws<DecodeException>(() => Decoder.OpenPath(path, SampleFormat.S16));
    }

    [Fact]
    public void A_missing_file_fails_to_open_with_the_reason()
    {
        var exception = Assert.Throws<DecodeException>(
            () => Decoder.OpenPath(Path.Combine(_directory, "absent.wav"), SampleFormat.S16));

        // FFmpeg's own diagnosis, not a flattened "could not open" - the
        // reason ffaudio_error_string passes AVERROR codes through untouched.
        Assert.Contains("No such file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // A stream that dies mid-track must fault, not spin and not end quietly.
    // Both halves matter. LibVLC's equivalent hot-looped 61,760 times on the
    // same signal and then had its demuxer fabricate the rest of the track,
    // so a cut-off song was indistinguishable from one the listener heard;
    // here the failure surfaces as an exception after a bounded number of
    // reads, with the audio that did arrive already handed over.
    [Fact]
    public void A_stream_that_fails_mid_track_faults_rather_than_ending_quietly()
    {
        var bytes = File.ReadAllBytes(HiResFixture());
        var source = new FailingStream(bytes, failAfter: bytes.Length / 3);
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24);

        var produced = 0;
        var buffer = new byte[16384];
        var thrown = Assert.Throws<DecodeException>(() =>
        {
            int read;
            while ((read = decoder.Read(buffer)) > 0)
                produced += read;
        });

        // The wording is the C runtime's strerror for EIO, which FFmpeg passes
        // through untouched, and the runtimes disagree: glibc and Apple libc
        // say "Input/output error", the MSVC one says "I/O error". Pinning
        // either spelling on its own asserts about the C library rather than
        // about the decoder faulting, which is what this test is for.
        Assert.Contains(OperatingSystem.IsWindows() ? "I/O error" : "Input/output error", thrown.Message);
        Assert.InRange(produced, 1, Frames * 6 - 1);
        Assert.InRange(source.Reads, 1, 200);
    }

    [Fact]
    public void The_native_library_matches_the_abi_this_build_expects() =>
        // Not a tautology: it is the check that would have caught a façade
        // rebuilt with a reordered format struct, which otherwise reports
        // plausible nonsense rather than failing.
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

    private sealed class FailingStream(byte[] bytes, int failAfter) : Stream
    {
        private int _position;

        public int Reads { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => _position = (int)value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            if (_position >= failAfter)
                throw new IOException("the connection went away");

            var take = Math.Min(buffer.Length, bytes.Length - _position);
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
