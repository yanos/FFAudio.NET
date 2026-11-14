using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace FFAudio
{
    // S24 is packed three-byte little-endian. The native layer converts it
    // losslessly from FFmpeg's left-aligned S32 output.
    public enum SampleFormat
    {
        S16 = 0,
        S24 = 1,
        S32 = 2,
        F32 = 3,
    }

    // ChannelLayout describes the output PCM; Codec and Container describe the source.
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

    // Encoded cover art, unchanged from the container. Unsupported image
    // headers report zero width and height without discarding the bytes.
    public sealed record CoverArt(byte[] Bytes, string MimeType, int Width, int Height);

    public sealed class DecodeException(string message, int code)
        : IOException($"{message}: {Native.Describe(code)}")
    {
        public int Code { get; } = code;
    }

    // Operations on one decoder are serialized. Stream callbacks must not
    // reenter the same decoder.
    public sealed unsafe class Decoder : IDisposable
    {
        private readonly object _gate = new();
        private bool _operationInProgress;
        private IntPtr _handle;
        private readonly Stream? _stream;
        // Ownership transfers only after an open/probe attempt succeeds.
        private bool _ownsStream;
        // Lets static, AOT-compatible callbacks recover their managed state.
        private GCHandle _self;

        public AudioFormat Format { get; }

        private Decoder(IntPtr handle, Stream? stream, GCHandle self)
        {
            _handle = handle;
            _stream = stream;
            _self = self;

            try
            {
                var rc = Native.GetFormat(handle, out var format);
                if (rc != Native.Ok)
                    throw new DecodeException("Could not read the decoded audio format", rc);

                NativeText.ReadPair(
                    (a, aBytes, b, bBytes) => Native.Names(handle, a, aBytes, b, bBytes),
                    out var codec, out var container);

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
            catch
            {
                // Construction can fail after native open (for example, an
                // unrepresentable duration). No caller can dispose us then.
                Dispose();
                throw;
            }
        }

        // True when a compatible native library can be loaded. The result is cached.
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
                // The native library targets a different architecture.
                return false;
            }
        }

        // Reject incompatible struct layouts before reading decoder state.
        private static void EnsureAbi()
        {
            var actual = Native.AbiVersion();
            if (actual != Native.ExpectedAbiVersion)
                throw new DecodeException(
                    $"ffaudio reports ABI {actual}, this build expects {Native.ExpectedAbiVersion}",
                    Native.Ok);
        }

        // Float32 is the default delivery format. Zero sampleRate or channels
        // preserves the corresponding source value.
        public static Decoder OpenPath(string path, SampleFormat format = SampleFormat.F32,
                                       int sampleRate = 0, int channels = 0)
        {
            EnsureAbi();
            var rc = Native.OpenPath(path, (int)format, sampleRate, channels, out var handle);
            if (rc != Native.Ok)
                throw new DecodeException($"Could not open {path}", rc);

            return new Decoder(handle, stream: null, default);
        }

        // formatHint is an FFmpeg demuxer name (for example, "mp4"). A rejected
        // hint on a seekable stream falls back to probing from the beginning.
        public static Decoder OpenStream(Stream stream, SampleFormat format = SampleFormat.F32,
                                               int sampleRate = 0, int channels = 0,
                                               string? formatHint = null, bool ownsStream = false,
                                               ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            EnsureAbi();

            try
            {
                Decoder decoder;
                if (formatHint is { Length: > 0 } && stream.CanSeek)
                {
                    try
                    {
                        decoder = OpenStreamAs(stream, format, sampleRate, channels, formatHint);
                    }
                    catch (DecodeException rejected)
                    {
                        // Surface bad catalog metadata even though probing may recover.
                        logger?.LogWarning(
                            rejected,
                            "The {Hint} demuxer would not open this stream; probing for the real container instead",
                            formatHint);

                        // The rejected demuxer may have consumed input.
                        stream.Seek(0, SeekOrigin.Begin);
                        decoder = OpenStreamAs(stream, format, sampleRate, channels, formatHint: null);
                    }
                }
                else
                    decoder = OpenStreamAs(stream, format, sampleRate, channels, formatHint);

                // Failed attempts borrow the stream so fallback remains possible.
                // The successful decoder assumes the requested ownership once.
                decoder._ownsStream = ownsStream;
                return decoder;
            }
            catch
            {
                if (ownsStream)
                    stream.Dispose();
                throw;
            }
        }

        private static Decoder OpenStreamAs(Stream stream, SampleFormat format,
                                                  int sampleRate, int channels,
                                                  string? formatHint)
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

            int rc;
            IntPtr handle;
            try
            {
                rc = Native.OpenIo(
                    GCHandle.ToIntPtr(self),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int>)&ReadTrampoline,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long, int, long>)&SeekTrampoline,
                    size,
                    stream.CanSeek ? 1 : 0,
                    formatHint,
                    (int)format, sampleRate, channels,
                    out handle);
            }
            catch
            {
                self.Free();
                throw;
            }

            if (rc != Native.Ok)
            {
                self.Free();
                throw new DecodeException("Could not open the audio stream", rc);
            }

            return new Decoder(handle, stream, self);
        }

        // Returns decoded bytes, or zero only at end of stream.
        public int Read(Span<byte> buffer)
        {
            using var operation = EnterNativeOperation();
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

        // Returns the actual resume position, which may precede the request.
        public TimeSpan Seek(TimeSpan position)
        {
            using var operation = EnterNativeOperation();

            var requestedMs = (long)Math.Max(0, position.TotalMilliseconds);
            var rc = Native.Seek(_handle, requestedMs, out var landedMs);
            if (rc != Native.Ok)
                throw new DecodeException($"Could not seek to {requestedMs}ms", rc);

            return TimeSpan.FromMilliseconds(landedMs);
        }

        // Loaded lazily to avoid metadata work for decode-only callers.
        public IReadOnlyList<KeyValuePair<string, string>> Tags
        {
            get
            {
                lock (_gate)
                    return _tags ??= ReadTags();
            }
        }

        private IReadOnlyList<KeyValuePair<string, string>>? _tags;

        private IReadOnlyList<KeyValuePair<string, string>> ReadTags()
        {
            using var operation = EnterNativeOperation();

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

        // Returns null when absent. This is a method because it copies the full image.
        public CoverArt? TryReadCoverArt()
        {
            using var operation = EnterNativeOperation();

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

            // Fetch the complete image after measuring it.
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
            lock (_gate)
            {
                if (_handle == IntPtr.Zero)
                    return;
                ThrowIfReentrant();
                _operationInProgress = true;
                try
                {
                    var handle = _handle;
                    _handle = IntPtr.Zero;
                    Native.Close(handle);

                    // Keep callback state alive until the native decoder is closed.
                    if (_self.IsAllocated)
                        _self.Free();

                    if (_ownsStream)
                        _stream?.Dispose();
                }
                finally
                {
                    _operationInProgress = false;
                }
            }
        }

        private NativeOperation EnterNativeOperation()
        {
            Monitor.Enter(_gate);
            try
            {
                ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
                ThrowIfReentrant();
                _operationInProgress = true;
                return new NativeOperation(this);
            }
            catch
            {
                Monitor.Exit(_gate);
                throw;
            }
        }

        private void ThrowIfReentrant()
        {
            // Monitor locks are reentrant. A custom Stream must not call back
            // into FFmpeg or free it while its native operation is on the stack.
            if (_operationInProgress)
                throw new InvalidOperationException("Stream callbacks cannot reenter the same decoder.");
        }

        private readonly struct NativeOperation(Decoder decoder) : IDisposable
        {
            public void Dispose()
            {
                decoder._operationInProgress = false;
                Monitor.Exit(decoder._gate);
            }
        }

        private sealed class StreamState(Stream stream)
        {
            public Stream Stream { get; } = stream;
            // Prevent repeated callbacks after the first stream failure.
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
                // Exceptions cannot cross the native callback boundary.
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
