# Builds ffaudio.dll for Windows, and puts the FFmpeg DLLs it needs
# beside it.
#
# Unlike macOS and Linux there is no FFmpeg on this machine to find and no
# pkg-config to ask, and unlike iOS and Android there is no reason to spend
# half an hour cross-compiling one: FFmpeg publishes usable Windows builds, and
# the LGPL variant of them is configured exactly the way this needs it (no
# --enable-gpl, no --enable-nonfree) and ships the libraries as separate,
# replaceable DLLs, which is the same shape the licence obligation takes on
# every other desktop. So this script downloads one rather than building it.
#
#     native/windows/build.ps1
#     native/windows/build.ps1 -Prefix C:\my\own\ffmpeg   # bring your own
#
# The result lands in native/artifacts/windows/, which is where
# Native.Resolve looks - five DLLs rather than one, since the façade
# imports avformat, avcodec, avutil and swresample. All five go into the
# package's runtimes/win-x64/native/, which is what makes them land beside a
# consumer's own binary.
#
# See ../README.md, whose licensing section applies here as much as anywhere:
# what makes this build distributable is that FFmpeg stays a set of DLLs the
# user can replace, and that this repo carries the source offer.
[CmdletBinding()]
param(
    # An FFmpeg prefix to build against - include/, lib/ and bin/ - instead of
    # the pinned download. For bisecting against a differently-built FFmpeg,
    # the way FFAUDIO_LIBRARY does at run time.
    [string]$Prefix,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here "../..")
$build = Join-Path $here "build"
$out = Join-Path $root "native/artifacts/windows"

# Pinned to a dated autobuild rather than the "latest" tag, whose assets are
# rebuilt daily under the same names: a version floor of FFmpeg 5.1 says what
# the façade needs to compile, and this says what it was last known to compile
# against. The checksum is the whole point of pinning - without it this is a
# script that runs whatever a download gave it.
$release = "autobuild-2026-09-04-14-01"
$asset = "ffmpeg-n8.1.2-50-g1a748fe2cd-win64-lgpl-shared-8.1.zip"
$sha256 = "d4a0db2e182e6d1535a022523d329daf8daff9d69db88d5aa569732005cffa91"

if (-not $Prefix) {
    $downloads = Join-Path $here "ffmpeg"
    $zip = Join-Path $downloads $asset
    $Prefix = Join-Path $downloads ([IO.Path]::GetFileNameWithoutExtension($asset))

    if (-not (Test-Path $Prefix)) {
        New-Item -ItemType Directory -Force -Path $downloads | Out-Null

        if (-not (Test-Path $zip)) {
            $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$release/$asset"
            Write-Host "==> Downloading $asset"
            # Invoke-WebRequest's progress bar makes this download several
            # times slower on a CI runner, where nothing is watching it.
            $previousProgress = $ProgressPreference
            $ProgressPreference = "SilentlyContinue"
            try {
                Invoke-WebRequest -Uri $url -OutFile $zip
            }
            finally {
                $ProgressPreference = $previousProgress
            }
        }

        $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $sha256) {
            Remove-Item $zip -Force
            throw "$asset hashes $actual, not the pinned $sha256 - refusing to build against it"
        }

        Write-Host "==> Unpacking"
        Expand-Archive -Path $zip -DestinationPath $downloads -Force
    }
}

if (-not (Test-Path (Join-Path $Prefix "include/libavformat/avformat.h"))) {
    throw "$Prefix does not look like an FFmpeg prefix: no include/libavformat/avformat.h"
}

Write-Host "==> Building the façade against $Prefix"
cmake -S (Join-Path $here "..") -B $build -A x64 "-DFFAUDIO_PREFIX=$((Resolve-Path $Prefix).Path -replace '\\', '/')"
if ($LASTEXITCODE -ne 0) {
    throw "cmake configure failed"
}

cmake --build $build --config $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "cmake build failed"
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item (Join-Path $build "$Configuration/ffaudio.dll") $out -Force

# The four the façade imports, and only those - the prefix also holds
# avdevice, avfilter and swscale, which nothing here calls. They go beside the
# façade rather than anywhere on PATH because that is where its own loader
# finds them: Native.Resolve loads ffaudio.dll by full path, and
# Windows then searches the directory it came out of for its dependencies.
foreach ($component in @("avformat", "avcodec", "avutil", "swresample")) {
    Get-ChildItem -Path (Join-Path $Prefix "bin/$component-*.dll") | Copy-Item -Destination $out -Force
}

Write-Host "built $out\ffaudio.dll"
Get-ChildItem $out | ForEach-Object { Write-Host ("  " + $_.Name) }

# The same sanity check the macOS and Linux scripts end on: eight exported
# symbols and no more, because a façade that exported FFmpeg's own would be a
# second way to reach it. dumpbin is only on PATH inside a Visual Studio
# developer prompt, so this reports rather than fails when it is missing.
$dumpbin = Get-Command dumpbin -ErrorAction SilentlyContinue
if ($dumpbin) {
    & $dumpbin.Path /exports (Join-Path $out "ffaudio.dll") | Select-String "ffaudio_"
}
else {
    Write-Host "(dumpbin not on PATH - exports unchecked)"
}
