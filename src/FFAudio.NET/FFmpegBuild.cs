using System;

namespace FFAudio
{
    // Which FFmpeg is inside this build, asked of the binary rather than of the
    // build that produced it.
    //
    // The distinction is the whole point. A configure line lives in a script, a
    // variant lives in an environment variable, and neither travels with a
    // .dylib that has been copied into a NuGet, embedded in an app bundle and
    // shipped. avutil travels with it, and avutil was there when the decision
    // was made.
    //
    // This exists for the licence, which is the one property of an FFmpeg build
    // that cannot be fixed after the fact. FFmpeg may be linked here only under
    // the LGPL: no --enable-gpl, no --enable-nonfree. On desktop that is
    // somebody else's package and the answer varies by machine - a MacPorts or
    // Homebrew FFmpeg is GPL-enabled and cannot be shipped, which is fine for
    // development and fatal for a release. On a phone it is linked in, and the
    // relink route the LGPL asks for starts with Configuration below: the exact
    // arguments that produced what is inside the binary.
    //
    // Nothing here opens a file or touches a decoder, so it is safe to ask
    // before anything else and cheap to log at startup.
    public static unsafe class FFmpegBuild
    {
        // "LGPL version 2.1 or later", "GPL version 2 or later", or "nonfree and
        // unredistributable".
        public static string License => _license ??= Read(Native.FfmpegLicense);

        // The configure line FFmpeg was built with, verbatim - well over a
        // kilobyte for a distro build.
        public static string Configuration => _configuration ??= Read(Native.FfmpegConfiguration);

        // "9.0.2", or a git describe like "n9.0.1-84-g946fcce07b" for a checkout
        // between releases. Falls back to the numeric libavutil version for a
        // build that carries no version string at all.
        public static string Version => _version ??= Read(Native.FfmpegVersion);

        // Whether this particular binary may be redistributed under the terms
        // this repository ships under.
        //
        // A question rather than an assertion, because the answer is allowed to
        // be no: the machine this was built on may be a developer's, and a
        // GPL-enabled FFmpeg is the normal thing to find there. What must never
        // happen is shipping one without noticing, so the release path asks
        // this and the test suite asks it whenever FFAUDIO_REQUIRE_LGPL is set.
        //
        // Read as a prefix rather than compared whole: avutil's string is
        // "LGPL version 2.1 or later", and "or later" is a phrase that has
        // changed before.
        public static bool IsRedistributable =>
            License.StartsWith("LGPL", StringComparison.Ordinal);

        private static string? _license;
        private static string? _configuration;
        private static string? _version;

        // NativeText's own retry loop: the façade reports a buffer it could
        // not fill without saying how much it wanted, and a configure line is
        // exactly the string that does not fit in the first 256 bytes.
        private static string Read(NativeText.FillOne fill) => NativeText.ReadString(fill);
    }
}
