using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Analytics;

// The ordered list of execution providers to try for the current platform,
// most-preferred first. CPU is always the implicit final fallback (the base
// ONNX Runtime package registers it on every RID) so it is not listed here.
//
// Only providers reachable through the generic AppendExecutionProvider(string)
// API are listed — they no-op/throw gracefully when their native EP isn't
// compiled into the referenced package, and we catch + fall back. DirectML and
// CUDA need their dedicated packages + AppendExecutionProvider_DML/_CUDA calls
// wired in the platform composition; until those are added per head, Windows
// and Linux run on CPU.
internal static class ExecutionProviderChain
{
    public static IReadOnlyList<(string Name, ExecutionProvider Provider)> ForCurrentPlatform()
    {
        if (OperatingSystem.IsAndroid())
            return new[] { ("NNAPI", ExecutionProvider.NnApi), ("XNNPACK", ExecutionProvider.Xnnpack) };

        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return new[] { ("CoreML", ExecutionProvider.CoreMl) };

        // Windows / Linux: CPU only for now (GPU EPs are a per-head package job).
        return Array.Empty<(string, ExecutionProvider)>();
    }
}
