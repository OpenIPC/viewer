using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.App.Services;

// The operating system's own notification center as an alternative to the
// app's corner toast (desktop "Notification style" setting). Today that is the
// Windows Action Center: the app shows up in Settings → System → Notifications,
// where the user controls banners, sound, priority and Do Not Disturb with the
// standard Windows tools. Registered by the Windows head only.
public interface ISystemNotificationSink
{
    // False on systems without native toasts (e.g. before Windows 10).
    bool IsSupported { get; }

    // Whether Windows will actually display our toasts right now.
    SystemNotificationState State { get; }

    void Show(NotificationRequest request);

    // Opens the OS notification settings page.
    void OpenSettings();
}

public enum SystemNotificationState
{
    Enabled,
    DisabledForApp,
    DisabledForUser,
    DisabledByPolicy,
    Unavailable,
}
