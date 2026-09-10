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
