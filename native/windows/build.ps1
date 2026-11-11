<#
.SYNOPSIS
Builds ffaudio.dll for Windows into native/artifacts/windows/, with the four
FFmpeg DLLs it imports beside it.

.DESCRIPTION
Builds a shared LGPL FFmpeg from source with MSVC - the release, version and
component set of native/codec-set.sh, as every other platform does - by
running native/windows/build-ffmpeg.sh in MSYS2's bash, then links the façade
against it. -Prefix names an FFmpeg of your own instead.

Needs Visual Studio (or its Build Tools) with the C++ workload, CMake, and
MSYS2 with make, diffutils and nasm:
    C:\msys64\usr\bin\bash.exe -lc "pacman -S --needed --noconfirm make diffutils nasm"
A Visual Studio developer environment is entered for you when cl.exe is not
already on PATH.

.PARAMETER Prefix
An FFmpeg prefix to build against - with include/, and import libraries and
DLLs in lib/ or bin/ - instead of building one.

.PARAMETER Variant
Which decoders the FFmpeg built from source gets: slim (the default) is a
music library, full is every audio decoder. [FFAUDIO_VARIANT]

.PARAMETER FFmpegVersion
The FFmpeg release to build (default: codec-set.sh's). [FFAUDIO_FFMPEG_VERSION]

.PARAMETER ConfigureFlags
Extra FFmpeg configure flags, space-separated. [FFAUDIO_FFMPEG_CONFIGURE_FLAGS]

.PARAMETER RebuildFFmpeg
Rebuild FFmpeg even though a build of it already exists. Needed after
changing -ConfigureFlags. [FFAUDIO_REBUILD_FFMPEG]

.PARAMETER AllowNonLgpl
Allow an FFmpeg that configures as GPL or nonfree. See the licensing section
of README.md. [FFAUDIO_ALLOW_NON_LGPL]

.PARAMETER Msys2
Where MSYS2 is installed (default C:\msys64). [FFAUDIO_MSYS2]

.PARAMETER Configuration
The CMake build configuration. Release by default.

.PARAMETER Help
Show this help. --help, -h and -? work too.

.EXAMPLE
native/windows/build.ps1

.EXAMPLE
native/windows/build.ps1 -Prefix C:\my\own\ffmpeg
#>

# Build ffaudio.dll against a shared LGPL FFmpeg built from source here.
# Disable positional binding so stray arguments fail instead of becoming Prefix.
[CmdletBinding(PositionalBinding = $false)]
param(
    # Optional FFmpeg prefix containing include/ and lib/ or bin/.
    [string]$Prefix,
    [ValidateSet("slim", "full")]
    [string]$Variant,
    [string]$FFmpegVersion,
    [string]$ConfigureFlags,
    [switch]$RebuildFFmpeg,
    [switch]$AllowNonLgpl,
    [string]$Msys2,
    [string]$Configuration = "Release",
    [switch]$Help,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

if ($Help -or ($Rest | Where-Object { $_ -in "--help", "-help", "/?" })) {
    Get-Help $PSCommandPath -Detailed
    exit 0
}
if ($Rest) {
    Write-Error "Unknown argument(s): $($Rest -join ' '). Run native/windows/build.ps1 -Help for the options." -ErrorAction Continue
    exit 2
}

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here "../..")
$build = Join-Path $here "build"
$out = Join-Path $root "native/artifacts/windows"

# MSVC on PATH, for FFmpeg's configure (CMake finds Visual Studio itself).
function Enter-Msvc {
    if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
        return
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "No cl.exe on PATH and no vswhere.exe to find one - install Visual Studio or its Build Tools, with the C++ workload"
    }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw "No Visual Studio with the x64 C++ tools - install the C++ workload"
    }
    Write-Host "==> Entering the Visual Studio developer environment ($vs)"
    Import-Module (Join-Path $vs "Common7\Tools\Microsoft.VisualStudio.DevShell.dll")
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64" | Out-Null
}

if (-not $Prefix) {
    # Hand the options to build-ffmpeg.sh the way the shell scripts hand
    # them to each other: as their FFAUDIO_* variables.
    if ($Variant) {
        $env:FFAUDIO_VARIANT = $Variant
    }
    if ($FFmpegVersion) {
        $env:FFAUDIO_FFMPEG_VERSION = $FFmpegVersion
    }
    if ($ConfigureFlags) {
        $env:FFAUDIO_FFMPEG_CONFIGURE_FLAGS = $ConfigureFlags
    }
    if ($RebuildFFmpeg) {
        $env:FFAUDIO_REBUILD_FFMPEG = "1"
    }
    if ($AllowNonLgpl) {
        $env:FFAUDIO_ALLOW_NON_LGPL = "1"
    }

    if (-not $Msys2) {
        $Msys2 = $env:FFAUDIO_MSYS2
    }
    if (-not $Msys2) {
        $Msys2 = "C:\msys64"
    }
    $bash = Join-Path $Msys2 "usr\bin\bash.exe"
    if (-not (Test-Path $bash)) {
        throw "No MSYS2 bash at $bash - install MSYS2 (msys2.org), or pass -Msys2 with where it is"
    }

    Enter-Msvc

    # inherit: keep the developer environment's PATH, so configure finds cl.
    # CHERE_INVOKING: start the login shell here rather than in ~.
    $env:MSYS2_PATH_TYPE = "inherit"
    $env:CHERE_INVOKING = "1"
    $env:MSYSTEM = "MSYS"
    Write-Host "==> Building FFmpeg (native/windows/build-ffmpeg.sh)"
    Push-Location $root
    try {
        & $bash -lc "native/windows/build-ffmpeg.sh"
        if ($LASTEXITCODE -ne 0) {
            throw "build-ffmpeg.sh failed"
        }
    }
    finally {
        Pop-Location
    }

    # Where build-ffmpeg.sh put it: codec-set.sh's ffaudio_prefix_root.
    $version = $env:FFAUDIO_FFMPEG_VERSION
    if (-not $version) {
        $codecSet = Get-Content (Join-Path $here "../codec-set.sh") -Raw
        if ($codecSet -notmatch 'ffaudio_ffmpeg_version="\$\{FFAUDIO_FFMPEG_VERSION:-([^}]+)\}"') {
            throw "Could not read the FFmpeg version out of codec-set.sh - its ffaudio_ffmpeg_version line must have changed"
        }
        $version = $Matches[1]
    }
    $variantName = $env:FFAUDIO_VARIANT
    if (-not $variantName) {
        $variantName = "slim"
    }
    $Prefix = Join-Path $here "ffmpeg/prefix/$version/$variantName/x64"
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

# Copy only imported FFmpeg DLLs beside the façade for loader discovery.
foreach ($component in @("avformat", "avcodec", "avutil", "swresample")) {
    Get-ChildItem -Path (Join-Path $Prefix "bin/$component-*.dll") | Copy-Item -Destination $out -Force
}

Write-Host "built $out\ffaudio.dll"
Get-ChildItem $out | ForEach-Object { Write-Host ("  " + $_.Name) }

# Show exports when dumpbin is available in the developer environment.
$dumpbin = Get-Command dumpbin -ErrorAction SilentlyContinue
if ($dumpbin) {
    & $dumpbin.Path /exports (Join-Path $out "ffaudio.dll") | Select-String "ffaudio_"

    # What each DLL loads: FFmpeg's own and system DLLs only, so nothing from
    # the build machine travels with them.
    Get-ChildItem -Path (Join-Path $out "*.dll") | ForEach-Object {
        Write-Host "=== $($_.Name) needs ==="
        & $dumpbin.Path /nologo /dependents $_.FullName | Select-String '^\s+\S+\.dll$' | ForEach-Object { Write-Host ("  " + $_.Line.Trim()) }
    }
}
else {
    Write-Host "(dumpbin not on PATH - exports unchecked)"
}
