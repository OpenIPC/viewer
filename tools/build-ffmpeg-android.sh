#!/usr/bin/env bash
# tools/build-ffmpeg-android.sh
#
# Cross-compiles FFmpeg n7.1 for Android arm64-v8a. Output goes to
# runtimes/android-arm64/native/ — picked up by OpenIPC.Viewer.Android.csproj's
# AndroidNativeLibrary glob and bundled into the APK's lib/arm64-v8a/.
#
# Requirements:
#   - ANDROID_NDK_HOME pointing at NDK r25+ (set up by nttld/setup-ndk in CI)
#   - bash, make, pkg-config, yasm/nasm, git, curl
#
# The configure flags below carve a minimal-but-functional FFmpeg suitable
# for OpenIPC viewer's needs: RTSP + h264/hevc decode with MediaCodec
# hardware accel. Audio decoders are stripped to keep .so size down (we
# don't currently render audio). Re-enable if Phase 9c+ needs them.
#
# Local usage:
#   ANDROID_NDK_HOME=/path/to/ndk ./tools/build-ffmpeg-android.sh
#
# CI usage: invoked by the android job in .github/workflows/build.yml.
# Cache key is keyed on FFmpeg version + script hash; cache hit skips the
# whole script.

set -euo pipefail

FFMPEG_VERSION="${FFMPEG_VERSION:-n7.1}"
ANDROID_API="${ANDROID_API:-21}"
NDK_HOME="${ANDROID_NDK_HOME:?ANDROID_NDK_HOME must be set}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_DIR="$REPO_ROOT/runtimes/android-arm64/native"
BUILD_DIR="$REPO_ROOT/build/ffmpeg-android-arm64"
SRC_DIR="$BUILD_DIR/ffmpeg"
INSTALL_DIR="$BUILD_DIR/install"

# Host detection — Android NDK ships different prebuilt toolchain trees per host.
case "$(uname -s)" in
    Linux*)   HOST_TAG="linux-x86_64" ;;
    Darwin*)  HOST_TAG="darwin-x86_64" ;;
    MINGW*|MSYS*|CYGWIN*) HOST_TAG="windows-x86_64" ;;
    *) echo "Unsupported host $(uname -s)" >&2; exit 1 ;;
esac

TOOLCHAIN="$NDK_HOME/toolchains/llvm/prebuilt/$HOST_TAG"
if [ ! -d "$TOOLCHAIN" ]; then
    echo "NDK toolchain not found at $TOOLCHAIN" >&2
    exit 1
fi

# Clang wrappers in the NDK encode the target triple + API level in the binary
# name; that's what FFmpeg's --cc / --cross-prefix expect.
TARGET=aarch64-linux-android
CC="$TOOLCHAIN/bin/${TARGET}${ANDROID_API}-clang"
CXX="$TOOLCHAIN/bin/${TARGET}${ANDROID_API}-clang++"
AR="$TOOLCHAIN/bin/llvm-ar"
RANLIB="$TOOLCHAIN/bin/llvm-ranlib"
STRIP="$TOOLCHAIN/bin/llvm-strip"
NM="$TOOLCHAIN/bin/llvm-nm"

mkdir -p "$BUILD_DIR" "$OUT_DIR"

if [ ! -d "$SRC_DIR/.git" ]; then
    echo "==> Cloning FFmpeg $FFMPEG_VERSION"
    git clone --depth 1 --branch "$FFMPEG_VERSION" \
        https://git.ffmpeg.org/ffmpeg.git "$SRC_DIR"
fi

cd "$SRC_DIR"

# Soname stripping: Android disallows shared-lib symlinks in APKs (only the
# unversioned libfoo.so survives the packaging step), and FFmpeg.AutoGen on
# Linux/Android tries the bare libfoo.so first. --build-suffix='' is the
# canonical way to keep the output filenames unversioned.
echo "==> Configuring FFmpeg for android-arm64 (api $ANDROID_API)"
./configure \
    --prefix="$INSTALL_DIR" \
    --target-os=android \
    --arch=aarch64 \
    --cpu=armv8-a \
    --enable-cross-compile \
    --cross-prefix="$TOOLCHAIN/bin/llvm-" \
    --cc="$CC" \
    --cxx="$CXX" \
    --ar="$AR" \
    --ranlib="$RANLIB" \
    --strip="$STRIP" \
    --nm="$NM" \
    --sysroot="$TOOLCHAIN/sysroot" \
    --extra-cflags="-Os -fpic" \
    --extra-ldflags="-Wl,-z,max-page-size=16384" \
    --enable-shared \
    --disable-static \
    --enable-pic \
    --enable-jni \
    --enable-mediacodec \
    --enable-hwaccel=h264_mediacodec \
    --enable-hwaccel=hevc_mediacodec \
    --disable-doc \
    --disable-programs \
    --disable-symver \
    --disable-debug \
    --disable-everything \
    --enable-network \
    --enable-protocol=file,http,https,tcp,udp,rtp,rtsp,tls \
    --enable-demuxer=rtsp,rtp,sdp,mpegts,mov,h264,hevc \
    --enable-parser=h264,hevc,aac \
    --enable-decoder=h264,h264_mediacodec,hevc,hevc_mediacodec,aac \
    --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb,extract_extradata \
    --enable-muxer=mp4 \
    --enable-encoder=h264_mediacodec,hevc_mediacodec

echo "==> Building (this takes ~10-20 min)"
make -j"$(nproc 2>/dev/null || sysctl -n hw.ncpu)"
make install

echo "==> Copying .so to $OUT_DIR"
rm -f "$OUT_DIR"/*.so
# Copy unversioned soname only — the versioned symlinks are useless inside
# an APK and would just bloat the install if accidentally picked up.
find "$INSTALL_DIR/lib" -maxdepth 1 -type f -name "*.so" -exec cp {} "$OUT_DIR/" \;

echo "==> Built libs:"
ls -la "$OUT_DIR"
