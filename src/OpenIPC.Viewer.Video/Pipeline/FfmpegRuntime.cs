using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace OpenIPC.Viewer.Video.Pipeline;

internal static class FfmpegRuntime
{
    // Raised for every native FFmpeg log line at warning level or worse. The
    // video engine subscribes once and routes these to ILogger; nobody else
    // should. Kept as a plain event so the Video project stays DI-free here.
    public static event Action<int, string>? NativeLog;

    // The real reason behind the most recent error, captured off FFmpeg's own
    // log (av_strerror only gives the generic "Operation not permitted"). Thread
    // -static because avformat_open_input logs synchronously on its caller; the
    // FfmpegException ctor on that same thread consumes it.
    [ThreadStatic] private static string? _lastNativeError;

    // Held so the GC can't collect the delegate while native code holds it.
    private static av_log_set_callback_callback? _logCallback;

    internal static string? TakeLastNativeError()
    {
        var v = _lastNativeError;
        _lastNativeError = null;
        return v;
    }
    // Serializes the one-time native init. The old design used an Interlocked
    // CompareExchange flag, but that let the *second* caller return "ready"
    // the instant the first caller flipped the flag — before its (slow)
    // DynamicallyLoadedBindings.Initialize() had populated the function
    // vectors. On a multi-camera grid every tile's reconnect loop calls in
    // concurrently, so the racers spawned decode threads that hit a still-null
    // delegate and died with a bare NullReferenceException inside av_dict_set.
    // A lock makes concurrent callers block until init genuinely completes.
    private static readonly object _gate = new();
    private static volatile bool _ready;
    private static Exception? _initFailure;

    public static void EnsureInitialized()
    {
        if (_ready)
        {
            if (_initFailure is not null) throw _initFailure;
            return;
        }

        lock (_gate)
        {
            if (_ready)
            {
                if (_initFailure is not null) throw _initFailure;
                return;
            }

            if (OperatingSystem.IsAndroid())
            {
                // FFmpeg.AutoGen 7.1.1's FunctionResolverFactory.Create() throws
                // PlatformNotSupportedException on Android — RuntimeInformation
                // .IsOSPlatform(Linux) returns false (Android is its own platform
                // in .NET 6+). Initialize() uses FunctionResolver if set, otherwise
                // calls the broken factory. Setting it here bypasses the throw.
                DynamicallyLoadedBindings.FunctionResolver = new AndroidFunctionResolver();
            }

            var nativeDir = ResolveNativeDir();

            try
            {
                // Replaces the old ffmpeg.RootPath setter. The Abstractions.ffmpeg
                // facade no longer owns the loader path; LibrariesPath on
                // DynamicallyLoadedBindings is the single source of truth.
                DynamicallyLoadedBindings.LibrariesPath = nativeDir;

                // Abstractions.ffmpeg cctor doesn't auto-invoke Initialize the way
                // the old monolithic FFmpeg.AutoGen package did — we have to call
                // it explicitly to populate the vectors before touching av_*.
                DynamicallyLoadedBindings.Initialize();

                // Touch one function so the P/Invoke loader resolves the native
                // libs early — anything missing surfaces here instead of mid-stream.
                _ = ffmpeg.av_version_info();
                ffmpeg.avformat_network_init();
                InstallLogCallback();
            }
            catch (Exception ex) when (IsNativeLoadFailure(ex))
            {
                // Most-likely cause on Android: the APK shipped without FFmpeg
                // shared libs because tools/build-ffmpeg-android.sh wasn't run for
                // the device's ABI (arm64-v8a / x86_64), or the .so are present but
                // depend on something missing (libc++_shared, libmediandk, etc.).
                // Cache the failure and mark ready so later callers replay it
                // instead of walking into uninitialized bindings.
                var (rid, _) = RuntimeIds.Current();
                var probe = string.IsNullOrEmpty(nativeDir) ? "(loader path)" : nativeDir;
                _initFailure = new FfmpegNativeLibsMissingException(
                    $"FFmpeg native libraries failed to load for runtime '{rid ?? "unknown"}'. " +
                    $"Probed: {probe}. " +
                    $"Android: ensure runtimes/android-{{arm64,x64}}/native/*.so are populated " +
                    $"(run tools/build-ffmpeg-android.sh via WSL or pull .so from a CI APK artifact). " +
                    $"Desktop: keep the runtimes/ folder from the release archive next to the exe, " +
                    $"or tools/fetch-ffmpeg.ps1 (Windows) / apt/brew. " +
                    $"Underlying: {DescribeChain(ex)}", ex);
                _ready = true;
                throw _initFailure;
            }

            _ready = true;
        }
    }

    // Route FFmpeg's internal av_log through a managed callback so the actual
    // failure reason (auth 401, connection refused, protocol whitelist, ...) is
    // captured instead of just the strerror of the return code.
    private static unsafe void InstallLogCallback()
    {
        _logCallback = LogCallback;
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
        ffmpeg.av_log_set_callback(_logCallback);
    }

    private static unsafe void LogCallback(void* avcl, int level, string fmt, byte* vl)
    {
        if (level > ffmpeg.AV_LOG_WARNING) return;

        const int bufSize = 1024;
        var buf = stackalloc byte[bufSize];
        var printPrefix = 1;
        ffmpeg.av_log_format_line2(avcl, level, fmt, vl, buf, bufSize, &printPrefix);
        var msg = Marshal.PtrToStringAnsi((IntPtr)buf)?.Trim();
        if (string.IsNullOrEmpty(msg)) return;

        if (level <= ffmpeg.AV_LOG_ERROR) _lastNativeError = msg;

        // A logging sink must never throw back into native FFmpeg.
        try { NativeLog?.Invoke(level, msg); }
        catch { /* swallow */ }
    }

    private static bool IsNativeLoadFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is DllNotFoundException) return true;
            if (cur is BadImageFormatException) return true;
            // FFmpeg.AutoGen's DynamicallyLoadedBindings doesn't throw on a
            // failed library load — it leaves the function vector pointing at a
            // stub that throws NotSupportedException on first call. Within this
            // init scope that's always a load failure, not a real capability gap.
            if (cur is NotSupportedException) return true;
        }
        return ex is TypeInitializationException;
    }

    private static string DescribeChain(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (sb.Length > 0) sb.Append(" → ");
            sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
        }
        return sb.ToString();
    }

    private static string ResolveNativeDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var (rid, _) = RuntimeIds.Current();
        if (rid is not null)
        {
            var candidate = Path.Combine(baseDir, "runtimes", rid, "native");
            if (Directory.Exists(candidate))
                return candidate;
        }

        // Fall back to "" — FFmpeg.AutoGen then uses the platform loader path
        // (apt/brew-installed libs on Linux/macOS, DLLs next to the exe on Windows,
        // and the bundled .so files added via <AndroidNativeLibrary> on Android).
        return "";
    }
}

public sealed class FfmpegNativeLibsMissingException : Exception
{
    public FfmpegNativeLibsMissingException(string message, Exception inner)
        : base(message, inner) { }
}
