using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Infrastructure.Secrets;

// Subprocess-based wrapper around macOS `security` (always present on macOS).
// Avoids the Security.framework CFType/CFDictionary marshalling boilerplate.
// Trade-off: the password appears as `-w <value>` on the command line and is
// briefly visible in `ps` output. For the v0.1.0-beta scope this is an
// accepted limitation, documented in README. A future phase can swap in
// SecItemAdd/SecItemCopyMatching P/Invoke if multi-user macOS hardening
// matters.
[SupportedOSPlatform("macos")]
public sealed class KeychainSecretsStore : ISecretsStore
{
    private const string Tool = "/usr/bin/security";
    private const string Service = "org.openipc.viewer";

    private readonly ILogger<KeychainSecretsStore> _logger;

    public KeychainSecretsStore(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<KeychainSecretsStore>();
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var result = await RunAsync(
            new[] { "find-generic-password", "-s", Service, "-a", key, "-w" },
            ct).ConfigureAwait(false);

        if (result.ExitCode == 0)
            return result.Stdout.TrimEnd('\n');

        // exit 44 = SecKeychainItemNotFound — normal "no such entry".
        if (result.ExitCode == 44)
            return null;

        _logger.LogWarning("security find-generic-password failed ({Code}): {Err}", result.ExitCode, result.Stderr);
        return null;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        // -U overwrites an existing entry instead of failing with exit 45.
        var result = await RunAsync(
            new[] { "add-generic-password", "-s", Service, "-a", key, "-w", value, "-U" },
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            _logger.LogError("security add-generic-password failed ({Code}): {Err}", result.ExitCode, result.Stderr);
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        var result = await RunAsync(
            new[] { "delete-generic-password", "-s", Service, "-a", key },
            ct).ConfigureAwait(false);

        // Missing-on-delete is idempotent from our perspective.
        if (result.ExitCode != 0 && result.ExitCode != 44)
            _logger.LogWarning("security delete-generic-password failed ({Code}): {Err}", result.ExitCode, result.Stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start /usr/bin/security");

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
