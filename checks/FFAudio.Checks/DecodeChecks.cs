using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

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

        // First, and separately, because everything after it is meaningless
        // if the answer is no: a run where the façade never loaded should say
        // so once rather than fail eight times for the same reason.
        results.Add(Run("the façade is loadable here", () =>
        {
            if (!Decoder.IsAvailable)
                throw new CheckFailedException(
                    "Decoder.IsAvailable is false - the façade was not found, would not load, "
                    + "or reports an ABI this build was not compiled against");
        }));

        if (!results[0].Passed)
            return results;

        var directory = Path.Combine(Path.GetTempPath(), "ffaudio-checks");
        Directory.CreateDirectory(directory);

        try
        {
            var path = SyntheticHiResWav.CreateFile(
                directory, "hires.wav", Rate, Frames, SyntheticHiResWav.Ramp24());

            results.Add(Run("a 24-bit source arrives with every bit", () => EveryBitSurvives(path)));
            results.Add(Run("each sample format is delivered at its own width", () => EveryFormatIsItsOwnWidth(path)));
            results.Add(Run("a managed stream decodes what the path decodes", () => StreamMatchesPath(path)));
            results.Add(Run("an unseekable stream still decodes", () => UnseekableStreamDecodes(path)));
            results.Add(Run("seeking lands at or before the request", () => SeekLandsWhereItSays(path)));
            results.Add(Run("a resample delivers the frames it promises", () => ResamplingIsRight(path)));

            var tagged = SyntheticTaggedAiff.CreateFile(directory, "tagged.aiff", 44100, 4410);

            results.Add(Run("a file says what it was tagged with", () => TagsComeBack(tagged)));
            results.Add(Run("cover art comes back byte for byte", () => CoverArtComesBack(tagged)));
            results.Add(Run("an untagged file says so rather than failing", () => NoTagsIsAnAnswer(path)));
            results.Add(Run("a file names its codec and its container", () => NamesAreRight(tagged, path)));
            results.Add(Run("the layout describes the pcm being delivered", () => LayoutFollowsTheDownmix(tagged)));
            results.Add(Run("the binary says which FFmpeg it is", WhichFfmpeg));
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

    // Every format the library offers, at the width it says. This is where an
    // AOT-specific marshalling fault would show as plausible nonsense rather
    // than a throw: a struct read at the wrong offsets still returns numbers.
    private static string EveryFormatIsItsOwnWidth(string path)
    {
        var widths = new (SampleFormat Format, int Bytes)[]
        {
            (SampleFormat.S16, 2),
            (SampleFormat.S24, 3),
            (SampleFormat.S32, 4),
            (SampleFormat.F32, 4),
        };

        foreach (var (format, bytes) in widths)
        {
            using var decoder = Decoder.OpenPath(path, format);
            Expect(decoder.Format.BytesPerFrame == bytes * Channels,
                $"{format}: {decoder.Format.BytesPerFrame} bytes per frame, wanted {bytes * Channels}");

            var pcm = DecodeAll(decoder);
            Expect(pcm.Length == Frames * bytes * Channels,
                $"{format}: {pcm.Length} bytes, wanted {Frames * bytes * Channels}");
        }

        return "S16, S24, S32, F32";
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
        using (var file = File.OpenRead(path))
        using (var decoder = Decoder.OpenStream(file, SampleFormat.S24))
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
        using var file = File.OpenRead(path);
        using var forward = new ForwardOnlyStream(file);
        using var decoder = Decoder.OpenStream(forward, SampleFormat.S24);

        var pcm = DecodeAll(decoder);
        Expect(pcm.Length == Frames * 6, $"{pcm.Length} bytes, wanted {Frames * 6}");

        return $"{pcm.Length} bytes with no seeking";
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

        var pcm = DecodeAll(decoder);
        Expect(pcm.Length > 0, "nothing decoded after the seek");

        return $"asked {asked.TotalMilliseconds:F0}ms, landed {landed.TotalMilliseconds:F0}ms";
    }

    // Asking for a rate the source is not. swresample is a separate FFmpeg
    // library from the demuxer and the decoder, so it is a separate thing for
    // a cross-compiled build to have got wrong.
    private static string ResamplingIsRight(string path)
    {
        const int target = 48000;

        using var decoder = Decoder.OpenPath(path, SampleFormat.S24, target);
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

        return $"{art.Bytes.Length} bytes of {art.MimeType}";
    }

    // Most music files have no embedded art and plenty have no tags, so a
    // caller asking is not making a mistake. An empty answer rather than a
    // throw is the difference between a library and a minefield.
    private static string NoTagsIsAnAnswer(string path)
    {
        using var decoder = Decoder.OpenPath(path, SampleFormat.S24);

        Expect(decoder.Tags.Count == 0, $"{decoder.Tags.Count} tags on a file that has none");
        Expect(decoder.TryReadCoverArt() is null, "cover art on a file that has none");

        return "no tags, no art, no exception";
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

    // Delivered, not source: a caller that asked for a downmix has to be told
    // about the channels it is going to get. `channels` is a number and a
    // number cannot say which channel is which, which is the whole reason the
    // layout string exists.
    private static string LayoutFollowsTheDownmix(string path)
    {
        using var stereo = Decoder.OpenPath(path, SampleFormat.S16);
        Expect(stereo.Format.Channels == 2, $"{stereo.Format.Channels} channels, wanted 2");
        Expect(stereo.Format.ChannelLayout == "stereo", $"layout {Quote(stereo.Format.ChannelLayout)}, wanted stereo");

        using var mono = Decoder.OpenPath(path, SampleFormat.S16, sampleRate: 0, channels: 1);
        Expect(mono.Format.SourceChannels == 2, $"source channels {mono.Format.SourceChannels}, wanted 2");
        Expect(mono.Format.Channels == 1, $"{mono.Format.Channels} channels, wanted 1");
        Expect(mono.Format.ChannelLayout == "mono", $"layout {Quote(mono.Format.ChannelLayout)}, wanted mono");

        return "stereo as read, mono as asked for";
    }

    // Which FFmpeg is actually inside this artifact, asked of the artifact
    // rather than of the build script that made it - a configure line does
    // not travel with a binary and avutil does. Reported rather than judged:
    // a development build against a distro FFmpeg is GPL and that is fine
    // here, so this fails only if the binary cannot answer at all.
    //
    // It needs no decoder open, which is the point of it living in FFmpegBuild
    // rather than on Decoder, and it is worth running on a phone because a
    // static mobile build is the one that has to be LGPL.
    private static string WhichFfmpeg()
    {
        var version = FFmpegBuild.Version;
        var license = FFmpegBuild.License;
        var configuration = FFmpegBuild.Configuration;

        Expect(version.Length > 0, "the build reports no version");
        Expect(license.Length > 0, "the build reports no license");

        // Long enough to have gone round NativeText's grow-and-retry loop,
        // which is the only part of this that can be wrong on one platform
        // and right on another.
        Expect(configuration.Length > 64, $"a {configuration.Length}-character configure line is not one");

        return $"{version}, {license}"
            + (FFmpegBuild.IsRedistributable ? "" : " - a development build, not shippable");
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
}
