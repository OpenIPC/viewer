using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;

namespace OpenIPC.Viewer.Core.Notifications;

// Cross-platform local notification sink (Phase 19.3), wired per platform like
// IAudioOutput: desktop toast, Android channel, iOS UNUserNotificationCenter.
// The NotificationCoordinator owns the policy (cooldown / quiet hours / type
// toggles); this contract just delivers one already-vetted notification.
public interface INotificationService
{
    // False when there's no native delivery (or permission denied) — the
    // coordinator skips delivery but the rest of the app is unaffected.
    bool IsAvailable { get; }

    void Show(NotificationRequest request);
}

public sealed record NotificationRequest(string Title, string Body, EventKind Kind, CameraId? CameraId = null);
