#!/usr/bin/env bash
# tools/build-ffmpeg-android.sh
#
# Cross-compiles FFmpeg n7.1 from upstream (git.ffmpeg.org) for the Android
# ABIs the app supports: arm64-v8a (default real-device ABI) and x86_64 (most
# emulators on Windows/Linux hosts). Outputs land in:
#   runtimes/android-arm64/native/   — picked up by AndroidNativeLibrary glob
#   runtimes/android-x64/native/       in src/OpenIPC.Viewer.Android.csproj
# and end up under lib/<abi>/ inside the produced APK.
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
#   BUILD_ABIS=aarch64 ./tools/build-ffmpeg-android.sh    # single ABI
#   BUILD_ABIS="aarch64 x86_64" ...                       # explicit (= default)
#
# CI usage: invoked by the android job in .github/workflows/build.yml.
# Cache key is keyed on FFmpeg version + script hash; cache hit skips the
# whole script.

set -euo pipefail

FFMPEG_VERSION="${FFMPEG_VERSION:-n7.1}"
ANDROID_API="${ANDROID_API:-21}"
BUILD_ABIS="${BUILD_ABIS:-aarch64 x86_64}"
NDK_HOME="${ANDROID_NDK_HOME:?ANDROID_NDK_HOME must be set}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

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

AR="$TOOLCHAIN/bin/llvm-ar"
RANLIB="$TOOLCHAIN/bin/llvm-ranlib"
STRIP="$TOOLCHAIN/bin/llvm-strip"
NM="$TOOLCHAIN/bin/llvm-nm"

build_one() {
    local abi="$1"
    local target ff_arch ff_cpu rid

    case "$abi" in
        aarch64)
            target=aarch64-linux-android
            ff_arch=aarch64
            ff_cpu=armv8-a
            rid=android-arm64
            ;;
        x86_64)
            target=x86_64-linux-android
            ff_arch=x86_64
            ff_cpu=x86-64
            rid=android-x64
            ;;
        *)
            echo "Unsupported BUILD_ABI '$abi' (expected: aarch64, x86_64)" >&2
            exit 1
            ;;
    esac

    local build_dir="$REPO_ROOT/build/ffmpeg-$rid"
    local src_dir="$build_dir/ffmpeg"
    local install_dir="$build_dir/install"
    local out_dir="$REPO_ROOT/runtimes/$rid/native"

    # Clang wrappers in the NDK encode the target triple + API level in the
    # binary name; that's what FFmpeg's --cc / --cross-prefix expect.
    local cc="$TOOLCHAIN/bin/${target}${ANDROID_API}-clang"
    local cxx="$TOOLCHAIN/bin/${target}${ANDROID_API}-clang++"

    mkdir -p "$build_dir" "$out_dir"

    if [ ! -d "$src_dir/.git" ]; then
        echo "==> [$abi] Cloning FFmpeg $FFMPEG_VERSION from upstream"
        git clone --depth 1 --branch "$FFMPEG_VERSION" \
            https://git.ffmpeg.org/ffmpeg.git "$src_dir"
    fi

    cd "$src_dir"
    # Reset any prior configure state in the same source tree so re-runs
    # across ABIs (we use a separate src_dir per ABI anyway, but be safe).
    make distclean >/dev/null 2>&1 || true

    # Soname stripping: Android disallows shared-lib symlinks in APKs (only the
    # unversioned libfoo.so survives the packaging step), and FFmpeg.AutoGen on
    # Linux/Android tries the bare libfoo.so first. --disable-symver is the
    # canonical way to keep the output filenames unversioned.
    echo "==> [$abi] Configuring FFmpeg (api $ANDROID_API, cpu $ff_cpu)"
    ./configure \
        --prefix="$install_dir" \
        --target-os=android \
        --arch="$ff_arch" \
        --cpu="$ff_cpu" \
        --enable-cross-compile \
        --cross-prefix="$TOOLCHAIN/bin/llvm-" \
        --cc="$cc" \
        --cxx="$cxx" \
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

    echo "==> [$abi] Building (this takes ~10-20 min)"
    make -j"$(nproc 2>/dev/null || sysctl -n hw.ncpu)"
    make install

    echo "==> [$abi] Copying .so to $out_dir"
    rm -f "$out_dir"/*.so
    # Copy unversioned soname only — the versioned symlinks are useless inside
    # an APK and would just bloat the install if accidentally picked up.
    find "$install_dir/lib" -maxdepth 1 -type f -name "*.so" -exec cp {} "$out_dir/" \;

    echo "==> [$abi] Built libs:"
    ls -la "$out_dir"
}

for abi in $BUILD_ABIS; do
    build_one "$abi"
done
