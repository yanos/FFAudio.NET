using System;

namespace FFAudio
{
    // Build metadata reported by the loaded FFmpeg binary, not the build scripts.
    public static unsafe class FFmpegBuild
    {
        // For example, "LGPL version 2.1 or later".
        public static string License => _license ??= Read(Native.FfmpegLicense);

        // The verbatim FFmpeg configure line.
        public static string Configuration => _configuration ??= Read(Native.FfmpegConfiguration);

        // A release or git-describe version, with a numeric libavutil fallback.
        public static string Version => _version ??= Read(Native.FfmpegVersion);

        // Whether the binary uses the LGPL terms expected by this repository.
        public static bool IsRedistributable =>
            License.StartsWith("LGPL", StringComparison.Ordinal);

        private static string? _license;
        private static string? _configuration;
        private static string? _version;

        private static string Read(NativeText.FillOne fill) => NativeText.ReadString(fill);
    }
}
