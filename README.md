# FFAudio.NET

A narrow, audio-only façade over FFmpeg's decode libraries, for .NET.

Open a file or a `Stream`, ask what PCM it holds, read interleaved samples,
seek, close — plus the handful of questions every consumer asks about a file
it has just opened: its tags, its cover art, its channel layout, what codec
and container it is, and which FFmpeg is inside the binary. That is the whole
of it: sixteen C functions over ints and byte buffers, and a small managed
binding on top. `native/ffaudio.h` is
the entire interface and is meant to be read in one sitting.

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

## What the file says

Decoding is most of this library, but a caller that cannot ask the façade
what a file *is* reaches past it into FFmpeg, and then has two routes to
FFmpeg to build, ship and keep in step — which is the outcome the façade
exists to prevent. So the cheap questions are answered here:

```csharp
foreach (var (key, value) in decoder.Tags)
    Console.WriteLine($"{key} = {value}");        // "title", "artist", ...

if (decoder.TryReadCoverArt() is { } art)
    File.WriteAllBytes($"cover{Path.GetExtension(art.MimeType)}", art.Bytes);

Console.WriteLine(decoder.Format.ChannelLayout);  // "stereo", "5.1(side)"
Console.WriteLine(decoder.Format.Codec);          // "flac"
Console.WriteLine(decoder.Format.Container);      // "flac"
```

Four things worth knowing about that surface:

- **Tags are a list, not a dictionary.** A track really can carry two `ARTIST`
  comments, and which one wins is the caller's policy rather than this
  library's. Container-level tags come first, then the audio stream's own,
  because where a format puts them is a property of the format — ID3 on the
  container for an MP3, Vorbis comments on the stream for an Ogg.
- **Cover art is the container's own bytes**, neither decoded nor rescaled:
  that is a decision belonging to whatever is going to draw it. `null` when
  there is none, which is the ordinary case and not a failure. It is a method
  rather than a property because album art is routinely megabytes.
- **`ChannelLayout` describes what `Read` delivers**, after any requested
  down- or up-mix — not what the source held. `Channels` is a number, and a
  number cannot say which channel is which.
- **None of it allocates on the native side.** Strings and image bytes go into
  caller-owned buffers; the managed binding retries with a larger one when the
  façade reports it could not fit.

These were additions rather than changes, so `FFAUDIO_ABI_VERSION` is still 1.
Nothing moved: a library built before them fails at the first call to one with
a missing symbol rather than an ABI mismatch, and while nothing has shipped
that is the whole of what a version bump would have bought.

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
| `FFAudio.NET.macOS` | `runtimes/osx-arm64/native/libffaudio.dylib` |
| `FFAudio.NET.Linux` | `runtimes/linux-x64/native/libffaudio.so` |
| `FFAudio.NET.Windows` | `runtimes/win-x64/native/` — the façade plus the four FFmpeg DLLs it imports |
| `FFAudio.NET.iOS` | both `ffaudio.framework` slices, and a `.targets` injecting `NativeReference` |
| `FFAudio.NET.Android` | `libffaudio.so` for three ABIs, and a `.targets` injecting `AndroidNativeLibrary` |

Reference the binding plus whichever payload the app actually ships:

```
dotnet add package FFAudio.NET
dotnet add package FFAudio.NET.macOS
```

Nothing depends on anything else here. The binding does not drag a native in,
because which native an app wants is the app's decision — a Windows FFmpeg is
~70MB where an iOS slice is 1.9MB, and a consumer building their own FFmpeg
wants neither.

Desktop is a plain `runtimes/<rid>/native/` payload, which the SDK resolves for
the running RID with no help from us. Neither mobile head does that, so those
two are `buildTransitive/` targets instead: an iOS framework is a directory and
has to arrive as a `NativeReference`, and an Android `.so` reaches an APK as an
`AndroidNativeLibrary` with an `<Abi>`. `buildTransitive` rather than `build` so
the injection survives a project reference, which is the normal arrangement —
the app references its own shared library, and that is what took the
dependency.

One RID per desktop package, and each is the one the CI runner that built it
actually is: `osx-arm64`, `linux-x64`, `win-x64`. An Intel Mac or an arm64
Linux box needs a build that nothing here has a machine to make, so those RIDs
are absent rather than claimed.

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

### What it can decode: `slim` and `full`

The phone builds are `--disable-everything` plus an explicit list, because a
static FFmpeg is linked into the app bundle and all of FFmpeg is 70MB of
avcodec for a façade that calls four functions. That list is in
`native/codec-set.sh`, and there are two of them:

| `FFAUDIO_VARIANT` | What it decodes | Size |
|---|---|---|
| `slim` (default) | A music library: MP3, AAC/ALAC, FLAC, Vorbis, Opus, WavPack, APE, DSD, the PCM family — 22 decoders, 12 demuxers | iOS slice ~1.9MB, Android ABI ~1.3MB |
| `full` | Every audio decoder FFmpeg has and every demuxer it has — 201 decoders, 350 demuxers | Untested; expect several times that |

```
FFAUDIO_VARIANT=full native/ios/build-ffmpeg.sh && FFAUDIO_VARIANT=full native/ios/build.sh
```

`full` is deliberately not "drop `--disable-everything`": that enables the
whole video decoder set for a façade that hands back PCM and cannot express a
frame. It is the audio half of FFmpeg's own decoder list, read out of the
source tree about to be configured — `libavcodec/allcodecs.c` groups its
declarations under `/* audio codecs */`, `/* PCM codecs */`, `/* DPCM codecs
*/` and `/* ADPCM codecs */`, and everything between those and `/* subtitles
*/` is what produces samples. If a future FFmpeg drops those markers the build
fails with an empty list rather than quietly configuring no decoders at all.
Demuxers are all of them: a demuxer is a table and a probe function, and the
file a caller hands over is theirs to name rather than ours to predict.
configure warns that it dropped the handful needing network or an external
library, which is the intended outcome.

This is a property of the *static* builds only. macOS and Linux link the
system FFmpeg through `pkg-config` and decode whatever that build decodes;
Windows decodes whatever the pinned download has.

Both variants keep the prefix `build-ffmpeg.sh` produced, under
`ffmpeg/prefix/<variant>/`, so switching between them is a relink rather than
another forty minutes. They build to the same artifact path under the same
name, so the artifact tree carries a `VARIANT` file saying which one is in it.

Neither can introduce a licence problem — every name in the lists is a decoder
or a demuxer rather than a configure switch — and the build asserts it anyway:
`ffaudio_assert_lgpl` reads the generated `config.h` after configure and stops
if `CONFIG_GPL` or `CONFIG_NONFREE` came back set. See **Licensing**.

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

### Asking the binary rather than the build

A configure line lives in a script, a variant lives in an environment variable,
and neither travels with a `.dylib` that has been copied into a NuGet, embedded
in an app bundle and shipped. avutil travels with it:

```csharp
FFmpegBuild.Version           // "7.1.1", or a git describe
FFmpegBuild.License           // "LGPL version 2.1 or later" — or "GPL version 2 or later"
FFmpegBuild.Configuration     // the configure line, verbatim
FFmpegBuild.IsRedistributable // whether this particular binary may be shipped
```

`Configuration` is the half of the LGPL's relink route that the binary can
state for itself: on a phone, where FFmpeg is linked in, it is the exact
arguments needed to reproduce what is inside it.

`IsRedistributable` is a question rather than an assertion, because the answer
is allowed to be no — a developer's machine is expected to fail it. What must
never happen is shipping one without noticing, so
`FFmpegBuildTests.A_shipping_build_carries_an_lgpl_only_ffmpeg` asserts it
whenever `FFAUDIO_REQUIRE_LGPL` is set, and skips otherwise. CI sets it on
Windows only: that FFmpeg is a pinned LGPL build this repo chose, where
Linux's and macOS's come from apt and brew. So the gate doubles as a check
that the pinned Windows asset is still what its name says.

The build asserts the same thing from the other side, where it can:
`ffaudio_assert_lgpl` reads the generated `config.h` after configure and stops
a mobile build whose `CONFIG_GPL` or `CONFIG_NONFREE` came back set. Two
checks because they fail at different times — one when the FFmpeg is built,
one when a binary that already exists is asked.

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

## Device checks

The same question — *does this platform turn a file into the right samples?* —
asked on a phone rather than on a developer's Mac.

```
dotnet test --filter FullyQualifiedName~DeviceChecksTests   # here
scripts/ios-device-checks.sh                                # iOS Simulator
scripts/android-device-checks.sh                            # Android emulator
```

Three things this library depends on are green at link time and fatal at
launch, and not one of them can fail on a desktop:

- **`Native.Resolve`'s iOS branch**, which loads the façade by hand out of
  `Frameworks/ffaudio.framework` because .NET-for-iOS resolves a P/Invoke by
  `dlopen`-ing the `DllImport` string, and that matches nothing there. The
  resolver is registered from a `[ModuleInitializer]` rather than a static
  constructor because Mono resolves the library for a stub *before* running
  the declaring type's cctor — so it was once registered by the very call that
  had already thrown `DllNotFoundException`. The library loaded fine when
  asked directly; nothing was ever asking.
- **The read and seek trampolines** `OpenStream` hands FFmpeg, compiled ahead
  of time on a phone rather than JITted. A mistake there is a crash at the
  first callback, not a compile error.
- **`libffaudio.so` loading out of an APK**, for whichever ABI the hardware
  turns out to be.

So `checks/FFAudio.Checks` carries no test framework — xUnit needs a host
process to discover and run it, and on iOS and Android what runs is an app. A
check is a method that throws, the runner is a loop, and
`checks/FFAudio.Checks.iOS` and `checks/FFAudio.Checks.Android` are the
smallest apps that can call `RunAll` and write down what came back: no audio
output, no network, and no UI beyond a text view. Every dependency they do not
have is one that cannot explain a failure.

They are written to be twins, reporting the same `FFAUDIO-CHECK` /
`FFAUDIO-CHECKS` lines under the same prefixes, because two platform runs are
only worth comparing when the one difference between them is the platform.
Each writes its transcript to a **file** in its own container rather than to
the obvious console: `Console.WriteLine` from a .NET iOS app does not reliably
reach `simctl launch --console-pty`, and logcat is a ring buffer shared with
the whole system, so a chatty emulator drops lines out of the middle of a long
one. A run that decoded everything and reported two thirds of its tally is
indistinguishable from a failing one.

The checks also run on every desktop, as `DeviceChecksTests`. A check that is
only ever exercised on a phone is one nobody can trust, because a failure
there would be ambiguous between the platform and the check itself — running
them here first means a red simulator run says something about the simulator.

Which ABI an Android run exercises is a property of the host: `arm64-v8a` on a
developer's Mac, `x86_64` on a CI runner. Both `.so`s are packaged, along with
`armeabi-v7a`, so between the two places these run, two of the three get
exercised.

## Versioning and releasing

There is no version number written down anywhere. MinVer derives it from git
tags on every build — local, CI and release alike — and the SDK stamps it into
the assembly and into the nuspec `dotnet pack` generates.

| Where you are | What you get |
|---|---|
| An untagged commit | `0.1.0-alpha.0.<height>` — a pre-release, which is what an unreleased commit is |
| `v1.2.3` | `1.2.3`, an official release |
| `v1.2.3-rc.1` | `1.2.3-rc.1`, which NuGet shows as a pre-release |

So cutting a release is two commands and no edit:

```
git tag v1.2.3
git push origin v1.2.3
```

`.github/workflows/ci.yml` picks the tag up. It is one workflow rather than
two because `needs:` and artifacts only reach across jobs of the same run: a
separate publish workflow firing on the same tag could not depend on the tests
or download what they built, only repeat the work and hope the second answer
matched the first. So the chain is `test` on three desktops → `pack` →
`publish`, and the last of those is gated on the tag with
`if: startsWith(github.ref, 'refs/tags/v')`.

The publish job has no checkout and no build step. It downloads the package
`pack` produced and pushes those exact bytes to nuget.org with the
`NUGET_API_KEY` repository secret — so what gets published is the artifact of
a green run, not a second compilation of the same commit that nobody looked
at.

The version it checks comes from `pack`, which asks MinVer and hands the
answer down as a job output; `publish` compares that against the tag before
pushing. Asking MinVer is the computation the build already did, and a second
way of working the version out is a second way to be wrong — a shallow
checkout is enough to make one, which is why every checkout here is
`fetch-depth: 0`.

### Trying the package before publishing it

A folder is a valid NuGet feed, so the package can be consumed for real
without anything leaving the machine:

```
dotnet pack src/FFAudio.NET/FFAudio.NET.csproj -c Release -o /tmp/nupkg
dotnet nuget push /tmp/nupkg/FFAudio.NET.*.nupkg --source /tmp/localfeed
```

Then, in a throwaway consumer project, a `nuget.config` that points at it:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="/tmp/localfeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="packages" />
  </config>
</configuration>
```

Both halves of that matter. `<clear />` is what stops a typo in the version
quietly restoring some other package from nuget.org and calling it a pass. The
private `globalPackagesFolder` is the one that actually bites: a package is
cached under its exact version, so re-packing `0.1.0-alpha.0` with different
contents and restoring again gets you the *first* one back, out of
`~/.nuget/packages`, forever. Giving the consumer its own cache means deleting
a directory is enough to start over.

What the consumer will not get from the package today is a decoder — that is
the per-platform native package under **Packaging**, which does not exist yet.
Until it does, point `FFAUDIO_LIBRARY` at a built artifact or drop it beside
the consumer's own binary, which is the same place a `runtimes/<rid>/native/`
payload would land:

```
FFAUDIO_LIBRARY=…/native/artifacts/macos/libffaudio.dylib dotnet run
```

Without one, `Decoder.OpenPath` throws `DllNotFoundException` out of
`EnsureAbi` — the managed half of the package is fine, and there is nothing
for it to call.

`.github/workflows/ci.yml` builds the façade and runs the suite against it on
all three desktops on every push, runs the device checks on an iOS Simulator
and an Android emulator alongside them, and packs once behind all five — so a commit that stopped
compiling on Windows, or stopped cross-compiling for a phone, produces no
package at all. That pre-release package is a downloadable artifact of the
run, so a commit can be tried before anyone decides to tag it, and packaging
never breaks for the first time during a release.

The phones are not only cross-compiled: CI boots an iOS Simulator and an
Android emulator and runs the checks on them, the same way `scripts/` does on
a developer's Mac. See **Device checks** for what that catches that a
cross-compile cannot.

The expensive half of a mobile job is FFmpeg itself: `build-ffmpeg.sh`
cross-compiles it from source, tens of minutes across two iOS slices or three
Android ABIs. Both scripts leave an existing prefix alone, so CI caches
`native/<platform>/ffmpeg/prefix` keyed on the script's own hash — the FFmpeg
version and the decoder list both live in that script, so changing either
misses the cache, and every other run is just the façade's single translation
unit.

Every build job uploads what it produced: `ffaudio-Linux`, `ffaudio-macOS`,
`ffaudio-Windows`, `ffaudio-iOS`, `ffaudio-Android`. Nothing downstream
consumes them yet — the package still carries no decoder — but a built native
from every platform, at one commit, in one run, is the half of a
`runtimes/<rid>/native/` payload that has to exist before the other half is
worth writing.

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

`FFmpegBuild` is new and, like everything else here, additive: three
functions, no struct moved, no signature changed, so `FFAUDIO_ABI_VERSION`
stays 1. Unlike the metadata surface it is not called on any path a decode
takes, so an older façade paired with this binding fails only if something
asks — which is the difference between a library that will not open a file
and one whose licence question throws.

`full` has never been built for a phone: its configure line was verified by
configuring FFmpeg 7.1.1 with it on macOS — 201 audio decoders, 350 demuxers,
`CONFIG_GPL 0` — and no slice or ABI has been linked from it. The DSD
decoders new to `slim` are in the same position: `dsf` had been in the demuxer
list with no `dsd_*` decoder behind it since the list was written, so a `.dsf`
demuxed and then failed to find a decoder, and the fix is a configure line
that no phone has yet run.

The metadata surface — tags, cover art, channel layout, codec and container
names — now runs green on all three desktops and on both phone simulators,
which it had not when it was written: run 34436202607 is the first time it was
built anywhere but macOS.

The per-platform native packages now exist and CI packs all six, out of the
natives the test and checks jobs built rather than a rebuild — the same
argument the publish job makes for pushing pack's exact bytes. A payload
package that packs nothing is the failure this invites, so each one names a
file that must exist and stops the build if it does not.

Verified end to end for macOS only: a scratch console app referencing
`FFAudio.NET` and `FFAudio.NET.macOS` from a folder feed decodes a FLAC with
no other setup. The mobile two have been packed and their layout checked, but
no phone project has consumed one — that is the first thing to do with them.

Nothing is published to NuGet yet: the workflow and the versioning are in
place, but no `v*` tag has been cut and no `NUGET_API_KEY` secret has been
set.
