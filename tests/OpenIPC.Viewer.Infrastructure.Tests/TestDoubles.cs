using System.Collections.Concurrent;
using OpenIPC.Viewer.Core.Settings;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Infrastructure.Tests;

internal sealed class InMemoryHostKeyStore : ISshHostKeyStore
{
    private readonly ConcurrentDictionary<string, string> _keys = new();

    public Task<string?> GetAsync(string host, int port, CancellationToken ct) =>
        Task.FromResult(_keys.TryGetValue($"{host}:{port}", out var v) ? v : null);

    public Task SetAsync(string host, int port, string fingerprint, CancellationToken ct)
    {
        _keys[$"{host}:{port}"] = fingerprint;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        _keys.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class FakeUserSettings : IUserSettingsAccessor
{
    public string? RecordingsDirectoryOverride => null;
    public int MaxConcurrentGridSessions => 9;
    public string? PreferredNetworkInterface => null;
    public bool SshStrictHostKey => true;
    public int SshDefaultPort => 22;
    public string MajesticConfigPath => "/etc/majestic.yaml";
    public OpenIPC.Viewer.Core.Analytics.AiAcceleration AiAcceleration =>
        OpenIPC.Viewer.Core.Analytics.AiAcceleration.Auto;
    public int ActiveLayoutId => 0;
    public bool NotificationsEnabled => true;
    public bool NotifyOnMotion => true;
    public bool NotifyOnDetection => true;
    public int NotificationCooldownSeconds => 30;
    public bool QuietHoursEnabled => false;
    public int QuietHoursStartHour => 22;
    public int QuietHoursEndHour => 7;
}
