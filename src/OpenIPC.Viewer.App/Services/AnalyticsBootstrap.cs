using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.App.Services;

// Lazily brings the analytics engine online (Phase 15). The first tile with
// analytics enabled calls EnsureStartedAsync, which downloads/loads the model,
// picks the execution provider, and starts the auto-record coordinator. Best-
// effort: a failure (e.g. offline first run, no model) is logged and analytics
// simply stays off — it never blocks the UI or crashes the app.
public sealed class AnalyticsBootstrap
{
    private readonly IAnalyticsEngine _engine;
    private readonly AutoRecordCoordinator _autoRecord;
    private readonly UserSettingsService _settings;
    private readonly ILogger<AnalyticsBootstrap> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;

    public AnalyticsBootstrap(
        IAnalyticsEngine engine,
        AutoRecordCoordinator autoRecord,
        UserSettingsService settings,
        ILogger<AnalyticsBootstrap> log)
    {
        _engine = engine;
        _autoRecord = autoRecord;
        _settings = settings;
        _log = log;
        _autoRecord.Failed += (_, ex) => _log.LogWarning(ex, "Auto-record error.");
    }

    public bool IsReady => _engine.IsReady;

    public async Task EnsureStartedAsync()
    {
        if (_started) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started) return;
            await _engine.InitializeAsync(_settings.AiAcceleration, CancellationToken.None).ConfigureAwait(false);
            _autoRecord.Start();
            _started = true;
            _log.LogInformation("Analytics engine started on {Provider}.", _engine.ActiveProvider);
        }
        catch (Exception ex)
        {
            // Leave _started false so a later enable retries (e.g. once online).
            _log.LogError(ex, "Analytics engine failed to start; analytics stays off.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
