using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Discovery;

namespace OpenIPC.Viewer.App.Services;

// Session-scoped memory for the discovery dialog, so the user can add several
// cameras from ONE scan: results, the typed credentials and the deep-scan flag
// all survive closing and reopening the dialog. In-memory only on purpose —
// the credentials must never be persisted to disk from here (they end up in
// the secrets store per-camera once a camera is saved).
public sealed class DiscoverySessionCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DiscoveredDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool DeepScan { get; set; }

    public IReadOnlyList<DiscoveredDevice> Snapshot()
    {
        lock (_sync)
            return new List<DiscoveredDevice>(_devices.Values);
    }

    public void Put(DiscoveredDevice device)
    {
        lock (_sync)
            _devices[device.Host] = device;
    }

    public void Clear()
    {
        lock (_sync)
            _devices.Clear();
    }
}
