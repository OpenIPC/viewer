using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Infrastructure.Ssh;

/// <summary>
/// Stores pinned host keys in <c>ssh_known_hosts.json</c> under the app data
/// dir. Loaded once into memory; writes are serialized through a lock. Clearing
/// just empties the map and rewrites the file.
/// </summary>
public sealed class JsonFileHostKeyStore : ISshHostKeyStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<JsonFileHostKeyStore> _logger;
    private readonly object _gate = new();
    private Dictionary<string, string>? _map;

    public JsonFileHostKeyStore(IFileSystem fs, ILogger<JsonFileHostKeyStore> logger)
    {
        _path = Path.Combine(fs.AppDataDir.FullName, "ssh_known_hosts.json");
        _logger = logger;
    }

    public Task<string?> GetAsync(string host, int port, CancellationToken ct)
    {
        lock (_gate)
        {
            var map = Load();
            return Task.FromResult(map.TryGetValue(Key(host, port), out var v) ? v : null);
        }
    }

    public Task SetAsync(string host, int port, string fingerprint, CancellationToken ct)
    {
        lock (_gate)
        {
            var map = Load();
            map[Key(host, port)] = fingerprint;
            Save(map);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _map = new Dictionary<string, string>();
            Save(_map);
        }
        return Task.CompletedTask;
    }

    private Dictionary<string, string> Load()
    {
        if (_map is not null)
            return _map;
        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, JsonOpts);
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ssh_known_hosts.json — starting empty");
        }
        return _map ??= new Dictionary<string, string>();
    }

    private void Save(Dictionary<string, string> map)
    {
        try
        {
            var tmp = _path + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, map, JsonOpts);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write ssh_known_hosts.json");
        }
    }

    private static string Key(string host, int port) => $"{host}:{port}";
}
