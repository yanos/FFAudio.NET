<#
.SYNOPSIS
Builds ffaudio.dll for Windows into native/artifacts/windows/, with the four
FFmpeg DLLs it imports beside it.

.DESCRIPTION
Downloads a pinned, checksummed LGPL FFmpeg 9.0.2 build and links the facade
against it, unless -Prefix names an FFmpeg of your own. Windows does not build
FFmpeg from source, so the FFmpeg options of the shell scripts (--variant,
--configure-flags and the rest) do not apply here; to change how FFmpeg is
configured, build it yourself and pass its prefix.

.PARAMETER Prefix
An FFmpeg prefix to build against - with include/, lib/ and bin/ - instead of
the pinned download.

.PARAMETER Configuration
The CMake build configuration. Release by default.

.PARAMETER Help
Show this help. --help, -h and -? work too.

.EXAMPLE
native/windows/build.ps1

.EXAMPLE
native/windows/build.ps1 -Prefix C:\my\own\ffmpeg
#>

# Build ffaudio.dll against a pinned, replaceable LGPL FFmpeg distribution.
# Disable positional binding so stray arguments fail instead of becoming Prefix.
[CmdletBinding(PositionalBinding = $false)]
param(
    # Optional FFmpeg prefix containing include/, lib/, and bin/.
    [string]$Prefix,
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

# Pin and checksum the same FFmpeg release used by source builds. The asset is
# mirrored here because upstream daily builds are pruned. When updating it,
# also update ffaudio_ffmpeg_version in ../codec-set.sh.
$release = "ffmpeg-win64-9.0.2"
$asset = "ffmpeg-n9.0.1-84-g946fcce07b-win64-lgpl-shared-9.0.zip"
$sha256 = "a2a50423b631cb51e91c2668c16a4807197c50d1f5fc56dd4516ca188a0d731f"

if (-not $Prefix) {
    $downloads = Join-Path $here "ffmpeg"
    $zip = Join-Path $downloads $asset
    $Prefix = Join-Path $downloads ([IO.Path]::GetFileNameWithoutExtension($asset))

    if (-not (Test-Path $Prefix)) {
        New-Item -ItemType Directory -Force -Path $downloads | Out-Null

        if (-not (Test-Path $zip)) {
            $url = "https://github.com/yanos/FFAudio.NET/releases/download/$release/$asset"
            Write-Host "==> Downloading $asset"
            # Disable the expensive legacy progress renderer in CI.
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
}
else {
    Write-Host "(dumpbin not on PATH - exports unchecked)"
}
