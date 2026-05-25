using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Infrastructure.Secrets;

// Subprocess-based wrapper around `secret-tool` (libsecret-tools package).
// Reasoning: variadic C P/Invoke to libsecret.so.1 needs GHashTable
// marshalling through glib, and Tmds.DBus would pull in a managed dep just
// for one credential store. `secret-tool` ships with every desktop libsecret
// install and matches the phase-08 demo step:
//   secret-tool lookup application openipc-viewer key cam:...
// Falls back to EncryptedFileSecretsStore when `secret-tool` is missing or
// there's no D-Bus session (headless servers, CI runners).
[SupportedOSPlatform("linux")]
public sealed class LibSecretSecretsStore : ISecretsStore
{
    private const string Tool = "secret-tool";
    private const string Application = "openipc-viewer";

    private readonly ILogger<LibSecretSecretsStore> _logger;
    private readonly ISecretsStore _fallback;
    private readonly Lazy<bool> _toolAvailable;

    public LibSecretSecretsStore(DirectoryInfo appDataDir, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<LibSecretSecretsStore>();
        _fallback = EncryptedFileSecretsStore.ForLinux(appDataDir);
        _toolAvailable = new Lazy<bool>(DetectTool, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        if (!_toolAvailable.Value)
            return await _fallback.GetAsync(key, ct).ConfigureAwait(false);

        var result = await RunAsync(
            new[] { "lookup", "application", Application, "key", key },
            stdin: null, ct).ConfigureAwait(false);

        if (result.ExitCode == 0)
            return result.Stdout.TrimEnd('\n');

        // secret-tool exits non-zero when the key is missing — that's a normal
        // "no such secret" path, not a real failure.
        if (string.IsNullOrEmpty(result.Stderr))
            return null;

        _logger.LogWarning("secret-tool lookup failed ({Code}): {Err}; falling back", result.ExitCode, result.Stderr);
        return await _fallback.GetAsync(key, ct).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        if (!_toolAvailable.Value)
        {
            await _fallback.SetAsync(key, value, ct).ConfigureAwait(false);
            return;
        }

        var result = await RunAsync(
            new[] { "store", "--label=OpenIPC Viewer", "application", Application, "key", key },
            stdin: value, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("secret-tool store failed ({Code}): {Err}; falling back", result.ExitCode, result.Stderr);
            await _fallback.SetAsync(key, value, ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        if (!_toolAvailable.Value)
        {
            await _fallback.RemoveAsync(key, ct).ConfigureAwait(false);
            return;
        }

        var result = await RunAsync(
            new[] { "clear", "application", Application, "key", key },
            stdin: null, ct).ConfigureAwait(false);

        // clear is idempotent and returns 0 even when nothing matched.
        if (result.ExitCode != 0)
            _logger.LogWarning("secret-tool clear failed ({Code}): {Err}", result.ExitCode, result.Stderr);
    }

    private bool DetectTool()
    {
        try
        {
            var psi = new ProcessStartInfo(Tool, "--help")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("secret-tool not available, using encrypted-file fallback: {Reason}", ex.Message);
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start secret-tool");

        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            p.StandardInput.Close();
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
