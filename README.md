# FFAudio.NET

A narrow, audio-only façade over FFmpeg's decode libraries, for .NET.

Open a file or a `Stream`, ask what PCM it holds, read interleaved samples,
seek, close. That is the whole of it — eight C functions over ints and byte
buffers, and a small managed binding on top. `native/ffaudio.h` is the entire
interface and is meant to be read in one sitting.

```csharp
using FFAudio;

using var decoder = Decoder.OpenPath("track.flac", SampleFormat.S24);

Console.WriteLine($"{decoder.Format.SampleRate}Hz "
                + $"{decoder.Format.Channels}ch "
                + $"{decoder.Format.SourceBitDepth}-bit, {decoder.Format.Duration}");

var buffer = new byte[16384];
int read;
while ((read = decoder.Read(buffer)) > 0)
    sink.Write(buffer.AsSpan(0, read));
```

A stream works the same way, and the `Stream` is yours:

```csharp
using var decoder = Decoder.OpenStream(await http.GetSeekableStreamAsync(url),
                                       SampleFormat.F32,
                                       formatHint: "mp4");

var landed = decoder.Seek(TimeSpan.FromMinutes(2));   // at or before the request
```

## Why this rather than the alternatives

There is no shortage of FFmpeg on NuGet. There is a shortage of this shape:

- **Process wrappers** (`FFMpegCore`, `Xabe.FFmpeg`) shell out to an `ffmpeg`
  executable. Excellent at what they do, and unavailable on a phone — iOS
  forbids spawning arbitrary executables outright. They are also the wrong
  shape for playback, which needs a pull-based PCM API with sample-accurate
  seek rather than a command line that produces a file.
- **Whole-ABI generated bindings** (`FFmpeg.AutoGen`, `Sdcb.FFmpeg`) are real
  in-process bindings and ship no natives at all. They hand you the entire
  FFmpeg ABI in `unsafe` C# and leave finding a libavcodec to you.

So the scarce part here is not the binding — anyone can P/Invoke
`avcodec_send_packet`. It is a working, LGPL-clean, statically linked,
audio-only FFmpeg **for iOS and Android**, with the export list narrowed to the
functions actually called. That is `native/ios/` and `native/android/`, and it
is the reason this repo exists.

## Sample formats

All four, because a library does not get to decide its caller's sink.

| | Delivered as |
|---|---|
| `SampleFormat.S16` | 2 bytes/sample, interleaved |
| `SampleFormat.S24` | **packed** 3-byte little-endian |
| `SampleFormat.S32` | 4 bytes/sample |
| `SampleFormat.F32` | 4-byte float |

S24 is the odd one out and the reason for the whole thing. swresample cannot
produce packed 24-bit, so the façade packs it from S32 by dropping the low
byte — lossless, because FFmpeg carries 24-bit PCM left-aligned in a 32-bit
container. It is also what `ma_format_s24` and most hardware sinks take. The
alternative, in practice, is a decode path that narrows a 24-bit source to 16
bits and never says so: LibVLC 3.0.x's `amem` seam does exactly that, on every
platform, whatever format is requested, which is the measurement this library
started from. `DecoderTests`' first two tests decode the same 24-bit fixture
twice and show the low byte surviving one way and gone the other.

`sampleRate` and `channels` of `0` ask for the source's own — a bit-perfect
open, with no conversion at all.

## Packaging

Two tiers, which is the arrangement VideoLAN uses for LibVLC and for the same
reason: a Windows FFmpeg is ~70MB where an iOS slice is 1.9MB and an Android
ABI 1.3MB, and nobody should pay for both.

| Package | Holds |
|---|---|
| `FFAudio.NET` | the managed binding — this is the one you reference |
| `FFAudio.NET.Windows` / `.Linux` / `.macOS` | `runtimes/<rid>/native/` payloads |
| `FFAudio.NET.iOS` / `.Android` | targets injecting `NativeReference` / `AndroidNativeLibrary` |

Neither mobile head resolves a native out of `runtimes/`, which is why those
two are targets rather than payloads.

## Building the natives

They are built, not restored. Nothing in `dotnet build` compiles
`native/ffaudio.c`.

```
native/build-all.sh                 # everything this host can build
native/build-all.sh macos ios       # just these
```

It is not a cross-compiler: a façade needs an FFmpeg for its target, and where
that comes from differs per platform. A Mac gets macOS, iOS and — with
`ANDROID_NDK_HOME` set — Android; a Linux box gets Linux and Android; Windows
is PowerShell and MSVC and is not reachable from a shell here at all. Getting
all five means running it on more than one machine, which is what CI is for. A
skip is not a failure, and everything asked for is attempted even if an earlier
one broke.

Everything lands in `native/artifacts/<platform>/`, which is where
`Native.Resolve` looks when walking up from a build output directory — so the
tests run against whatever was last built, with nothing to copy. Or point
`FFAUDIO_LIBRARY` at a file to override it entirely, which is how to bisect
against a differently-built FFmpeg without a rebuild.

### macOS and Linux

```
native/macos/build.sh
sudo apt-get install -y libavformat-dev libavcodec-dev libavutil-dev libswresample-dev
native/linux/build.sh
```

One `CMakeLists.txt`, FFmpeg found through `pkg-config`. The version floor is
FFmpeg 5.1 — where `AVChannelLayout` and `swr_alloc_set_opts2` arrived, the
newest APIs `ffaudio.c` uses. It was briefly 7.x, which was not a requirement
but the version of the machine it was first built on, and it kept the Linux
build from finding Ubuntu 24.04's FFmpeg 6 at all.

### Windows

```
native/windows/build.ps1
native/windows/build.ps1 -Prefix C:\my\own\ffmpeg
```

The one platform with nothing to find: no distro package, no MacPorts, no
pkg-config to ask. It is also the one platform where cross-compiling FFmpeg
buys nothing, because FFmpeg publishes Windows builds whose LGPL variant is
already configured correctly and ships the libraries as separate, replaceable
DLLs. So the script downloads a pinned, checksummed BtbN autobuild and
`CMakeLists.txt` finds it through `FFAUDIO_PREFIX` rather than pkg-config.

Five DLLs come out rather than one: `ffaudio.dll` imports avformat, avcodec,
avutil and swresample, so those four are copied beside it. avdevice, avfilter
and swscale are in the download and are deliberately not linked — an unused
import is a DLL that would then have to be shipped and kept replaceable.

### iOS

```
native/ios/build-ffmpeg.sh   # slow: cross-compiles FFmpeg itself, both slices
native/ios/build.sh          # wraps it in ffaudio.framework
```

FFmpeg is built from the release tarball for device arm64 and Apple Silicon
simulator arm64, and then linked *into* the façade: one framework per slice
rather than five libraries, which is what `-DFFAUDIO_STATIC` means for mobile.
About 1.9MB each. Dynamic rather than static framework, because a
P/Invoke-only symbol reference gets dead-stripped out of a static `.a` on iOS
unless `ForceLoad` is set.

.NET-for-iOS cannot resolve a `DllImport` string to a binary nested inside an
embedded framework — it dlopens the string, which matches nothing, even though
the app's own load commands name the framework. `Native.Resolve` has the branch
that fixes this, so no consumer has to discover it.

### Android

```
ANDROID_NDK_HOME=~/Library/Android/sdk/ndk/<version> native/android/build-ffmpeg.sh
ANDROID_NDK_HOME=~/Library/Android/sdk/ndk/<version> native/android/build.sh
```

The same two-step shape with the NDK's clang in place of Xcode's:
`arm64-v8a`, `armeabi-v7a` and `x86_64`, 1.3–1.9MB per ABI. `x86_64` is built
`--disable-x86asm`, since FFmpeg's x86 assembly wants a nasm that a Mac has no
reason to have and the emulator ABI exists so the app runs, not so anyone
listens on it.

No resolver branch is needed here: Android's loader finds `libffaudio.so` in
the APK from the `DllImport("ffaudio")` string alone.

### Two details that are load-bearing on both phones

**The configure line is where the LGPL obligation is actually met.** A phone
links FFmpeg statically, so unlike desktop there is no distro build to blame or
replace. No `--enable-gpl`, no `--enable-nonfree`, and `--disable-everything`
plus an explicit list. The generated `config.h` says `CONFIG_GPL 0`, which is
the thing to check after any change.

**The export list.** `CMAKE_C_VISIBILITY_PRESET hidden` cannot reach inside a
static archive, so without `-exported_symbols_list` (Apple) or an ELF version
script (Android) the binary would re-export FFmpeg's entire ABI — precisely the
second route to FFmpeg this façade exists in order not to have. Both are derived
from the `FFAUDIO_API` lines in the header, so the ABI is described once, and
both scripts end by printing anything else that got out. Android lists the
*dynamic* table specifically: the strip step takes the symtab with it, so a
plain `nm` reads "no symbols" and would pass whatever it was handed.

Also `--disable-network` on both, because the façade never lets FFmpeg open a
URL. A streamed track arrives through the façade's own AVIO callbacks over a
`Stream` the caller supplies, which keeps authentication, range probing and
retry policy in the caller's own HTTP stack rather than duplicated inside
FFmpeg's.

## Licensing

The façade and the managed binding are **Apache-2.0** (see `LICENSE`).

FFmpeg is **LGPL** and may be linked only as such. Any build that ships must be
configured without `--enable-gpl` and without `--enable-nonfree`, must carry the
corresponding source offer, and must keep the FFmpeg libraries replaceable —
dynamically linked on desktop; on mobile, where they are linked in, an
equivalent relink route has to be offered. See `NOTICE`.

**A MacPorts or Homebrew FFmpeg is not a shipping build.** Both enable GPL
components by default. They are fine for development and for running the tests;
they cannot be packaged. Point `PKG_CONFIG_LIBDIR` at an LGPL-only prefix for
anything that ships. No decoder here needs a GPL component — the GPL parts of a
distro build are filters and encoders this façade never touches.

## Tests

```
dotnet test                                          # needs a built native
dotnet test --filter "Category!=RequiresNative"      # without one
```

They decode real files through real FFmpeg. There are no mocks in here worth
having: the claims are about bytes.

## Debugging

`-DFFAUDIO_ASAN=ON` builds with AddressSanitizer, worth doing after any change
to the buffer management in `ffaudio_decoder_read` — the scratch/pending pair
and the S24 packing are where a mistake corrupts the heap rather than fails a
test.

## Status

| Platform | Artifact | Status |
|---|---|---|
| macOS | `libffaudio.dylib` | Built against MacPorts FFmpeg; in daily real listening |
| Linux | `libffaudio.so` | Built on CI; never built on a Linux machine by hand |
| Windows | `ffaudio.dll` | Built on CI against a pinned LGPL download; never listened to on Windows |
| iOS | `ffaudio.framework` per slice | Built; decode checks pass on the simulator and on a physical device |
| Android | `libffaudio.so` per ABI | Built for all three ABIs; decode checks pass on an emulator |

Nothing is published to NuGet yet.
