using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

namespace FFAudio.Checks;

// Does this platform actually turn a file into the right samples?
//
// FFAudio.Tests answers that on a desktop, thoroughly, with xUnit. This
// answers a smaller version of it anywhere - including inside an app on a
// phone, where there is no test host to run xUnit in and where the parts most
// likely to be broken are the parts a desktop never exercises:
//
//  - Native.Resolve's iOS branch, which loads the façade by hand out of
//    Frameworks/ffaudio.framework because .NET-for-iOS resolves a P/Invoke by
//    dlopen-ing the DllImport string and that matches nothing there. It is
//    registered from a [ModuleInitializer] rather than a static constructor
//    because Mono resolves the library for a P/Invoke stub *before* running
//    the declaring type's cctor - so the resolver was once registered by the
//    very call that had already failed. Nothing but a launch finds that.
//  - the read and seek trampolines OpenStream hands to FFmpeg, under full
//    AOT rather than the JIT every desktop run uses.
//  - libffaudio.so loading out of an APK, for whichever ABI this hardware is.
//
// A cross-compile catches none of the three: they are all green at link time
// and all fatal at launch.
//
// It answers the metadata half too - tags, cover art, layout, codec names -
// because a phone is where a caller most wants them and where the least of
// the library has ever been run. A build that decodes perfectly and returns
// no title is broken for anything that draws a track list.
public static class DecodeChecks
{
    private const int Rate = 96000;
    private const int Frames = 24000; // a quarter second at 96kHz
    private const int Channels = 2;

    public static IReadOnlyList<CheckResult> RunAll()
    {
        var results = new List<CheckResult>();

        var directory = Path.Combine(Path.GetTempPath(), "ffaudio-checks");
        Directory.CreateDirectory(directory);

        try
        {
            var path = SyntheticHiResWav.CreateFile(
                directory, "hires.wav", Rate, Frames, SyntheticHiResWav.Ramp24());

            // Keep this list in the same order as the desktop suite. The
            // mobile heads cannot run xUnit, but they must exercise every
            // behaviour a desktop build does rather than a smaller proxy.
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
                // A phone's temp directory surviving a run is not a finding.
            }
        }

        return results;
    }

    // The claim the whole library rests on. The fixture is a 24-bit ramp whose
    // low byte changes every frame, so a decoder that narrowed to 16 bits
    // anywhere is wrong at almost every frame rather than at a few - the loss
    // is only invisible when the fixture had nothing down there to lose.
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

    // The question a caller asks before offering playback at all, and the one
    // place a missing or mismatched native is meant to be a false rather than
    // an exception. On a platform where everything else here passes it has to
    // say true - a false here would have an app hide a player that works.
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

    // OpenStream hands FFmpeg two function pointers into managed code. Under
    // the JIT that is unremarkable; under full AOT the trampolines are
    // compiled ahead of time and a mistake there is a crash at the first
    // callback, not a compile error. Comparing against the path decode means
    // a stream that opens but reads the wrong bytes is caught too.
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

    // The seek callback's other answer. A stream that cannot seek reports its
    // size as unknown and refuses every seek, which is what a live HTTP body
    // looks like - and it is the path where returning the wrong thing from a
    // trampoline hangs rather than throws.
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

    // Seek returns where decode actually resumed, which is at or before the
    // request because the demuxer is keyframe-bound. A caller that reports the
    // request instead ends up with a scrubber permanently offset from the
    // audio.
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

    // Asking for a rate the source is not. swresample is a separate FFmpeg
    // library from the demuxer and the decoder, so it is a separate thing for
    // a cross-compiled build to have got wrong.
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

        // Halving the rate halves the frames, give or take the resampler's
        // own filter delay. A tolerance rather than an equality because that
        // delay is an implementation detail; an order of magnitude out is not.
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
            // FFmpeg's own AVERROR, negative, rather than a code this library
            // made up: a caller who wants to branch on why an open failed
            // compares against the same numbers FFmpeg documents.
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
            // Both spellings of the same errno. The text is libc's rather than
            // FFmpeg's - av_strerror hands EIO straight to strerror - so it is
            // the platform's C library that decides the wording, not the
            // platform's family: glibc says "Input/output error", while
            // Bionic and the Windows CRT both say "I/O error". Keying this on
            // IsWindows was a guess that held until the first Android run.
            //
            // Still asserted rather than dropped: the claim worth keeping is
            // that a mid-stream failure arrives as a DecodeException a person
            // can read, and an empty or generic message would fail that. What
            // is not worth asserting is which libc the phone shipped.
            Expect(exception.Message.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
                    || exception.Message.Contains("Input/output error", StringComparison.OrdinalIgnoreCase),
                $"stream failure was {Quote(exception.Message)}");
            Expect(produced > 0 && produced < Frames * 6, $"{produced} bytes before failure");
            Expect(source.Reads is >= 1 and <= 200, $"{source.Reads} reads before failure");
            return $"{produced} bytes before {source.Reads} reads";
        }

        throw new CheckFailedException("a failing stream ended without DecodeException");
    }

    // A named demuxer, used rather than probed for. The hint's value is a
    // stream that starts somewhere a probe would misjudge; the check is only
    // that naming the right one changes nothing about what comes out.
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

    // A catalog that says FLAC about a WAV. The forced open fails, the stream
    // is rewound and probed, and the track plays - but the mislabel is still a
    // fact about the caller's data, and the warning is the only place it
    // surfaces. Both halves are the contract: it opens, and it says so.
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

    // ownsStream is who closes the Stream, and both answers are promises: a
    // decoder that owns it closes it on Dispose, one that does not leaves it
    // open for a caller that still wants it - to retry, to rewind, to hand to
    // the next track.
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

    // The half of ownership nobody tests: an open that throws never returns a
    // decoder to Dispose, so if it does not close an owned stream itself,
    // nothing ever will.
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

    // Tags are read off the format context rather than the packet stream, so
    // this is a question about the file and not a decode - but the marshalling
    // beneath it is a two-call dance (count, then each by index) with a
    // grow-and-retry buffer, and that is AOT-sensitive in a way a struct read
    // is not.
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

    // The bytes, unaltered. Cover art is the one call that hands back an
    // arbitrary-sized buffer the caller did not size, so a length mismatch
    // here is the marshalling and not the image.
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

        // The dimensions matter most on exactly the builds this check runs on.
        // A phone links an audio-only FFmpeg, which has no decoder that could
        // work out a picture's size, so these come from the façade reading the
        // PNG header itself - and a zero here means that parse silently
        // stopped happening, which is also what would bring back the
        // "Could not find codec parameters ... unspecified size" noise on
        // every art-bearing file the app opens.
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
            // Every exception, not just CheckFailedException: a
            // DllNotFoundException or a marshalling crash is exactly the
            // finding this suite exists for, and it must be reported as a
            // failed check rather than take the whole run down.
            return new CheckResult(name, Passed: false, failed.ToString(), clock.Elapsed);
        }
    }

    private static CheckResult Run(string name, Action check) =>
        Run(name, () => { check(); return string.Empty; });

    // A stream that reports no length and refuses to seek, wrapping one that
    // can do both. What a decoder sees when the bytes are arriving from
    // somewhere rather than sitting on a disk.
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
