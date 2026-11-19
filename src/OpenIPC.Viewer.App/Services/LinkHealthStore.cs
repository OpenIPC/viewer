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
//
// Writes are serialized (snapshot + write + move under one lock), and Dispose
// waits for a timer save already in flight before the final save, so two
// writers never share the .tmp file and an older snapshot can't land last.
// Loading never throws: the store is resolved at startup by every head, so a
// damaged or hand-edited file must fall back to an empty journal, not stop
// the app.
public sealed class LinkHealthStore : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);
    // Longest outage worth keeping (100 years); anything beyond is a corrupt value.
    private const double MaxOutageSeconds = 100 * 365.25 * 24 * 3600;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ConnectionJournal _journal;
    private readonly ILogger<LinkHealthStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();      // timer + disposed flag
    private readonly object _saveGate = new();  // one writer at a time
    private Timer? _timer;
    private bool _disposed;

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
        Timer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        if (timer is not null)
        {
            // Stop new callbacks and wait for one that is already saving, so the
            // final save below is the last write.
            var done = new ManualResetEvent(false);
            // Dispose the event only once it fired: the timer signals it later
            // if the in-flight save outlives the wait, and a closed handle there
            // would fault a pool thread. (A leak only in that shutdown corner.)
            if (!timer.Dispose(done) || done.WaitOne(DisposeWait))
                done.Dispose();
        }
        Save();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
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

            // A loop, not ToDictionary: the same camera twice (e.g. its GUID in
            // two letter cases) keeps the last entry instead of throwing.
            var counters = new Dictionary<CameraId, ConnectionCounters>();
            foreach (var c in dto.Counters ?? new List<CounterDto>())
            {
                if (c is null || !Guid.TryParse(c.CameraId, out var id)) continue;
                counters[new CameraId(id)] = new ConnectionCounters(
                    Math.Max(0, c.Connects), Math.Max(0, c.Drops), Math.Max(0, c.FailedAttempts), 0,
                    c.LastConnectedAt, c.LastDropAt, c.LastRecoveredAt,
                    Outage(c.LastOutageSeconds));
            }

            var events = new List<ConnectionEvent>();
            foreach (var e in dto.Events ?? new List<EventDto>())
            {
                if (e is null || !Guid.TryParse(e.CameraId, out var id)) continue;
                if (!Enum.IsDefined(typeof(ConnectionEventKind), e.Kind)) continue;
                events.Add(new ConnectionEvent(new CameraId(id), e.At, e.Kind, e.Detail));
            }

            _journal.Import(counters, events);
        }
        catch (Exception ex)
        {
            // Anything wrong with the file — unreadable, not JSON, or JSON with
            // values out of range — means "no history", never a failed startup.
            _logger.LogWarning(ex, "Could not read {Path}; starting with an empty connection journal", _path);
        }
    }

    private static TimeSpan? Outage(double? seconds) =>
        seconds is { } s && !double.IsNaN(s) && !double.IsInfinity(s) && s >= 0 && s <= MaxOutageSeconds
            ? TimeSpan.FromSeconds(s)
            : null;

    private void Save()
    {
        lock (_saveGate)
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
