using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

namespace FFAudio.Checks;

// Cross-platform runtime checks for native loading, AOT callbacks, decoding,
// seeking, and metadata. These run inside mobile apps without an xUnit host.
public static class DecodeChecks
{
    private const int Rate = 96000;
    private const int Frames = 24000; // a quarter second at 96kHz
    private const int Channels = 2;

    public static IReadOnlyList<CheckResult> RunAll()
    {
        var results = new List<CheckResult>();

        // Isolate concurrent target-framework runs.
        var directory = Path.Combine(Path.GetTempPath(), "ffaudio-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = SyntheticHiResWav.CreateFile(
                directory, "hires.wav", Rate, Frames, SyntheticHiResWav.Ramp24());

            // Keep output stable and comparable across runners.
            results.Add(Run("the native library matches the expected ABI", () => NativeLibraryMatchesAbi(path)));
            results.Add(Run("the decoder says it is available", DecoderIsAvailable));
            results.Add(Run("a 24-bit source arrives with every bit", () => EveryBitSurvives(path)));
            results.Add(Run("a 24-bit source delivered as 16-bit loses low bits", () => S16LosesLowBits(path)));
            results.Add(Run("a 24-bit source delivered as 32-bit has an empty low byte", () => S32HasEmptyLowByte(path)));
            results.Add(Run("24-bit output is packed 32-bit output", () => S24IsPackedS32(path)));
            results.Add(Run("float output preserves a 24-bit source exactly", () => F32IsExact(path)));
            results.Add(Run("16-bit output reports its width", () => FormatHasRightWidth(path, SampleFormat.S16, 2)));
            results.Add(Run("24-bit output reports its width", () => FormatHasRightWidth(path, SampleFormat.S24, 3)));
            results.Add(Run("32-bit output reports its width", () => FormatHasRightWidth(path, SampleFormat.S32, 4)));
            results.Add(Run("float output reports its width", () => FormatHasRightWidth(path, SampleFormat.F32, 4)));
            results.Add(Run("source and delivered formats are reported separately", () => SourceFormatIsReported(path)));
            results.Add(Run("resampling delivers the expected frame count", () => ResamplingIsRight(path)));
            results.Add(Run("a managed stream decodes what the path decodes", () => StreamMatchesPath(path)));
            results.Add(Run("an unseekable stream still decodes", () => UnseekableStreamDecodes(path)));
            results.Add(Run("an unseekable stream refuses to seek", () => ForwardOnlyStreamRefusesSeek(path)));
            results.Add(Run("seeking lands at or before the request", () => SeekLandsWhereItSays(path)));
            results.Add(Run("seeking to the start replays the same samples", () => SeekingBackReplaysSamples(path)));
            results.Add(Run("reading past the end returns zero", () => ReadingPastEndReturnsZero(path)));
            results.Add(Run("a non-audio file fails to open", () => NonAudioFailsToOpen(directory)));
            results.Add(Run("a missing file fails with its reason", () => MissingFileFailsWithReason(directory)));
            results.Add(Run("a stream failure faults instead of ending quietly", () => FailingStreamFaults(path)));
            results.Add(Run("a right format hint opens the stream as named", () => RightHintOpens(path)));
            results.Add(Run("a wrong format hint falls back to probing and says so", () => WrongHintFallsBack(path)));
            results.Add(Run("a wrong format hint falls back with no logger to tell", () => WrongHintWithoutLogger(path)));
            results.Add(Run("a stream the decoder owns is closed with it", () => OwnedStreamIsClosed(path)));
            results.Add(Run("a failed open closes a stream the decoder owns", () => FailedOpenClosesOwnedStream(directory)));

            var tagged = SyntheticTaggedAiff.CreateFile(directory, "tagged.aiff", 44100, 4410);

            results.Add(Run("a file says what it was tagged with", () => TagsComeBack(tagged)));
            results.Add(Run("cover art comes back byte for byte", () => CoverArtComesBack(tagged)));
            results.Add(Run("an untagged file has no tags", () => NoTagsAreReported(path)));
            results.Add(Run("a file without cover art says so", () => NoCoverArtIsReported(path)));
            results.Add(Run("a stereo file reports a stereo layout", () => StereoLayoutIsReported(tagged)));
            results.Add(Run("a downmix reports the layout it produces", () => DownmixLayoutIsReported(tagged)));
            results.Add(Run("a file names its codec and its container", () => NamesAreRight(tagged, path)));
            results.Add(Run("a stream carries metadata", () => StreamCarriesMetadata(tagged)));
            results.Add(Run("reading metadata does not disturb decoding", () => MetadataDoesNotDisturbDecode(tagged)));

            results.Add(Run("the binary reports its FFmpeg version and license", FfmpegIdentityIsReported));
            results.Add(Run("the FFmpeg configuration is returned whole", FfmpegConfigurationIsWhole));
            results.Add(Run("the redistribution flag matches the FFmpeg license", RedistributionFlagMatchesLicense));
            results.Add(Run("the mobile FFmpeg build is LGPL-only", MobileBuildIsRedistributable));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Cleanup failure does not invalidate completed checks.
            }
        }

        return results;
    }

    // The ramp changes its low byte every frame to expose 16-bit truncation.
    private static string EveryBitSurvives(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S24);
        var pcm = DecodeAll(decoder);

        Expect(decoder.Format.SourceBitDepth == 24, $"source bit depth {decoder.Format.SourceBitDepth}, wanted 24");
        Expect(decoder.Format.SampleRate == Rate, $"sample rate {decoder.Format.SampleRate}, wanted {Rate}");
        Expect(pcm.Length == Frames * 6, $"{pcm.Length} bytes, wanted {Frames * 6}");

        var expected = SyntheticHiResWav.Ramp24();
        for (var frame = 0; frame < Frames; frame++)
        {
            for (var channel = 0; channel < Channels; channel++)
            {
                var offset = frame * 6 + channel * 3;
                var actual = SyntheticHiResWav.ReadInt24(pcm.AsSpan(offset, 3));
                Expect(actual == expected(frame),
                    $"frame {frame} channel {channel}: {actual}, wanted {expected(frame)}");
            }
        }

        return $"{Frames} frames, all 24 bits";
    }

    private static string NativeLibraryMatchesAbi(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S16);
        return $"ABI {decoder.Format.SampleFormat}";
    }

    // Availability should report a compatible native without throwing.
    private static string DecoderIsAvailable()
    {
        Expect(Decoder.IsAvailable, "Decoder.IsAvailable is false with a working native present");
        return "available";
    }

    private static string S16LosesLowBits(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S16);
        var pcm = DecodeAll(decoder);

        Expect(pcm.Length == Frames * 4, $"{pcm.Length} bytes, wanted {Frames * 4}");

        var expected = SyntheticHiResWav.Ramp24();
        var differing = 0;
        for (var frame = 0; frame < Frames; frame++)
        {
            var delivered = BitConverter.ToInt16(pcm, frame * 4);
            if (delivered != (short)(expected(frame) >> 8))
                differing++;
        }

        Expect(differing < Frames / 100, $"{differing} of {Frames} frames did not match 16-bit truncation");
        return $"{differing} rounded frames";
    }

    private static string S32HasEmptyLowByte(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S32);
        var pcm = DecodeAll(decoder);

        Expect(pcm.Length == Frames * 8, $"{pcm.Length} bytes, wanted {Frames * 8}");
        for (var offset = 0; offset < pcm.Length; offset += 4)
            Expect(pcm[offset] == 0, $"sample {offset / 4} has low byte {pcm[offset]}");

        return $"{pcm.Length / 4} samples";
    }

    private static string S24IsPackedS32(string path)
    {
        using var packed = Decoder.OpenPath(path, SampleFormat.S24);
        var s24 = DecodeAll(packed);
        using var wide = Decoder.OpenPath(path, SampleFormat.S32);
        var s32 = DecodeAll(wide);

        Expect(s24.Length / 3 == s32.Length / 4, $"{s24.Length} S24 bytes and {s32.Length} S32 bytes disagree");
        for (int source = 0, destination = 0; source < s32.Length; source += 4, destination += 3)
        {
            Expect(s24[destination] == s32[source + 1], $"sample {source / 4} byte 0 differs");
            Expect(s24[destination + 1] == s32[source + 2], $"sample {source / 4} byte 1 differs");
            Expect(s24[destination + 2] == s32[source + 3], $"sample {source / 4} byte 2 differs");
        }

        return $"{s24.Length / 3} samples";
    }

    private static string F32IsExact(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.F32);
        var pcm = DecodeAll(decoder);

        Expect(pcm.Length == Frames * 8, $"{pcm.Length} bytes, wanted {Frames * 8}");
        var expected = SyntheticHiResWav.Ramp24();
        for (var frame = 0; frame < Frames; frame++)
        {
            for (var channel = 0; channel < Channels; channel++)
            {
                var delivered = BitConverter.ToSingle(pcm, frame * 8 + channel * 4);
                Expect(delivered == expected(frame) / 8388608f,
                    $"frame {frame} channel {channel}: {delivered}, wanted {expected(frame) / 8388608f}");
            }
        }

        return $"{Frames} frames";
    }

    private static string FormatHasRightWidth(string path, SampleFormat format, int bytesPerSample)
    {
        using var decoder = Decoder.OpenPath(path, format);
        var pcm = DecodeAll(decoder);

        Expect(decoder.Format.SampleFormat == format, $"format {decoder.Format.SampleFormat}, wanted {format}");
        Expect(decoder.Format.Channels == Channels, $"{decoder.Format.Channels} channels, wanted {Channels}");
        Expect(decoder.Format.BytesPerFrame == bytesPerSample * Channels,
            $"{decoder.Format.BytesPerFrame} bytes per frame, wanted {bytesPerSample * Channels}");
        Expect(pcm.Length == Frames * decoder.Format.BytesPerFrame,
            $"{pcm.Length} bytes, wanted {Frames * decoder.Format.BytesPerFrame}");

        return $"{decoder.Format.BytesPerFrame} bytes per frame";
    }

    private static string SourceFormatIsReported(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S16, sampleRate: 48000, channels: 2);

        Expect(decoder.Format.SourceSampleRate == Rate, $"source rate {decoder.Format.SourceSampleRate}, wanted {Rate}");
        Expect(decoder.Format.SourceBitDepth == 24, $"source depth {decoder.Format.SourceBitDepth}, wanted 24");
        Expect(decoder.Format.SampleRate == 48000, $"sample rate {decoder.Format.SampleRate}, wanted 48000");
        Expect(decoder.Format.SampleFormat == SampleFormat.S16, $"format {decoder.Format.SampleFormat}, wanted S16");
        Expect(decoder.Format.Duration is { } duration
               && duration >= TimeSpan.FromMilliseconds(245)
               && duration <= TimeSpan.FromMilliseconds(255),
            $"duration {decoder.Format.Duration}, wanted about 250ms");

        return $"{decoder.Format.SourceSampleRate}Hz source, {decoder.Format.SampleRate}Hz output";
    }

    // Exercise managed callbacks under both JIT and full AOT.
    private static string StreamMatchesPath(string path)
    {
        byte[] fromPath;
        using (var decoder = Decoder.OpenPath(path, SampleFormat.S24))
            fromPath = DecodeAll(decoder);

        byte[] fromStream;
        using (var source = new MemoryStream(File.ReadAllBytes(path)))
        using (var decoder = Decoder.OpenStream(source, SampleFormat.S24))
            fromStream = DecodeAll(decoder);

        Expect(fromStream.Length == fromPath.Length,
            $"{fromStream.Length} bytes from the stream, {fromPath.Length} from the path");

        for (var i = 0; i < fromPath.Length; i++)
        {
            Expect(fromStream[i] == fromPath[i], $"byte {i} differs: {fromStream[i]} vs {fromPath[i]}");
        }

        return $"{fromPath.Length} bytes, identical";
    }

    // Model a live body with unknown length and no seeking.
    private static string UnseekableStreamDecodes(string path)
    {
        byte[] expected;
        using (var fromPath = Decoder.OpenPath(path, SampleFormat.S24))
            expected = DecodeAll(fromPath);

        using var forward = new ForwardOnlyStream(new MemoryStream(File.ReadAllBytes(path)));
        using var decoder = Decoder.OpenStream(forward, SampleFormat.S24);

        var pcm = DecodeAll(decoder);
        Expect(pcm.Length == expected.Length, $"{pcm.Length} bytes, wanted {expected.Length}");
        for (var i = 0; i < pcm.Length; i++)
            Expect(pcm[i] == expected[i], $"byte {i} differs: {pcm[i]} vs {expected[i]}");

        return $"{pcm.Length} bytes, identical";
    }

    // Seek reports the demuxer's actual landing position.
    private static string SeekLandsWhereItSays(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S24);

        var asked = TimeSpan.FromMilliseconds(100);
        var landed = decoder.Seek(asked);

        Expect(landed <= asked, $"asked {asked}, landed {landed} - after the request");
        Expect(landed >= TimeSpan.Zero, $"landed {landed}, before the start");

        var remaining = DecodeAll(decoder).Length / 6;
        Expect(remaining >= Frames - (int)(0.100 * Rate) - 64 && remaining <= Frames,
            $"{remaining} frames after seeking, wanted up to {Frames}");

        return $"asked {asked.TotalMilliseconds:F0}ms, landed {landed.TotalMilliseconds:F0}ms, {remaining} frames remain";
    }

    // Exercise the separately linked swresample path.
    private static string ResamplingIsRight(string path)
    {
        const int target = 48000;

        using var decoder = Decoder.OpenPath(path, SampleFormat.S16, target);
        Expect(decoder.Format.SampleRate == target,
            $"sample rate {decoder.Format.SampleRate}, wanted {target}");
        Expect(decoder.Format.SourceSampleRate == Rate,
            $"source sample rate {decoder.Format.SourceSampleRate}, wanted {Rate}");

        var pcm = DecodeAll(decoder);
        var frames = pcm.Length / decoder.Format.BytesPerFrame;

        // Allow for implementation-specific resampler delay.
        var wanted = Frames * target / Rate;
        Expect(Math.Abs(frames - wanted) <= 64, $"{frames} frames, wanted about {wanted}");

        return $"{Rate}Hz to {target}Hz, {frames} frames";
    }

    private static string ForwardOnlyStreamRefusesSeek(string path)
    {
        using var source = new ForwardOnlyStream(new MemoryStream(File.ReadAllBytes(path)));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24);

        try
        {
            _ = decoder.Seek(TimeSpan.FromMilliseconds(100));
        }
        catch (DecodeException)
        {
            return "DecodeException";
        }

        throw new CheckFailedException("seeking an unseekable stream did not throw DecodeException");
    }

    private static string SeekingBackReplaysSamples(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S24);
        var first = new byte[6 * 512];
        Expect(ReadFully(decoder, first) == first.Length, "the first read did not fill the buffer");

        _ = decoder.Seek(TimeSpan.Zero);

        var again = new byte[first.Length];
        Expect(ReadFully(decoder, again) == again.Length, "the replay did not fill the buffer");
        for (var i = 0; i < first.Length; i++)
            Expect(first[i] == again[i], $"byte {i} changed after seeking to the start");

        return $"{first.Length} bytes replayed";
    }

    private static string ReadingPastEndReturnsZero(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S24);
        _ = DecodeAll(decoder);

        var buffer = new byte[4096];
        Expect(decoder.Read(buffer) == 0, "the first read after the end was not zero");
        Expect(decoder.Read(buffer) == 0, "the second read after the end was not zero");
        return "zero twice";
    }

    private static string NonAudioFailsToOpen(string directory)
    {
        var path = Path.Combine(directory, "not-audio.wav");
        File.WriteAllText(path, "this is not a wav file, whatever its name says");

        try
        {
            using var decoder = Decoder.OpenPath(path, SampleFormat.S16);
        }
        catch (DecodeException exception)
        {
            // Preserve FFmpeg's documented AVERROR value.
            Expect(exception.Code < 0, $"error code was {exception.Code}, wanted an AVERROR");
            return $"DecodeException {exception.Code}";
        }

        throw new CheckFailedException("a non-audio file opened successfully");
    }

    private static string MissingFileFailsWithReason(string directory)
    {
        try
        {
            using var decoder = Decoder.OpenPath(Path.Combine(directory, "absent.wav"), SampleFormat.S16);
        }
        catch (DecodeException exception)
        {
            Expect(exception.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase),
                $"missing-file error was {Quote(exception.Message)}");
            Expect(exception.Code < 0, $"error code was {exception.Code}, wanted an AVERROR");
            return $"{exception.Code}: {exception.Message}";
        }

        throw new CheckFailedException("a missing file opened successfully");
    }

    private static string FailingStreamFaults(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var source = new FailingStream(bytes, bytes.Length / 3);
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24);

        var produced = 0;
        var buffer = new byte[16384];
        try
        {
            int read;
            while ((read = decoder.Read(buffer)) > 0)
                produced += read;
        }
        catch (DecodeException exception)
        {
            // strerror uses either common EIO spelling depending on the C runtime.
            Expect(exception.Message.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
                    || exception.Message.Contains("Input/output error", StringComparison.OrdinalIgnoreCase),
                $"stream failure was {Quote(exception.Message)}");
            Expect(produced > 0 && produced < Frames * 6, $"{produced} bytes before failure");
            Expect(source.Reads is >= 1 and <= 200, $"{source.Reads} reads before failure");
            return $"{produced} bytes before {source.Reads} reads";
        }

        throw new CheckFailedException("a failing stream ended without DecodeException");
    }

    // A correct demuxer hint must preserve decoded output.
    private static string RightHintOpens(string path)
    {
        byte[] expected;
        using (var fromPath = Decoder.OpenPath(path, SampleFormat.S24))
            expected = DecodeAll(fromPath);

        var logger = new RecordingLogger();
        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24, formatHint: "wav", logger: logger);

        Expect(decoder.Format.Container == "wav", $"container {Quote(decoder.Format.Container)}");
        Expect(logger.Entries.Count == 0, $"{logger.Entries.Count} log entries for a hint that was right");
        var pcm = DecodeAll(decoder);
        Expect(pcm.AsSpan().SequenceEqual(expected), "the hinted stream decoded differently from the path");
        return $"{pcm.Length} bytes, identical";
    }

    // A wrong hint should fall back to probing and produce a warning.
    private static string WrongHintFallsBack(string path)
    {
        byte[] expected;
        using (var fromPath = Decoder.OpenPath(path, SampleFormat.S24))
            expected = DecodeAll(fromPath);

        var logger = new RecordingLogger();
        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24, formatHint: "flac", logger: logger);

        Expect(decoder.Format.Container == "wav", $"container {Quote(decoder.Format.Container)}");
        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
        Expect(warnings.Count == 1, $"{warnings.Count} warnings, wanted one");
        Expect(warnings[0].Message.Contains("flac", StringComparison.Ordinal),
            $"the warning does not name the hint: {Quote(warnings[0].Message)}");
        var pcm = DecodeAll(decoder);
        Expect(pcm.AsSpan().SequenceEqual(expected), "the fallback decoded differently from the path");
        return warnings[0].Message;
    }

    // Logging is optional and must not affect hint fallback.
    private static string WrongHintWithoutLogger(string path)
    {
        byte[] expected;
        using (var fromPath = Decoder.OpenPath(path, SampleFormat.S24))
            expected = DecodeAll(fromPath);

        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24, formatHint: "flac");

        Expect(decoder.Format.Container == "wav", $"container {Quote(decoder.Format.Container)}");
        var pcm = DecodeAll(decoder);
        Expect(pcm.AsSpan().SequenceEqual(expected), "the fallback decoded differently from the path");
        return $"{pcm.Length} bytes";
    }

    // Verify both sides of the ownsStream contract.
    private static string OwnedStreamIsClosed(string path)
    {
        var bytes = File.ReadAllBytes(path);

        var owned = new MemoryStream(bytes);
        Decoder.OpenStream(owned, SampleFormat.S16, ownsStream: true).Dispose();
        Expect(!owned.CanRead, "an owned stream was still open after Dispose");

        using var borrowed = new MemoryStream(bytes);
        Decoder.OpenStream(borrowed, SampleFormat.S16).Dispose();
        Expect(borrowed.CanRead, "a borrowed stream was closed by Dispose");

        return "owned closed, borrowed open";
    }

    // Failed opens must close owned streams without a Decoder to dispose.
    private static string FailedOpenClosesOwnedStream(string directory)
    {
        var owned = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("this is not audio, whatever it claims"));
        try
        {
            using var decoder = Decoder.OpenStream(owned, SampleFormat.S16, ownsStream: true);
        }
        catch (DecodeException exception)
        {
            Expect(!owned.CanRead, "an owned stream was left open after a failed open");
            return $"DecodeException {exception.Code}, stream closed";
        }

        throw new CheckFailedException("a non-audio stream opened successfully");
    }

    // Exercise indexed tag marshalling and grow-and-retry buffers under AOT.
    private static string TagsComeBack(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S16);

        var tags = decoder.Tags;
        Expect(tags.Count > 0, "no tags at all");

        Expect(Tag(decoder, "title") == SyntheticTaggedAiff.Title,
            $"title {Quote(Tag(decoder, "title"))}, wanted {Quote(SyntheticTaggedAiff.Title)}");
        Expect(Tag(decoder, "artist") == SyntheticTaggedAiff.Artist,
            $"artist {Quote(Tag(decoder, "artist"))}, wanted {Quote(SyntheticTaggedAiff.Artist)}");
        Expect(Tag(decoder, "album") == SyntheticTaggedAiff.Album,
            $"album {Quote(Tag(decoder, "album"))}, wanted {Quote(SyntheticTaggedAiff.Album)}");

        return $"{tags.Count} tags, title {Quote(SyntheticTaggedAiff.Title)}";
    }

    // Cover art must survive variable-length buffer marshalling unchanged.
    private static string CoverArtComesBack(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S16);

        var art = decoder.TryReadCoverArt();
        Expect(art is not null, "no cover art came back");
        Expect(art!.MimeType == "image/png", $"mime type {Quote(art.MimeType)}, wanted image/png");

        var expected = SyntheticTaggedAiff.CoverPng();
        Expect(art.Bytes.Length == expected.Length,
            $"{art.Bytes.Length} bytes of art, wanted {expected.Length}");
        for (var i = 0; i < expected.Length; i++)
        {
            Expect(art.Bytes[i] == expected[i], $"art byte {i} differs: {art.Bytes[i]} vs {expected[i]}");
        }

        // Mobile audio-only builds obtain these values from the PNG header.
        Expect(art.Width == 1 && art.Height == 1,
            $"art is {art.Width}x{art.Height}, wanted 1x1");

        return $"{art.Bytes.Length} bytes of {art.MimeType}, {art.Width}x{art.Height}";
    }

    private static string NoTagsAreReported(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S24);
        Expect(decoder.Tags.Count == 0, $"{decoder.Tags.Count} tags on a file that has none");
        return "no tags";
    }

    private static string NoCoverArtIsReported(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S24);
        Expect(decoder.TryReadCoverArt() is null, "cover art on a file that has none");
        return "no art";
    }

    private static string StereoLayoutIsReported(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S16);
        Expect(decoder.Format.Channels == 2, $"{decoder.Format.Channels} channels, wanted 2");
        Expect(decoder.Format.ChannelLayout == "stereo", $"layout {Quote(decoder.Format.ChannelLayout)}, wanted stereo");
        return decoder.Format.ChannelLayout;
    }

    private static string DownmixLayoutIsReported(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S16, sampleRate: 0, channels: 1);
        Expect(decoder.Format.SourceChannels == 2, $"source channels {decoder.Format.SourceChannels}, wanted 2");
        Expect(decoder.Format.Channels == 1, $"{decoder.Format.Channels} channels, wanted 1");
        Expect(decoder.Format.ChannelLayout == "mono", $"layout {Quote(decoder.Format.ChannelLayout)}, wanted mono");
        return decoder.Format.ChannelLayout;
    }

    private static string NamesAreRight(string tagged, string plain)
    {
        using var aiff = Decoder.OpenPath(tagged, SampleFormat.S16);
        using var wav = Decoder.OpenPath(plain, SampleFormat.S24);

        Expect(aiff.Format.Codec == "pcm_s16be", $"aiff codec {Quote(aiff.Format.Codec)}");
        Expect(aiff.Format.Container == "aiff", $"aiff container {Quote(aiff.Format.Container)}");
        Expect(wav.Format.Codec == "pcm_s24le", $"wav codec {Quote(wav.Format.Codec)}");
        Expect(wav.Format.Container == "wav", $"wav container {Quote(wav.Format.Container)}");

        return $"{aiff.Format.Container}/{aiff.Format.Codec} and {wav.Format.Container}/{wav.Format.Codec}";
    }

    private static string StreamCarriesMetadata(string path)
    {
        using var file = File.OpenRead(path);
        using var decoder = Decoder.OpenStream(file, SampleFormat.S16);
        Expect(Tag(decoder, "title") == SyntheticTaggedAiff.Title,
            $"title {Quote(Tag(decoder, "title"))}, wanted {Quote(SyntheticTaggedAiff.Title)}");
        Expect(decoder.TryReadCoverArt() is not null, "no cover art came back");
        return "tags and art";
    }

    private static string MetadataDoesNotDisturbDecode(string path)
    {
        byte[] expected;
        using (var straight = Decoder.OpenPath(path, SampleFormat.S16))
            expected = DecodeAll(straight);

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

        var actual = rest.ToArray();
        Expect(actual.Length == expected.Length, $"{actual.Length} bytes, wanted {expected.Length}");
        for (var i = 0; i < actual.Length; i++)
            Expect(actual[i] == expected[i], $"byte {i} differs after reading metadata");

        return $"{actual.Length} bytes";
    }

    private static string FfmpegIdentityIsReported()
    {
        Expect(!string.IsNullOrWhiteSpace(FFmpegBuild.Version), "the build reports no FFmpeg version");
        Expect(!string.IsNullOrWhiteSpace(FFmpegBuild.License), "the build reports no FFmpeg license");
        return $"{FFmpegBuild.Version}, {FFmpegBuild.License}";
    }

    private static string FfmpegConfigurationIsWhole()
    {
        var configuration = FFmpegBuild.Configuration;
        Expect(configuration.StartsWith("--", StringComparison.Ordinal),
            $"configuration {Quote(configuration)} does not start with --");
        Expect(configuration == FFmpegBuild.Configuration, "the configuration changed between reads");
        Expect(configuration.Length > 256,
            $"a {configuration.Length}-character configuration fits the initial buffer");
        return $"{configuration.Length} characters";
    }

    private static string RedistributionFlagMatchesLicense()
    {
        var isLgpl = FFmpegBuild.License.StartsWith("LGPL", StringComparison.Ordinal);
        Expect(FFmpegBuild.IsRedistributable == isLgpl,
            $"license {Quote(FFmpegBuild.License)} and IsRedistributable disagree");
        return isLgpl ? "LGPL" : "not LGPL";
    }

    private static string MobileBuildIsRedistributable()
    {
        var required = OperatingSystem.IsIOS()
            || OperatingSystem.IsAndroid()
            || Environment.GetEnvironmentVariable("FFAUDIO_REQUIRE_LGPL") is { Length: > 0 };
        if (!required)
            return "not required for this development build";

        Expect(FFmpegBuild.IsRedistributable,
            $"FFmpeg is under {Quote(FFmpegBuild.License)}; configuration: {FFmpegBuild.Configuration}");
        Expect(!FFmpegBuild.Configuration.Contains("--enable-gpl", StringComparison.Ordinal),
            "FFmpeg configuration enables GPL");
        Expect(!FFmpegBuild.Configuration.Contains("--enable-nonfree", StringComparison.Ordinal),
            "FFmpeg configuration enables nonfree components");
        return "LGPL-only";
    }

    private static string? Tag(Decoder decoder, string key)
    {
        foreach (var tag in decoder.Tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase))
                return tag.Value;
        }

        return null;
    }

    private static string Quote(string? value) => value is null ? "(absent)" : $"\"{value}\"";

    private static byte[] DecodeAll(Decoder decoder)
    {
        var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }

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

    private static void Expect(bool held, string complaint)
    {
        if (!held)
            throw new CheckFailedException(complaint);
    }

    private static CheckResult Run(string name, Func<string> check)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var detail = check();
            return new CheckResult(name, Passed: true, detail, clock.Elapsed);
        }
        catch (Exception failed)
        {
            // Report loading and marshalling exceptions as individual failures.
            return new CheckResult(name, Passed: false, failed.ToString(), clock.Elapsed);
        }
    }

    private static CheckResult Run(string name, Action check) =>
        Run(name, () => { check(); return string.Empty; });

    // Wrap a seekable source as a forward-only stream with unknown length.
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
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
