using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FFAudio
{
    // P/Invoke for native/ffaudio.h. Nothing above this file knows FFmpeg
    // exists; nothing in it knows anything about FFmpeg either, because the
    // façade's whole purpose is that its ABI is eight functions over ints and
    // byte buffers. See that header for why.
    internal static class Native
    {
        internal const string Library = "ffaudio";

        // Must match FFAUDIO_ABI_VERSION. Checked once at load, because
        // the failure mode of a mismatched library is a struct read at the
        // wrong offsets rather than an error.
        internal const int ExpectedAbiVersion = 1;

        internal const int Ok = 0;
        internal const int EndOfStream = 1;

        internal const int SeekSize = 0x10000;

        internal const int ErrorBase = -10000;
        internal const int NotPresent = ErrorBase - 6;
        internal const int Truncated = ErrorBase - 7;

        // A module initializer rather than this class's static constructor,
        // which is where it was and which worked everywhere except the one
        // platform that needs it most. A P/Invoke has no body for a type
        // initializer to run in front of, and Mono on iOS resolves the library
        // for the stub before it runs the declaring type's cctor - so the
        // resolver was registered by the very call that had already failed
        // with DllNotFoundException. The framework loaded fine when asked
        // directly; nothing was ever asking. Registering at module load has no
        // such ordering to get wrong.
        // CA2255 says a module initializer belongs in an application rather
        // than a library, whose author cannot know when it runs. Here that is
        // the point: it must run before any P/Invoke in this assembly, and
        // there is no earlier hook a library can offer.
#pragma warning disable CA2255
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void RegisterResolver()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        }
#pragma warning restore CA2255

        // The façade is not on a default search path in any of the three
        // situations that matter - a dev build reading it out of
        // native/artifacts/, a test run, and a consuming app - so each is named
        // rather than left to the loader. FFAUDIO_LIBRARY is first so a bisect
        // against a differently-built FFmpeg needs no rebuild.
        //
        // The last resort is TryLoad by plain name, which is what picks up a
        // NuGet's own runtimes/<rid>/native/ payload: the host copies that
        // beside the consumer's binary, so by then it is simply there.
        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        {
            if (name != Library)
                return IntPtr.Zero;

            if (Environment.GetEnvironmentVariable("FFAUDIO_LIBRARY") is { Length: > 0 } explicitPath
                && NativeLibrary.TryLoad(explicitPath, out var fromEnvironment))
                return fromEnvironment;

            // iOS ships the façade as an embedded framework, whose binary sits
            // at a nested path the loader is never told about: .NET-for-iOS
            // resolves a P/Invoke by dlopen-ing the DllImport string, which
            // matches nothing here even though the app's own load commands
            // name the framework. Every consumer on iOS has this problem, and
            // none of them should have to know about it.
            if (OperatingSystem.IsIOS())
            {
                var framework = Path.Combine(AppContext.BaseDirectory, "Frameworks", "ffaudio.framework", "ffaudio");
                return NativeLibrary.TryLoad(framework, out var embedded) ? embedded : IntPtr.Zero;
            }

            foreach (var candidate in Candidates())
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }

            return NativeLibrary.TryLoad(Library, assembly, path, out var byName) ? byName : IntPtr.Zero;
        }

        private static string[] Candidates()
        {
            var file = OperatingSystem.IsWindows() ? "ffaudio.dll"
                : OperatingSystem.IsMacOS() ? "libffaudio.dylib"
                : "libffaudio.so";

            var baseDirectory = AppContext.BaseDirectory;
            var platform = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsMacOS() ? "macos"
                : "linux";

            // Walking up to the repo root is a development convenience for
            // this repo's own tests, which run against whatever
            // native/build-all.sh last produced. A consuming app finds the
            // library beside itself on the first candidate and never looks
            // further.
            var repoRelative = Path.Combine("native", "artifacts", platform, file);
            var walked = new string[6];
            var directory = baseDirectory;
            for (var i = 0; i < walked.Length; i++)
            {
                walked[i] = Path.Combine(directory, repoRelative);
                directory = Path.Combine(directory, "..");
            }

            var candidates = new string[walked.Length + 1];
            candidates[0] = Path.Combine(baseDirectory, file);
            Array.Copy(walked, 0, candidates, 1, walked.Length);
            return candidates;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DecoderFormat
        {
            public int SampleRate;
            public int Channels;
            public int SampleFormat;
            public int SourceBitDepth;
            public int SourceSampleRate;
            public int SourceChannels;
            public long DurationMs;
        }

        [DllImport(Library, EntryPoint = "ffaudio_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AbiVersion();

        [DllImport(Library, EntryPoint = "ffaudio_decoder_open_path", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int OpenPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                            int requestedFormat, int requestedSampleRate, int requestedChannels,
                                            out IntPtr decoder);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_open_io", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int OpenIo(IntPtr opaque,
                                          IntPtr read, IntPtr seek,
                                          long size, int seekable,
                                          [MarshalAs(UnmanagedType.LPUTF8Str)] string? formatHint,
                                          int requestedFormat, int requestedSampleRate, int requestedChannels,
                                          out IntPtr decoder);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_get_format", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetFormat(IntPtr decoder, out DecoderFormat format);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_read", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int Read(IntPtr decoder, byte* buffer, int bufferBytes, out int bytesWritten);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_seek", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Seek(IntPtr decoder, long positionMs, out long landedMs);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Close(IntPtr decoder);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_tag_count", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TagCount(IntPtr decoder, out int count);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_tag_at", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int TagAt(IntPtr decoder, int index,
                                                byte* key, int keyBytes,
                                                byte* value, int valueBytes);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_cover_art", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int CoverArt(IntPtr decoder,
                                                   byte* buffer, int bufferBytes,
                                                   out int outBytes,
                                                   byte* mime, int mimeBytes);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_channel_layout", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int ChannelLayout(IntPtr decoder, byte* buffer, int bufferBytes);

        [DllImport(Library, EntryPoint = "ffaudio_decoder_names", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int Names(IntPtr decoder,
                                                byte* codec, int codecBytes,
                                                byte* container, int containerBytes);

        [DllImport(Library, EntryPoint = "ffaudio_ffmpeg_license", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int FfmpegLicense(byte* buffer, int bufferBytes);

        [DllImport(Library, EntryPoint = "ffaudio_ffmpeg_configuration", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int FfmpegConfiguration(byte* buffer, int bufferBytes);

        [DllImport(Library, EntryPoint = "ffaudio_ffmpeg_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int FfmpegVersion(byte* buffer, int bufferBytes);

        [DllImport(Library, EntryPoint = "ffaudio_error_string", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void ErrorString(int code, byte* buffer, int bufferBytes);

        internal static unsafe string Describe(int code)
        {
            const int capacity = 256;
            var buffer = stackalloc byte[capacity];
            ErrorString(code, buffer, capacity);
            return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {code}";
        }
    }
}
