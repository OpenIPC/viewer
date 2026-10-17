using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen.Abstractions;

namespace OpenIPC.Viewer.Video.Pipeline;

internal static partial class FfmpegError
{
    // FFmpeg on Windows maps socket failures to the MSVC CRT's POSIX errno
    // values (ETIMEDOUT = 138, ...) or passes WSA codes through, and the CRT's
    // strerror knows neither — av_strerror then prints "Error number -138
    // occurred". Name the ones a camera connection actually hits.
    private static readonly Dictionary<int, string> WindowsSocketErrors = new()
    {
        [100] = "Address already in use",
        [106] = "Connection aborted",
        [107] = "Connection refused",
        [108] = "Connection reset by peer",
        [110] = "Host is unreachable",
        [116] = "Network is down",
        [118] = "Network is unreachable",
        [138] = "Connection timed out",
        [10053] = "Connection aborted",
        [10054] = "Connection reset by peer",
        [10060] = "Connection timed out",
        [10061] = "Connection refused",
        [10051] = "Network is unreachable",
        [10065] = "Host is unreachable",
    };

    [GeneratedRegex(@"Error number -(\d+) occurred")]
    private static partial Regex UnknownErrnoRegex();

    public static unsafe string Describe(int code)
    {
        const int bufSize = 1024;
        var buffer = stackalloc byte[bufSize];
        ffmpeg.av_strerror(code, buffer, (ulong)bufSize);
        var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"FFmpeg error {code}";
        return Humanize(text);
    }

    // Swaps FFmpeg's "Error number -N occurred" for the socket error's name
    // when N is one we know; anything else passes through untouched.
    public static string Humanize(string text) =>
        text.Contains("Error number -", StringComparison.Ordinal)
            ? UnknownErrnoRegex().Replace(text, m =>
                int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && WindowsSocketErrors.TryGetValue(n, out var name)
                    ? name
                    : m.Value)
            : text;

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
