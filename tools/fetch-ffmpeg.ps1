<#
.SYNOPSIS
  Downloads the FFmpeg shared build (LGPL) for Windows x64 and places its DLLs
  + ffmpeg.exe in runtimes/win-x64/native/. ffmpeg.exe is what the recorder and
  clip-exporter shell out to. Everything is git-ignored — every dev runs this once.

.NOTES
  Source: BtbN/FFmpeg-Builds GitHub releases (https://github.com/BtbN/FFmpeg-Builds).
  Version pin: n7.1 (matches FFmpeg.AutoGen 7.1.x bindings).
  Build flavor: lgpl-shared (no GPL components; safe to redistribute alongside
  a closed-source app provided DLLs remain replaceable).
#>
[CmdletBinding()]
param(
    [string]$FfmpegVersion = "n7.1",
    [string]$AssetName     = "ffmpeg-n7.1-latest-win64-lgpl-shared-7.1.zip",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$nativeDir  = Join-Path $repoRoot "runtimes/win-x64/native"
$cacheDir   = Join-Path $repoRoot ".cache/ffmpeg"
$zipPath    = Join-Path $cacheDir $AssetName
$extractDir = Join-Path $cacheDir ($AssetName -replace "\.zip$","")

$downloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$AssetName"

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

if ((Test-Path $zipPath) -and -not $Force) {
    Write-Host "[fetch-ffmpeg] using cached $zipPath"
} else {
    Write-Host "[fetch-ffmpeg] downloading $downloadUrl"
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
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
