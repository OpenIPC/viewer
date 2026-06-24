using System;
using FFmpeg.AutoGen.Abstractions;

namespace OpenIPC.Viewer.Video.Pipeline;

internal static class FfmpegError
{
    public static unsafe string Describe(int code)
    {
        const int bufSize = 1024;
        var buffer = stackalloc byte[bufSize];
        ffmpeg.av_strerror(code, buffer, (ulong)bufSize);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"FFmpeg error {code}";
    }

    public static void ThrowIfError(int code, string operation)
    {
        if (code < 0)
            throw new FfmpegException(operation, code);
    }
}

internal sealed class FfmpegException : Exception
{
    public int Code { get; }

    public FfmpegException(string operation, int code)
        : base(Compose(operation, code))
    {
        Code = code;
    }

    // Append FFmpeg's own last error line when we captured one — turns a generic
    // "Operation not permitted (-1)" into the concrete cause (e.g. a 401 or
    // "Connection refused") surfaced in the UI error banner.
    private static string Compose(string operation, int code)
    {
        var msg = $"{operation} failed: {FfmpegError.Describe(code)} ({code})";
        var native = FfmpegRuntime.TakeLastNativeError();
        return string.IsNullOrEmpty(native) ? msg : $"{msg} — {native}";
    }
}
