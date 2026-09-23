#!/usr/bin/env bash
# Downloads the FFmpeg shared build (LGPL) for Linux x64 and places its .so
# libraries + the ffmpeg binary in runtimes/linux-x64/native/. The shared libs
# are what FFmpeg.AutoGen's DynamicallyLoaded loader dlopen()s for live decode;
# the binary is what the subprocess recorder/clip-exporter shell out to.
# Everything is git-ignored — every dev (and CI) runs this once.
#
# WHY THIS EXISTS (issue #35): the app binds FFmpeg.AutoGen 7.1.1, generated
# against the FFmpeg n7.x ABI, so the loader resolves version-suffixed sonames
# (libavcodec.so.61, libavformat.so.61, libavutil.so.59, ...). No current Ubuntu
# LTS ships FFmpeg 7 — 24.04 has 6.1 (libavcodec.so.60), 22.04 has 4.4 — so
# `apt install ffmpeg` cannot satisfy the ABI and live view dies with
# "FFmpeg native libraries failed to load for runtime linux-x64". Bundling the
# matching n7.1 libs next to the binary makes the linux-x64 release self-
# contained, exactly like fetch-ffmpeg.ps1 does for win-x64.
#
# Source:  BtbN/FFmpeg-Builds GitHub releases (https://github.com/BtbN/FFmpeg-Builds).
# Pin:     n7.1 (matches FFmpeg.AutoGen 7.1.x bindings), from a dated release
#          rather than the rolling `latest` tag. `latest` is rebuilt daily and
#          only keeps the branches BtbN currently builds: n7.1 was dropped from
#          it in August 2026 (only master / n8.1 / n9.0 remain), which 404'd this
#          script and took CI down with it. Only month-end dated tags are kept
#          long-term (back to 2024); mid-month ones are pruned within weeks, which
#          404'd the previous 2026-08-16 pin. 2026-07-31 is the last month-end tag
#          that still carries n7.1. The SHA-256 below makes a silently swapped or
#          truncated archive fail here instead of at runtime.
# Flavor:  lgpl-shared (no GPL components; redistributable alongside the app as
#          long as the .so remain shared + replaceable — see README "Licensing").
set -euo pipefail

# Overridable together: a different release/asset needs its own checksum, and
# an empty EXPECTED_SHA256 skips the check (with a warning) rather than failing
# every override.
FFMPEG_RELEASE="${FFMPEG_RELEASE:-autobuild-2026-07-31-14-10}"
ASSET_NAME="${ASSET_NAME:-ffmpeg-n7.1.5-12-g1fdbca85aa-linux64-lgpl-shared-7.1.tar.xz}"
EXPECTED_SHA256="${EXPECTED_SHA256-f5f0ad52c6ee28a222eb10838c231469a10ad325f84063d3bc0aadf08164b3ec}"
FORCE="${FORCE:-0}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
native_dir="$repo_root/runtimes/linux-x64/native"
cache_dir="$repo_root/.cache/ffmpeg"
archive_path="$cache_dir/$ASSET_NAME"
extract_dir="$cache_dir/${ASSET_NAME%.tar.xz}"

download_url="https://github.com/BtbN/FFmpeg-Builds/releases/download/$FFMPEG_RELEASE/$ASSET_NAME"

mkdir -p "$native_dir" "$cache_dir"

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

download() {
  echo "[fetch-ffmpeg-linux] downloading $download_url"
  curl -fL --retry 3 -o "$archive_path" "$download_url"
}

if [[ -f "$archive_path" && "$FORCE" != "1" ]]; then
  echo "[fetch-ffmpeg-linux] using cached $archive_path"
else
  download
fi

if [[ -z "$EXPECTED_SHA256" ]]; then
  echo "[fetch-ffmpeg-linux] EXPECTED_SHA256 empty — skipping integrity check" >&2
else
  actual="$(sha256_of "$archive_path")"
  if [[ "$actual" != "$EXPECTED_SHA256" ]]; then
    # A stale or half-written cache is the likely cause, so spend one retry on
    # it before giving up.
    echo "[fetch-ffmpeg-linux] checksum mismatch on $archive_path — re-downloading" >&2
    rm -f "$archive_path"
    download
    actual="$(sha256_of "$archive_path")"
  fi
  if [[ "$actual" != "$EXPECTED_SHA256" ]]; then
    echo "[fetch-ffmpeg-linux] SHA-256 mismatch, refusing to unpack:" >&2
    echo "  expected $EXPECTED_SHA256" >&2
    echo "  got      $actual" >&2
    rm -f "$archive_path"
    exit 1
  fi
  echo "[fetch-ffmpeg-linux] sha256 ok"
fi

rm -rf "$extract_dir"
echo "[fetch-ffmpeg-linux] extracting"
tar -xJf "$archive_path" -C "$cache_dir"

src_lib="$(find "$extract_dir" -type d -name lib -print -quit)"
src_bin="$(find "$extract_dir" -type d -name bin -print -quit)"
if [[ -z "$src_lib" ]]; then
  echo "[fetch-ffmpeg-linux] could not locate lib/ inside $extract_dir" >&2
  exit 1
fi

# Required runtime libs only — the DynamicallyLoaded loader resolves these five
# plus avdevice/avfilter (pulled in transitively). cp -P preserves the soname
# symlinks (libavcodec.so -> libavcodec.so.61 -> libavcodec.so.61.x.x); the
# loader needs the .so.61 name and the libs find each other via RUNPATH=$ORIGIN.
echo "[fetch-ffmpeg-linux] copying .so from $src_lib to $native_dir"
shopt -s nullglob
for pattern in avcodec avformat avutil swscale swresample avdevice avfilter postproc; do
  for f in "$src_lib"/lib${pattern}.so*; do
    cp -Pf "$f" "$native_dir/"
    echo "  $(basename "$f")"
  done
done
shopt -u nullglob

# ffmpeg binary — the subprocess recorder + clip-exporter shell out to it. The
# recorder sets LD_LIBRARY_PATH to this dir so it finds the .so above (its build
# -time RUNPATH points at ../lib, which won't exist once co-located here).
if [[ -n "$src_bin" && -f "$src_bin/ffmpeg" ]]; then
  cp -f "$src_bin/ffmpeg" "$native_dir/"
  chmod +x "$native_dir/ffmpeg"
  echo "  ffmpeg"
else
  echo "[fetch-ffmpeg-linux] ffmpeg binary not found — recording/export will fall back to system PATH" >&2
fi

so_count="$(find "$native_dir" -maxdepth 1 -name '*.so*' | wc -l | tr -d ' ')"
echo "[fetch-ffmpeg-linux] done. $native_dir contains $so_count .so file(s) + ffmpeg."
