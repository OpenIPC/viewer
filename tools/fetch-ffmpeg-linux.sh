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
# Pin:     n7.1 (matches FFmpeg.AutoGen 7.1.x bindings).
# Flavor:  lgpl-shared (no GPL components; redistributable alongside the app as
#          long as the .so remain shared + replaceable — see README "Licensing").
set -euo pipefail

FFMPEG_VERSION="${FFMPEG_VERSION:-n7.1}"
ASSET_NAME="${ASSET_NAME:-ffmpeg-${FFMPEG_VERSION}-latest-linux64-lgpl-shared-7.1.tar.xz}"
FORCE="${FORCE:-0}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
native_dir="$repo_root/runtimes/linux-x64/native"
cache_dir="$repo_root/.cache/ffmpeg"
archive_path="$cache_dir/$ASSET_NAME"
extract_dir="$cache_dir/${ASSET_NAME%.tar.xz}"

download_url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$ASSET_NAME"

mkdir -p "$native_dir" "$cache_dir"

if [[ -f "$archive_path" && "$FORCE" != "1" ]]; then
  echo "[fetch-ffmpeg-linux] using cached $archive_path"
else
  echo "[fetch-ffmpeg-linux] downloading $download_url"
  curl -fL --retry 3 -o "$archive_path" "$download_url"
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
