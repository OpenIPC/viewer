using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Notifications;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.App.Services;

// The in-app notification sound: picks the clip the user chose (built-in chime
// or their own file), applies the volume and plays it on a private audio stream.
// A custom file is decoded ONCE at import and stored as a plain WAV under
// AppDataDir/sounds, so playback needs neither FFmpeg nor the original file.
public sealed class NotificationSoundService
{
    public const string SoundChime = "chime";
    public const string SoundCustom = "custom";
    public const string SoundOff = "off";

    private static readonly TimeSpan MaxCustomLength = TimeSpan.FromSeconds(15);

    private readonly UserSettingsService _settings;
    private readonly IFileSystem _fs;
    private readonly NotificationSoundPlayer? _player;
    private readonly IAudioClipDecoder? _decoder;
    private readonly ILogger<NotificationSoundService> _log;
    private readonly object _gate = new();

    private PcmClip? _chime;
    private string? _customPath;
    private DateTime _customStamp;
    private PcmClip? _custom;

    public NotificationSoundService(
        UserSettingsService settings,
        IFileSystem fs,
        ILogger<NotificationSoundService> log,
        NotificationSoundPlayer? player = null,
        IAudioClipDecoder? decoder = null)
    {
        _settings = settings;
        _fs = fs;
        _log = log;
        _player = player;
        _decoder = decoder;
    }

    // No player on heads where the OS plays the notification sound itself
    // (Android channel, iOS) — the Settings page hides the sound controls.
    public bool IsAvailable => _player is not null;

    // Sound for a real notification, per the saved settings.
    public void PlayForNotification()
    {
        var s = _settings.Current;
        Play(s.NotificationSound, s.NotificationSoundFile, s.NotificationVolume);
    }

    // Preview from the Settings page with the values currently on screen.
    public void Play(string mode, string customFile, int volumePercent)
    {
        if (_player is null || volumePercent <= 0) return;
        var clip = Resolve(mode, customFile);
        if (clip is null) return;
        _player.Play(clip, Math.Min(100, volumePercent) / 100.0);
    }

    private PcmClip? Resolve(string mode, string customFile)
    {
        if (string.Equals(mode, SoundOff, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(mode, SoundCustom, StringComparison.OrdinalIgnoreCase))
        {
            var custom = LoadCustom(customFile);
            if (custom is not null) return custom;
            // A missing/broken custom file falls back to the chime rather than silence.
        }
        lock (_gate) return _chime ??= NotificationChime.Generate();
    }

    private PcmClip? LoadCustom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (_gate)
            {
                if (_custom is not null && _customPath == path && _customStamp == stamp)
                    return _custom;
            }
            using var stream = File.OpenRead(path);
            var clip = WavCodec.Read(stream);
            lock (_gate)
            {
                _custom = clip;
                _customPath = path;
                _customStamp = stamp;
            }
            return clip;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Custom notification sound {Path} could not be read", path);
            return null;
        }
    }

    // Decodes the picked file (any format FFmpeg reads; WAV even without FFmpeg),
    // trims it to MaxCustomLength and stores it as AppDataDir/sounds/notification.wav.
    // Returns the stored path. Throws with a readable message on a bad file.
    public async Task<string> ImportAsync(string sourcePath, CancellationToken ct)
    {
        PcmClip clip;
        var isWav = string.Equals(Path.GetExtension(sourcePath), ".wav", StringComparison.OrdinalIgnoreCase);
        if (isWav || _decoder is null)
        {
            try
            {
                using var stream = File.OpenRead(sourcePath);
                clip = WavCodec.Read(stream);
            }
            catch (InvalidDataException) when (_decoder is not null)
            {
                // e.g. a compressed WAV (ADPCM/µ-law) — let FFmpeg try.
                clip = await _decoder.DecodeAsync(sourcePath, MaxCustomLength, ct).ConfigureAwait(false);
            }
        }
        else
        {
            clip = await _decoder.DecodeAsync(sourcePath, MaxCustomLength, ct).ConfigureAwait(false);
        }
        clip = clip.Truncate(MaxCustomLength);

        var dir = Path.Combine(_fs.AppDataDir.FullName, "sounds");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "notification.wav");
        var tmp = target + ".tmp";
        using (var output = File.Create(tmp))
            WavCodec.Write(output, clip);
        File.Move(tmp, target, overwrite: true);

        lock (_gate)
        {
            _custom = clip;
            _customPath = target;
            _customStamp = File.GetLastWriteTimeUtc(target);
        }
        return target;
    }
}
