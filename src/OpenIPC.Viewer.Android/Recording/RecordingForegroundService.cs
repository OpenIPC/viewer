using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace OpenIPC.Viewer.Android.Recording;

// Android requires a foreground service for any audio/video capture that
// outlives the activity, otherwise the OS kills the process within minutes
// of backgrounding. We use ForegroundServiceType.DataSync because the
// activity is *recording media to disk* — `dataSync` is the right type per
// the foreground-service-types documentation (camera/microphone need
// Camera/Microphone type which would also force runtime permissions; we
// don't capture from a hardware sensor, we just remux an RTSP stream).
//
// The service is a thin notification holder. The actual recording loop
// lives in LibavformatRecordingSession owned by AndroidRecorder — service
// crashing while the session continues is the right failure mode (the
// recording finishes its file, just without keep-alive guarantees).
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class RecordingForegroundService : Service
{
    public const string ChannelId = "openipc.recording";
    public const int NotificationId = 0x0470;

    public const string ExtraCameraName = "openipc.cameraName";
    public const string ActionStart = "openipc.action.recording.start";
    public const string ActionStop = "openipc.action.recording.stop";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action ?? ActionStart;

        if (action == ActionStop)
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        EnsureChannel();
        var cameraName = intent?.GetStringExtra(ExtraCameraName) ?? "camera";
        var notification = BuildNotification(cameraName);

        // ServiceCompat.StartForeground is the API-level-aware wrapper —
        // on Android 14+ it requires the type bitmask to match the manifest.
        ServiceCompat.StartForeground(
            this,
            NotificationId,
            notification,
            (int)ForegroundService.TypeDataSync);

        return StartCommandResult.Sticky;
    }

    private void EnsureChannel()
    {
        // NotificationChannel is required since Android 8 (API 26); below
        // that this no-ops and the notification still appears.
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

        var channel = new NotificationChannel(
            ChannelId,
            "Recording",
            NotificationImportance.Low)
        {
            Description = "Persistent notification while a camera is being recorded.",
        };
        var nm = (NotificationManager?)GetSystemService(NotificationService);
        nm?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string cameraName)
    {
        // NotificationCompat.Builder setters return Builder? in the AndroidX
        // bindings — chaining triggers CS8602 even though the calls mutate
        // in place. Easier to call them as statements than scatter `!`s.
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("Recording");
        builder.SetContentText(cameraName);
        builder.SetSmallIcon(global::Android.Resource.Drawable.PresenceVideoOnline);
        builder.SetOngoing(true);
        builder.SetCategory(NotificationCompat.CategoryService);
        builder.SetPriority(NotificationCompat.PriorityLow);
        return builder.Build()!;
    }
}
