using System.Collections.Concurrent;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Infrastructure.Tests;

// Backs host-key TOFU storage during integration tests — no DPAPI/keychain.
internal sealed class InMemorySecretsStore : ISecretsStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public Task<string?> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
