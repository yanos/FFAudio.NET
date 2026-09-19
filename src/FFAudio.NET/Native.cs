using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FFAudio
{
    // P/Invoke bindings for the stable ABI in native/ffaudio.h.
    internal static class Native
    {
        internal const string Library = "ffaudio";

        // Must match FFAUDIO_ABI_VERSION.
        internal const int ExpectedAbiVersion = 1;

        internal const int Ok = 0;
        internal const int EndOfStream = 1;

        internal const int SeekSize = 0x10000;

        internal const int ErrorBase = -10000;
        internal const int NotPresent = ErrorBase - 6;
        internal const int Truncated = ErrorBase - 7;

        // Mono may resolve an iOS P/Invoke before the declaring type's static
        // constructor, so the resolver must be registered at module load.
#pragma warning disable CA2255
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void RegisterResolver()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        }
#pragma warning restore CA2255

        // Prefer FFAUDIO_LIBRARY, then platform-specific locations, then the
        // default loader path used by NuGet runtime assets.
        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        {
            if (name != Library)
                return IntPtr.Zero;

            if (Environment.GetEnvironmentVariable("FFAUDIO_LIBRARY") is { Length: > 0 } explicitPath
                && NativeLibrary.TryLoad(explicitPath, out var fromEnvironment))
                return fromEnvironment;

            // .NET for iOS does not resolve DllImport names inside embedded frameworks.
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

            // Search upward for native/artifacts during repository test runs.
            var repoRelative = Path.Combine("native", "artifacts", platform, file);
            var walked = new string[7];
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
                                                   byte* mime, int mimeBytes,
                                                   out int outWidth, out int outHeight);

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
