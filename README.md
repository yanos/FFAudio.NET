# FFAudio.NET

FFAudio.NET is a small .NET API for decoding audio with FFmpeg. It reads files
or streams and returns interleaved PCM samples, with support for seeking,
metadata, cover art, and source format information.

The native API is defined in [`native/ffaudio.h`](native/ffaudio.h).

## Supported platforms

The managed library targets .NET 8 and .NET 10. The supplied native packages
support:

| Platform | .NET target | Native support |
|---|---|---|
| macOS | `net8.0`, `net10.0` | macOS 11 or later, Arm64 |
| Linux | `net8.0`, `net10.0` | x64 |
| Windows | `net8.0`, `net10.0` | x64 |
| iOS | `net10.0-ios` | iOS 12.2 or later, Arm64 device and simulator |
| Android | `net10.0-android` | API 21; `arm64-v8a`, `armeabi-v7a`, `x86_64` |

The .NET Android workload may require an app minimum version above API 21.
Other operating systems and architectures require a custom native build.

## Format support

FFAudio.NET supports most audio formats that FFmpeg supports, provided the
corresponding decoder and demuxer are present in the native FFmpeg build. The
supplied native packages use a music-focused set that includes MP3, AAC, ALAC,
FLAC, Vorbis, Opus, WavPack, Monkey's Audio, DSD, WAV, AIFF, and compatible
containers.

Support ultimately depends on how FFmpeg was built. Formats backed by GPL or
nonfree components are not included in the supplied LGPL builds; see
[License and redistribution](#license-and-redistribution). A custom build can
select FFmpeg's broader audio set with `--variant full` or add specific FFmpeg
configure flags.

## Install

Add the managed package and the native package for your target platform:

```sh
dotnet add package FFAudio.NET
dotnet add package FFAudio.NET.macOS      # or .Windows, .Linux, .iOS, .Android
```

## Build from source

`dotnet build` only builds the managed library. Build the native library with:

```sh
native/build-all.sh                 # everything supported by this host
native/build-all.sh macos ios       # selected targets
native/build-all.sh --help          # every option
```

macOS and iOS require a Mac, Linux requires a Linux host, and Android requires
an NDK, passed with `--ndk PATH`. On Windows, use PowerShell and MSVC:

```powershell
native/windows/build.ps1
native/windows/build.ps1 -Help
```

Development builds on macOS and Linux use the FFmpeg found through
`pkg-config`. Use `--static` for a self-contained build intended for
distribution:

```sh
native/build-all.sh --static macos
```

Every script under `native/` takes `--help`, which lists its options. Each
option also has an environment variable, shown in the help (`--static` is
`FFAUDIO_STATIC=1`), which is useful in CI.

Native outputs are written to `native/artifacts/<platform>/`.

## Decode a file

`Read` fills the buffer with interleaved samples and returns `0` at the end of
the file.

```csharp
using FFAudio;

using var decoder = Decoder.OpenPath("track.flac", SampleFormat.S16);

var buffer = new byte[16384];
int read;
while ((read = decoder.Read(buffer)) > 0)
    output.Write(buffer.AsSpan(0, read));   // your audio sink, file, etc.
```

Pass a sample rate and channel count to resample and mix the output. Leave
either value at `0` to use the source value.

```csharp
// 48 kHz stereo float output
using var decoder = Decoder.OpenPath(
    "track.mp3",
    SampleFormat.F32,
    sampleRate: 48000,
    channels: 2);
```

## Decode a file into float samples

```csharp
using System.Runtime.InteropServices;

using var decoder = Decoder.OpenPath("track.wav", SampleFormat.F32);

using var pcm = new MemoryStream();
var buffer = new byte[65536];
int read;
while ((read = decoder.Read(buffer)) > 0)
    pcm.Write(buffer, 0, read);

float[] samples = MemoryMarshal.Cast<byte, float>(
    pcm.GetBuffer().AsSpan(0, (int)pcm.Length)).ToArray();

// Interleaved: samples[frame * decoder.Format.Channels + channel]
```

## Inspect the audio

`Format` includes both source information and the PCM format returned by
`Read`.

```csharp
using var decoder = Decoder.OpenPath("track.flac", SampleFormat.S24);
var format = decoder.Format;

Console.WriteLine($"{format.Codec} in {format.Container}");
Console.WriteLine($"{format.SourceSampleRate} Hz, {format.SourceBitDepth}-bit, "
                + $"{format.SourceChannels} channels, {format.Duration}");
Console.WriteLine($"Output: {format.SampleRate} Hz, {format.ChannelLayout}, "
                + $"{format.BytesPerFrame} bytes per frame");
```

`Duration` is `null` when it cannot be determined. `ChannelLayout` describes
the output after any requested channel conversion.

## Read tags and cover art

```csharp
using var decoder = Decoder.OpenPath("track.m4a", SampleFormat.S16);

foreach (var (key, value) in decoder.Tags)
    Console.WriteLine($"{key} = {value}");

if (decoder.TryReadCoverArt() is { } art)
    File.WriteAllBytes($"cover.{art.MimeType.Split('/')[1]}", art.Bytes);
```

Tags are returned as a list because a file may contain the same key more than
once. Cover art is returned in its original encoded form and is `null` when
none is present.

## Decode a stream

Any readable `Stream` can be used. The caller owns the stream unless
`ownsStream` is `true`. A correct `formatHint` avoids probing; an incorrect
hint falls back to format detection.

```csharp
using var decoder = Decoder.OpenStream(
    stream, SampleFormat.F32,
    formatHint: "mp4",
    ownsStream: true,
    logger: logger);       // optional
```

## Seek

The returned position is where decoding resumes, which may be before the
requested time. Streams must be seekable.

```csharp
TimeSpan landed = decoder.Seek(TimeSpan.FromMinutes(2));
```

## Handle errors

```csharp
if (!Decoder.IsAvailable)
{
    // The native library is missing or does not match this platform.
    return;
}

try
{
    using var decoder = Decoder.OpenPath(path, SampleFormat.S16);
    // ...
}
catch (DecodeException e)
{
    Console.WriteLine($"{e.Message} (FFmpeg error {e.Code})");
}
```

## License and redistribution

FFAudio.NET and its native facade are licensed under
[Apache-2.0](LICENSE). FFmpeg is used under
[LGPL-2.1-or-later](NOTICE).

The repository builds FFmpeg without `--enable-gpl` or `--enable-nonfree`, so
the resulting native packages use FFmpeg under the LGPL. If you distribute
these builds, include the required notices and corresponding FFmpeg source,
and preserve the user's ability to replace or relink FFmpeg. See
[`NOTICE`](NOTICE) for details.

To rebuild FFmpeg and the native library with the default LGPL configuration:

```sh
# macOS or Linux shipping build
native/build-all.sh --rebuild-ffmpeg --static macos

# iOS or Android
native/build-all.sh --rebuild-ffmpeg ios
```

Replace `macos` with `linux` or `ios` with `android` as needed. Android also
needs `--ndk PATH`. `--rebuild-ffmpeg` is important because the scripts
otherwise reuse an existing FFmpeg build of the same version and variant.
Each FFmpeg version is kept in its own folder under
`native/<platform>/ffmpeg/prefix/`, so changing `--ffmpeg-version` builds the
new one without `--rebuild-ffmpeg`; changing `--configure-flags` does need it.

You may pass additional FFmpeg configure flags on the command line. Because
GPL and nonfree builds use different redistribution terms, they also require
an explicit acknowledgment:

```sh
# Build FFmpeg under the GPL instead of the LGPL
native/build-all.sh --rebuild-ffmpeg --allow-non-lgpl \
    --configure-flags="--enable-gpl" ios

# Build a nonfree FFmpeg
native/build-all.sh --rebuild-ffmpeg --allow-non-lgpl \
    --configure-flags="--enable-nonfree" ios
```

`--enable-gpl` changes the resulting FFmpeg build from LGPL to GPL. You may
redistribute it only under the GPL and must determine what that means for the
combined native library and application. `--enable-nonfree` allows components
with incompatible licenses; FFmpeg marks the resulting binary as
unredistributable, so it must not be included in a distributed package or
application.

Multiple configure flags can be separated by spaces in `--configure-flags`. The override applies to source builds for
macOS, Linux, iOS, and Android. Windows uses a pinned prebuilt FFmpeg; to use a
different configuration, build FFmpeg separately and pass its prefix to:

```powershell
native/windows/build.ps1 -Prefix C:\path\to\ffmpeg
```

You can inspect the FFmpeg embedded in or loaded by a native binary:

```csharp
Console.WriteLine(FFmpegBuild.Version);
Console.WriteLine(FFmpegBuild.License);
Console.WriteLine(FFmpegBuild.Configuration);

if (!FFmpegBuild.IsRedistributable)
    throw new InvalidOperationException("This FFmpeg build is GPL or nonfree.");
```

`FFmpegBuild.IsRedistributable` means redistributable under this repository's
LGPL terms; a GPL build returns `false` even though GPL redistribution may be
possible under the GPL. You are responsible for confirming that your chosen
configuration and distribution comply with all applicable licenses.
