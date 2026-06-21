using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.Android.Platform;

// Android local notifications via NotificationManager (Phase 19.3). One channel
// for camera events; each Show posts a fresh notification. Delivery needs the
// POST_NOTIFICATIONS runtime permission (Android 13+, requested at startup) —
// if denied, Notify is a silent no-op, which is fine.
public sealed class AndroidNotificationService : INotificationService
{
    private const string ChannelId = "openipc.events";

    private readonly Context _context;
    private int _nextId = 2000;

    public AndroidNotificationService(Context context)
    {
        _context = context;
        CreateChannel();
    }

    public bool IsAvailable => true;

    private void CreateChannel()
    {
        if (_context.GetSystemService(Context.NotificationService) is not NotificationManager mgr) return;
        var channel = new NotificationChannel(ChannelId, "Camera events", NotificationImportance.Default)
        {
            Description = "Motion and object-detection alerts",
        };
        mgr.CreateNotificationChannel(channel);
    }

    public void Show(NotificationRequest request)
    {
        if (_context.GetSystemService(Context.NotificationService) is not NotificationManager mgr) return;

        var notification = new Notification.Builder(_context, ChannelId)
            .SetContentTitle(request.Title)!
            .SetContentText(request.Body)!
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)!
            .SetAutoCancel(true)!
            .Build();

        mgr.Notify(Interlocked.Increment(ref _nextId), notification);
    }
}
