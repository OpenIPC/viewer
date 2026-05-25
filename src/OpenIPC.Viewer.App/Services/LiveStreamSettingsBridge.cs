using System;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

// Watches UserSettingsService.Changed; when RtspTransport differs from the
// last value we saw, asks the LiveStreamCoordinator to drop every cached
// session (which raises Invalidated, picked up by tiles / single-camera VM
// to restart with the new transport). Registered as a singleton — platform
// hosts must eagerly resolve it once at startup so the subscription wires up.
//
// Kept here in App (not Core) because UserSettingsService lives in App.
// Other future invalidation triggers (cache size, decoder swap) can join this
// class without growing the coordinator's surface.
public sealed class LiveStreamSettingsBridge
{
    private readonly UserSettingsService _settings;
    private readonly LiveStreamCoordinator _coordinator;
    private readonly ILogger<LiveStreamSettingsBridge> _logger;
    private string _lastTransport;

    public LiveStreamSettingsBridge(
        UserSettingsService settings,
        LiveStreamCoordinator coordinator,
        ILogger<LiveStreamSettingsBridge> logger)
    {
        _settings = settings;
        _coordinator = coordinator;
        _logger = logger;
        _lastTransport = settings.Current.RtspTransport;
        settings.Changed += OnChanged;
    }

    private async void OnChanged(object? sender, EventArgs e)
    {
        var next = _settings.Current.RtspTransport;
        if (string.Equals(next, _lastTransport, StringComparison.OrdinalIgnoreCase))
            return;
        _lastTransport = next;
        _logger.LogInformation("RtspTransport changed to {Transport}; invalidating cached sessions", next);
        try { await _coordinator.InvalidateAllAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "InvalidateAllAsync failed"); }
    }
}
