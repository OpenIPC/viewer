<#
.SYNOPSIS
  Downloads the FFmpeg shared build (LGPL) for Windows x64 and places its DLLs
  + ffmpeg.exe in runtimes/win-x64/native/. ffmpeg.exe is what the recorder and
  clip-exporter shell out to. Everything is git-ignored — every dev runs this once.

.NOTES
  Source: BtbN/FFmpeg-Builds GitHub releases (https://github.com/BtbN/FFmpeg-Builds).
  Version pin: n7.1 (matches FFmpeg.AutoGen 7.1.x bindings), taken from a dated
  release rather than the rolling `latest` tag - `latest` only keeps the branches
  BtbN currently builds, and n7.1 was dropped from it in August 2026 (master /
  n8.1 / n9.0 remain), which 404'd this script. Only month-end dated tags are
  kept long-term; mid-month ones get pruned (that 404'd the 2026-08-16 pin), so
  this uses 2026-07-31, the last month-end tag that still carries n7.1. The
  SHA-256 below turns a swapped or truncated archive into a failure here
  instead of at runtime.
  Build flavor: lgpl-shared (no GPL components; safe to redistribute alongside
  a closed-source app provided DLLs remain replaceable).
#>
[CmdletBinding()]
param(
    [string]$FfmpegRelease  = "autobuild-2026-07-31-14-10",
    [string]$AssetName      = "ffmpeg-n7.1.5-12-g1fdbca85aa-win64-lgpl-shared-7.1.zip",
    # A different release/asset needs its own hash; pass an empty string to skip
    # the check rather than have every override fail.
    [string]$ExpectedSha256 = "0f376f96fb38554ccefb1b2ae9c7c6a7b351f0e60a372b38262c320e8392c5d0",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$nativeDir  = Join-Path $repoRoot "runtimes/win-x64/native"
$cacheDir   = Join-Path $repoRoot ".cache/ffmpeg"
$zipPath    = Join-Path $cacheDir $AssetName
$extractDir = Join-Path $cacheDir ($AssetName -replace "\.zip$","")

$downloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$FfmpegRelease/$AssetName"

# Required runtime DLLs only — header/license files don't ship.
$requiredDlls = @(
    "avcodec-*.dll",
    "avformat-*.dll",
    "avutil-*.dll",
    "swscale-*.dll",
    "swresample-*.dll",
    "avdevice-*.dll",
    "avfilter-*.dll"
)

New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
New-Item -ItemType Directory -Force -Path $cacheDir  | Out-Null

function Get-FfmpegArchive {
    Write-Host "[fetch-ffmpeg] downloading $downloadUrl"
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
}

if ((Test-Path $zipPath) -and -not $Force) {
    Write-Host "[fetch-ffmpeg] using cached $zipPath"
} else {
    Get-FfmpegArchive
}

if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    Write-Warning "[fetch-ffmpeg] ExpectedSha256 empty - skipping integrity check"
} else {
    $expected = $ExpectedSha256.Trim().ToUpperInvariant()
    $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        # A stale or half-written cache is the likely cause, so spend one retry
        # on it before giving up.
        Write-Warning "[fetch-ffmpeg] checksum mismatch on $zipPath - re-downloading"
        Remove-Item -LiteralPath $zipPath -Force
        Get-FfmpegArchive
        $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    }
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $zipPath -Force
        throw "[fetch-ffmpeg] SHA-256 mismatch, refusing to unpack: expected $expected, got $actual"
    }
    Write-Host "[fetch-ffmpeg] sha256 ok"
}

if (Test-Path $extractDir) {
    Remove-Item -Recurse -Force $extractDir
}
Write-Host "[fetch-ffmpeg] extracting"
Expand-Archive -LiteralPath $zipPath -DestinationPath $cacheDir

$srcBin = Get-ChildItem -Path $extractDir -Directory -Recurse |
          Where-Object { $_.Name -eq "bin" } |
          Select-Object -First 1

if (-not $srcBin) {
    throw "Could not locate bin/ inside extracted archive at $extractDir"
}

Write-Host "[fetch-ffmpeg] copying DLLs from $($srcBin.FullName) to $nativeDir"
foreach ($pattern in $requiredDlls) {
    Get-ChildItem -Path $srcBin.FullName -Filter $pattern -File |
        ForEach-Object {
            Copy-Item -Path $_.FullName -Destination $nativeDir -Force
            Write-Host "  $($_.Name)"
        }
}

# ffmpeg.exe — the recorder and clip-exporter shell out to it (segment rotation,
# precise re-encode). The shared-build exe is tiny; it loads the DLLs above.
# Without it, recording/export fail with "cannot find ffmpeg" on packaged builds.
$ffmpegExe = Join-Path $srcBin.FullName "ffmpeg.exe"
if (Test-Path $ffmpegExe) {
    Copy-Item -Path $ffmpegExe -Destination $nativeDir -Force
    Write-Host "  ffmpeg.exe"
} else {
    Write-Warning "[fetch-ffmpeg] ffmpeg.exe not found in $($srcBin.FullName) — recording/export will fall back to PATH."
}

Write-Host "[fetch-ffmpeg] done. $nativeDir contains $((Get-ChildItem $nativeDir -Filter '*.dll').Count) DLL(s) + ffmpeg.exe."
