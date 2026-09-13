using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace FFAudio
{
    // What the caller wants the decoder to hand back.
    //
    // S24 is packed three-byte little-endian. It is the odd one out: swresample
    // cannot produce it, so the façade packs it from S32 by dropping the low
    // byte, which is lossless because FFmpeg carries 24-bit PCM left-aligned.
    // It is also what miniaudio's ma_format_s24 and most hardware sinks take,
    // and it is the only way to hand a 24-bit source over without either
    // widening it or losing a bit.
    //
    // All four are here because a library does not get to decide its caller's
    // sink. S32 and F32 cost nothing to expose - swresample produces both
    // directly - and a caller feeding an analysis tool or a DAW-adjacent
    // pipeline should not have to fork this to get them.
    public enum SampleFormat
    {
        S16 = 0,
        S24 = 1,
        S32 = 2,
        F32 = 3,
    }

    // ChannelLayout describes the PCM being delivered; Codec and Container
    // describe where it came from. Both are here rather than on Decoder
    // because they are answered once at open and never change, and a caller
    // printing a file's properties wants them in the same breath as its rate.
    public readonly record struct AudioFormat(
        int SampleRate,
        int Channels,
        SampleFormat SampleFormat,
        int SourceBitDepth,
        int SourceSampleRate,
        int SourceChannels,
        TimeSpan? Duration,
        string ChannelLayout,
        string Codec,
        string Container)
    {
        public int BytesPerFrame => SampleFormat switch
        {
            SampleFormat.S16 => 2 * Channels,
            SampleFormat.S24 => 3 * Channels,
            _ => 4 * Channels,
        };
    }

    // The encoded image exactly as the container holds it - not decoded, not
    // rescaled. Bytes rather than a Stream because it is already in memory by
    // the time the container has been opened at all.
    //
    // Width and Height come from the image's own header rather than from
    // decoding it, so a picture in a format the façade cannot parse reports
    // zero for both. Bytes without dimensions is an ordinary answer here, not
    // a failure - callers that only want to show the image never need them.
    public sealed record CoverArt(byte[] Bytes, string MimeType, int Width, int Height);

    public sealed class DecodeException(string message, int code)
        : IOException($"{message}: {Native.Describe(code)}")
    {
        public int Code { get; } = code;
    }

    // One decode of one source, over either a file path or a managed Stream.
    // Not thread-safe: like the native decoder it wraps, one instance belongs
    // to one decode thread.
    //
    // The Stream overload is the reason to reach for this over a process
    // wrapper. FFmpeg takes read and seek callbacks, so a track streamed from a
    // server is seekable by construction rather than by whatever the platform's
    // HTTP layer decided - and the Stream is the caller's, so authentication,
    // range probing and retry policy all stay in the caller's own HTTP stack
    // rather than being duplicated inside FFmpeg's (which is why the mobile
    // builds are configured --disable-network).
    public sealed unsafe class Decoder : IDisposable
    {
        private IntPtr _handle;
        private readonly Stream? _stream;
        private readonly bool _ownsStream;
        // Pins this instance for as long as the native decoder can call back
        // into it. The callbacks are static so they survive AOT compilation on
        // iOS; this handle is how they find their way back to an instance.
        private GCHandle _self;

        public AudioFormat Format { get; }

        private Decoder(IntPtr handle, Stream? stream, bool ownsStream, GCHandle self)
        {
            _handle = handle;
            _stream = stream;
            _ownsStream = ownsStream;
            _self = self;

            var rc = Native.GetFormat(handle, out var format);
            if (rc != Native.Ok)
            {
                Dispose();
                throw new DecodeException("Could not read the decoded audio format", rc);
            }

            var codec = "";
            var container = "";
            NativeText.ReadPair(
                (a, aBytes, b, bBytes) => Native.Names(handle, a, aBytes, b, bBytes),
                out codec, out container);

            Format = new AudioFormat(
                format.SampleRate,
                format.Channels,
                (SampleFormat)format.SampleFormat,
                format.SourceBitDepth,
                format.SourceSampleRate,
                format.SourceChannels,
                format.DurationMs < 0 ? null : TimeSpan.FromMilliseconds(format.DurationMs),
                NativeText.ReadString((buffer, bytes) => Native.ChannelLayout(handle, buffer, bytes)),
                codec,
                container);
        }

        // Whether this build can decode through the façade at all - i.e.
        // whether ffaudio is present, loadable, and the ABI this assembly was
        // compiled against.
        //
        // Worth asking rather than assuming: the native payload is a separate
        // package per platform, so "the managed assembly is here" and "there is
        // a decoder here" are genuinely different facts. Asking costs one
        // P/Invoke, once, and the alternative is a DllNotFoundException thrown
        // from a decode thread at the moment somebody presses play.
        //
        // Cached because a failure is permanent for the process: the resolver
        // has already walked every candidate path by the time this returns.
        public static bool IsAvailable => _isAvailable ??= ProbeAvailability();

        private static bool? _isAvailable;

        private static bool ProbeAvailability()
        {
            try
            {
                return Native.AbiVersion() == Native.ExpectedAbiVersion;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                // A library built for the wrong architecture - a stale x64
                // artifact on an arm64 machine, most likely.
                return false;
            }
        }

        // Verified once rather than per open: a library whose ABI does not
        // match reads the format struct at the wrong offsets and reports
        // plausible nonsense instead of failing.
        private static void EnsureAbi()
        {
            var actual = Native.AbiVersion();
            if (actual != Native.ExpectedAbiVersion)
                throw new DecodeException(
                    $"ffaudio reports ABI {actual}, this build expects {Native.ExpectedAbiVersion}",
                    Native.Ok);
        }

        // sampleRate/channels of 0 ask for the source's own, which is how a
        // bit-perfect open asks for no conversion at all.
        public static Decoder OpenPath(string path, SampleFormat format, int sampleRate = 0, int channels = 0)
        {
            EnsureAbi();
            var rc = Native.OpenPath(path, (int)format, sampleRate, channels, out var handle);
            if (rc != Native.Ok)
                throw new DecodeException($"Could not open {path}", rc);

            return new Decoder(handle, stream: null, ownsStream: false, default);
        }

        // formatHint names a demuxer to prefer (FFmpeg's short name, e.g.
        // "mp4"), skipping the probe on a stream whose container the caller
        // already knows.
        //
        // A preference rather than a verdict: forcing a demuxer discards
        // FFmpeg's probe, so a hint that is wrong about the bytes does not
        // open slowly, it does not open at all. The hint's source is a
        // catalog entry describing a file on a server's disk, and the bytes
        // on the wire are whatever that server chose to send - so being wrong
        // is an ordinary event, not a corrupt library. When the forced open
        // fails the stream is rewound and opened again by probing, which is
        // what would have happened with no hint at all.
        public static Decoder OpenStream(Stream stream, SampleFormat format,
                                               int sampleRate = 0, int channels = 0,
                                               string? formatHint = null, bool ownsStream = false,
                                               ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            EnsureAbi();

            if (formatHint is { Length: > 0 } && stream.CanSeek)
            {
                try
                {
                    // ownsStream deliberately false on this attempt: a failure
                    // here is not the end of the stream's life, it is the
                    // start of the second attempt on the same stream.
                    return OpenStreamAs(stream, format, sampleRate, channels, formatHint, ownsStream: false);
                }
                catch (DecodeException rejected)
                {
                    // Logged rather than swallowed: the fallback means a
                    // mislabelled track plays, but the label is still wrong,
                    // and this is the only place that ever finds out.
                    logger?.LogWarning(
                        rejected,
                        "The {Hint} demuxer would not open this stream; probing for the real container instead",
                        formatHint);

                    // Back to where the forced attempt started reading. A
                    // demuxer that refused the stream still consumed some of
                    // it, and probing from the middle of a file finds nothing.
                    stream.Seek(0, SeekOrigin.Begin);
                }

                return OpenStreamAs(stream, format, sampleRate, channels, formatHint: null, ownsStream);
            }

            return OpenStreamAs(stream, format, sampleRate, channels, formatHint, ownsStream);
        }

        private static Decoder OpenStreamAs(Stream stream, SampleFormat format,
                                                  int sampleRate, int channels,
                                                  string? formatHint, bool ownsStream)
        {
            var state = new StreamState(stream);
            var self = GCHandle.Alloc(state);
            long size;
            try
            {
                size = stream.CanSeek ? stream.Length : -1;
            }
            catch (Exception)
            {
                size = -1;
            }

            var rc = Native.OpenIo(
                GCHandle.ToIntPtr(self),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int>)&ReadTrampoline,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long, int, long>)&SeekTrampoline,
                size,
                stream.CanSeek ? 1 : 0,
                formatHint,
                (int)format, sampleRate, channels,
                out var handle);

            if (rc != Native.Ok)
            {
                self.Free();
                if (ownsStream)
                    stream.Dispose();
                throw new DecodeException("Could not open the audio stream", rc);
            }

            return new Decoder(handle, stream, ownsStream, self);
        }

        // Fills as much of buffer as the source has left. Returns 0 at the end
        // of the track - and only at the end, never as "nothing right now",
        // because the native side turns a short managed read into FFmpeg's own
        // end-of-file rather than letting it read as a stall.
        public int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            if (buffer.IsEmpty)
                return 0;

            fixed (byte* pointer = buffer)
            {
                var rc = Native.Read(_handle, pointer, buffer.Length, out var written);
                if (rc == Native.EndOfStream)
                    return 0;
                if (rc != Native.Ok)
                    throw new DecodeException("Decoding failed", rc);
                return written;
            }
        }

        // Returns where decode actually resumed, which is at or before the
        // request - the demuxer is keyframe-bound. Callers that report the
        // request instead end up with a scrubber permanently offset from the
        // audio.
        public TimeSpan Seek(TimeSpan position)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            var requestedMs = (long)Math.Max(0, position.TotalMilliseconds);
            var rc = Native.Seek(_handle, requestedMs, out var landedMs);
            if (rc != Native.Ok)
                throw new DecodeException($"Could not seek to {requestedMs}ms", rc);

            return TimeSpan.FromMilliseconds(landedMs);
        }


        // ------------------------------------------------------------ metadata

        // Read on first ask rather than at open: a player showing a file's
        // title wants these, and a player decoding ten thousand tracks into a
        // ring buffer never asks.
        public IReadOnlyList<KeyValuePair<string, string>> Tags => _tags ??= ReadTags();

        private IReadOnlyList<KeyValuePair<string, string>>? _tags;

        private IReadOnlyList<KeyValuePair<string, string>> ReadTags()
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            var rc = Native.TagCount(_handle, out var count);
            if (rc != Native.Ok)
                throw new DecodeException("Could not count this file's tags", rc);

            var handle = _handle;
            var tags = new List<KeyValuePair<string, string>>(count);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                NativeText.ReadPair((k, kBytes, v, vBytes) => Native.TagAt(handle, index, k, kBytes, v, vBytes),
                         out var key, out var value);
                tags.Add(new KeyValuePair<string, string>(key, value));
            }

            return tags;
        }

        // Null when the file carries none, which is the ordinary case rather
        // than a failure. A method and not a property because it copies the
        // whole image - album art is routinely megabytes, and a property that
        // does that surprises everyone who touches it in a debugger.
        public CoverArt? TryReadCoverArt()
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            var mime = new byte[256];
            int rc;
            int size;
            int width;
            int height;
            fixed (byte* mimeBuffer = mime)
                rc = Native.CoverArt(_handle, null, 0, out size, mimeBuffer, mime.Length,
                                     out width, out height);

            if (rc == Native.NotPresent)
                return null;
            if (rc != Native.Ok)
                throw new DecodeException("Could not measure this file's cover art", rc);

            // Asked for again now that there is somewhere to put it. The
            // façade writes nothing into a buffer that would not hold all of
            // it, so this cannot come back as a partial image.
            var bytes = new byte[size];
            fixed (byte* buffer = bytes)
            fixed (byte* mimeBuffer = mime)
                rc = Native.CoverArt(_handle, buffer, bytes.Length, out _, mimeBuffer, mime.Length,
                                     out _, out _);

            if (rc != Native.Ok)
                throw new DecodeException("Could not read this file's cover art", rc);

            return new CoverArt(bytes, NativeText.Text(mime), width, height);
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                Native.Close(handle);

            // After the native decoder is closed, and not before: it can be
            // inside a read callback right up to that call returning.
            if (_self.IsAllocated)
                _self.Free();

            if (_ownsStream)
                _stream?.Dispose();
        }

        private sealed class StreamState(Stream stream)
        {
            public Stream Stream { get; } = stream;
            // Latched so a stream that has already failed is not asked again
            // on every subsequent callback. The decode is over either way; the
            // difference is whether it ends or thrashes.
            public bool Broken;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadTrampoline(IntPtr opaque, byte* buffer, int bufferBytes)
        {
            var state = (StreamState?)GCHandle.FromIntPtr(opaque).Target;
            if (state is null || state.Broken || bufferBytes <= 0)
                return 0;

            try
            {
                return state.Stream.Read(new Span<byte>(buffer, bufferBytes));
            }
            catch (Exception)
            {
                // An exception must not cross back into C. The negative return
                // is the callback contract's own way to say the same thing,
                // and the native side turns it into an AVERROR the open or
                // read call surfaces.
                state.Broken = true;
                return -1;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static long SeekTrampoline(IntPtr opaque, long offset, int whence)
        {
            var state = (StreamState?)GCHandle.FromIntPtr(opaque).Target;
            if (state is null || state.Broken)
                return -1;

            try
            {
                if (whence == Native.SeekSize)
                    return state.Stream.CanSeek ? state.Stream.Length : -1;

                return state.Stream.Seek(offset, (SeekOrigin)whence);
            }
            catch (Exception)
            {
                state.Broken = true;
                return -1;
            }
        }
    }
}
