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
            var clip = WavCodec.Read(stream, MaxCustomLength);
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

    // The managed WAV reader, bounded to MaxCustomLength. Null when the file
    // isn't a WAV it can read (then FFmpeg gets a go).
    private PcmClip? TryReadWav(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return WavCodec.Read(stream, MaxCustomLength);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    // Decodes the picked file (any format FFmpeg reads; WAV even without FFmpeg),
    // trims it to MaxCustomLength and stores it as AppDataDir/sounds/notification.wav.
    // Returns the stored path. Throws with a readable message on a bad file.
    public async Task<string> ImportAsync(string sourcePath, CancellationToken ct)
    {
        // Off the UI thread: even a bounded read of a big file takes a moment.
        var clip = await Task.Run(() => TryReadWav(sourcePath), ct).ConfigureAwait(false);
        if (clip is null)
        {
            if (_decoder is null)
                throw new InvalidDataException("Only WAV files can be used on this device");
            // Compressed audio, or a WAV encoding the managed reader doesn't do
            // (ADPCM, µ-law…) — let FFmpeg decode it.
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
