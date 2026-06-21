namespace OpenIPC.Viewer.Core.Notifications;

// Fallback when a head has no native notifier yet. IsAvailable=false; Show is a
// no-op. Phase 19.3.
public sealed class NullNotificationService : INotificationService
{
    public bool IsAvailable => false;
    public void Show(NotificationRequest request) { }
}
