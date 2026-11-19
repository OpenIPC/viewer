using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.LinkHealth;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.App.Services;

// Persists the connection journal to AppDataDir/linkhealth.json so the counters
// (connects, drops, failed attempts) and the drop history used for the
// shared-segment analysis survive a restart of the app or of the computer.
// Saves are debounced: a burst of reconnect attempts writes the file once.
public sealed class LinkHealthStore : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ConnectionJournal _journal;
    private readonly ILogger<LinkHealthStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();
    private Timer? _timer;

    public LinkHealthStore(ConnectionJournal journal, IFileSystem fs, ILogger<LinkHealthStore> logger)
    {
        _journal = journal;
        _logger = logger;
        _path = Path.Combine(fs.AppDataDir.FullName, "linkhealth.json");
        Load();
        _journal.Changed += OnChanged;
    }

    public void Dispose()
    {
        _journal.Changed -= OnChanged;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        Save();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(_path), Json);
            if (dto is null) return;
            var counters = (dto.Counters ?? new List<CounterDto>())
                .Where(c => Guid.TryParse(c.CameraId, out _))
                .ToDictionary(
                    c => new CameraId(Guid.Parse(c.CameraId!)),
                    c => new ConnectionCounters(c.Connects, c.Drops, c.FailedAttempts, 0,
                        c.LastConnectedAt, c.LastDropAt, c.LastRecoveredAt,
                        c.LastOutageSeconds is { } s ? TimeSpan.FromSeconds(s) : null));
            var events = (dto.Events ?? new List<EventDto>())
                .Where(e => Guid.TryParse(e.CameraId, out _))
                .Select(e => new ConnectionEvent(new CameraId(Guid.Parse(e.CameraId!)), e.At, e.Kind, e.Detail));
            _journal.Import(counters, events);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; starting with an empty connection journal", _path);
        }
    }

    private void Save()
    {
        try
        {
            var (counters, events) = _journal.Export();
            var dto = new StoreDto
            {
                Counters = counters.Select(kv => new CounterDto
                {
                    CameraId = kv.Key.Value.ToString(),
                    Connects = kv.Value.Connects,
                    Drops = kv.Value.Drops,
                    FailedAttempts = kv.Value.FailedAttempts,
                    LastConnectedAt = kv.Value.LastConnectedAt,
                    LastDropAt = kv.Value.LastDropAt,
                    LastRecoveredAt = kv.Value.LastRecoveredAt,
                    LastOutageSeconds = kv.Value.LastOutage?.TotalSeconds,
                }).ToList(),
                Events = events.Select(e => new EventDto
                {
                    CameraId = e.CameraId.Value.ToString(),
                    At = e.At,
                    Kind = e.Kind,
                    Detail = e.Detail,
                }).ToList(),
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write {Path}", _path);
        }
    }

    private sealed class StoreDto
    {
        public List<CounterDto>? Counters { get; set; }
        public List<EventDto>? Events { get; set; }
    }

    private sealed class CounterDto
    {
        public string? CameraId { get; set; }
        public long Connects { get; set; }
        public long Drops { get; set; }
        public long FailedAttempts { get; set; }
        public DateTimeOffset? LastConnectedAt { get; set; }
        public DateTimeOffset? LastDropAt { get; set; }
        public DateTimeOffset? LastRecoveredAt { get; set; }
        public double? LastOutageSeconds { get; set; }
    }

    private sealed class EventDto
    {
        public string? CameraId { get; set; }
        public DateTimeOffset At { get; set; }
        public ConnectionEventKind Kind { get; set; }
        public string? Detail { get; set; }
    }
}
