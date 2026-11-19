using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.Desktop.Notifications;

// Native Windows toast notifications (Action Center) for the "Windows" notification
// style. Talks to the WinRT ToastNotificationManager through raw vtable calls, so
// the desktop head stays a plain net9.0 build with no Windows SDK projection.
//
// An unpackaged app needs an AppUserModelID that Windows can resolve to a name
// and icon: we register it under HKCU\Software\Classes\AppUserModelId (the same
// mechanism the Windows App SDK uses for unpackaged apps). After the first toast
// the app is listed in Settings → System → Notifications, where the user tunes
// banners, sound, priority and Do Not Disturb. The process's own AUMID is left
// alone so pinned taskbar shortcuts keep grouping with the window.
//
// All WinRT work runs on one dedicated MTA thread: it owns the COM init and the
// cached notifier, and keeps the UI thread out of it.
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class WindowsToastNotifier : ISystemNotificationSink, IDisposable
{
    public const string AppUserModelId = "OpenIPC.Viewer";
    private const string DisplayName = "OpenIPC Viewer";

    private static readonly Guid IID_IToastNotificationManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid IID_IToastNotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IID_IXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IID_IXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");

    private readonly ILogger<WindowsToastNotifier> _log;
    private readonly string _iconPath;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private IntPtr _notifier; // IToastNotifier*, worker thread only
    private bool _registered;

    public WindowsToastNotifier(string appDataDir, ILogger<WindowsToastNotifier> log)
    {
        _log = log;
        _iconPath = Path.Combine(appDataDir, "notification-icon.png");
        _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "win-toast" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsSupported => true;

    public SystemNotificationState State
    {
        get
        {
            var result = SystemNotificationState.Unavailable;
            RunAndWait(() =>
            {
                var notifier = EnsureNotifier();
                // IToastNotifier::get_Setting (slot 8).
                int setting;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)VTable(notifier, 8))(notifier, &setting);
                if (hr < 0)
                {
                    _log.LogDebug("ToastNotifier.Setting failed: 0x{Hr:X8}", hr);
                    return;
                }
                result = setting switch
                {
                    0 => SystemNotificationState.Enabled,
                    1 => SystemNotificationState.DisabledForApp,
                    2 => SystemNotificationState.DisabledForUser,
                    _ => SystemNotificationState.DisabledByPolicy,
                };
            });
            return result;
        }
    }

    public void Show(NotificationRequest request)
    {
        var xml =
            "<toast><visual><binding template=\"ToastGeneric\">" +
            $"<text>{SecurityElement.Escape(request.Title)}</text>" +
            $"<text>{SecurityElement.Escape(request.Body)}</text>" +
            "</binding></visual></toast>";
        try
        {
            _work.Add(() =>
            {
                try
                {
                    ShowCore(xml);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Windows toast failed");
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Disposed (app shutting down) — nothing to show it with.
        }
    }

    public void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open Windows notification settings");
        }
    }

    // Writes the AUMID display name + icon so Windows can show who the toast is
    // from. Idempotent; cheap enough to run once per process.
    private void EnsureRegistered()
    {
        if (_registered) return;
        try
        {
            if (!File.Exists(_iconPath))
            {
                using var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://OpenIPC.Viewer.App/Assets/openipc-logo.png"));
                using var dst = File.Create(_iconPath);
                src.CopyTo(dst);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not write the toast icon; toasts will show without it");
        }

        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}"))
        {
            key.SetValue("DisplayName", DisplayName);
            if (File.Exists(_iconPath)) key.SetValue("IconUri", _iconPath);
            key.SetValue("IconBackgroundColor", "FF1E1E1E");
        }
        _registered = true;
    }

    private IntPtr EnsureNotifier()
    {
        if (_notifier != IntPtr.Zero) return _notifier;
        EnsureRegistered();

        var statics = GetActivationFactory("Windows.UI.Notifications.ToastNotificationManager", IID_IToastNotificationManagerStatics);
        try
        {
            var aumid = CreateHString(AppUserModelId);
            try
            {
                IntPtr notifier;
                // IToastNotificationManagerStatics::CreateToastNotifierWithId (slot 7).
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)VTable(statics, 7))(statics, aumid, &notifier);
                Check(hr, "CreateToastNotifierWithId");
                _notifier = notifier;
            }
            finally
            {
                WindowsDeleteString(aumid);
            }
        }
        finally
        {
            Release(statics);
        }
        return _notifier;
    }

    private void ShowCore(string xml)
    {
        var notifier = EnsureNotifier();

        // XmlDocument → LoadXml(xml).
        var docClass = CreateHString("Windows.Data.Xml.Dom.XmlDocument");
        IntPtr docInspectable;
        try
        {
            Check(RoActivateInstance(docClass, out docInspectable), "RoActivateInstance(XmlDocument)");
        }
        finally
        {
            WindowsDeleteString(docClass);
        }

        IntPtr docIo = IntPtr.Zero, doc = IntPtr.Zero, factory = IntPtr.Zero, toast = IntPtr.Zero;
        try
        {
            docIo = QueryInterface(docInspectable, IID_IXmlDocumentIO);
            var content = CreateHString(xml);
            try
            {
                // IXmlDocumentIO::LoadXml (slot 6).
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)VTable(docIo, 6))(docIo, content);
                Check(hr, "XmlDocument.LoadXml");
            }
            finally
            {
                WindowsDeleteString(content);
            }

            doc = QueryInterface(docInspectable, IID_IXmlDocument);
            factory = GetActivationFactory("Windows.UI.Notifications.ToastNotification", IID_IToastNotificationFactory);

            // IToastNotificationFactory::CreateToastNotification (slot 6).
            IntPtr created;
            var hr2 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)VTable(factory, 6))(factory, doc, &created);
            Check(hr2, "CreateToastNotification");
            toast = created;

            // IToastNotifier::Show (slot 6).
            var hr3 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)VTable(notifier, 6))(notifier, toast);
            Check(hr3, "ToastNotifier.Show");
        }
        finally
        {
            Release(toast);
            Release(factory);
            Release(doc);
            Release(docIo);
            Release(docInspectable);
        }
    }

    // --- worker thread -----------------------------------------------------

    private void WorkerLoop()
    {
        // RO_INIT_MULTITHREADED. S_FALSE (already initialized) is fine; this
        // thread lives as long as the process, so there is no matching uninit.
        _ = RoInitialize(1);
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { _log.LogDebug(ex, "Toast worker action failed"); }
        }
        Release(_notifier);
        _notifier = IntPtr.Zero;
    }

    private void RunAndWait(Action action)
    {
        using var done = new ManualResetEventSlim();
        try
        {
            _work.Add(() =>
            {
                try { action(); }
                catch (Exception ex) { _log.LogDebug(ex, "Toast state query failed"); }
                finally { done.Set(); }
            });
        }
        catch (InvalidOperationException)
        {
            return; // disposed
        }
        done.Wait(TimeSpan.FromSeconds(3));
    }

    public void Dispose() => _work.CompleteAdding();

    // --- WinRT plumbing ----------------------------------------------------

    private static void* VTable(IntPtr obj, int slot) => (*(void***)obj)[slot];

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        var hr = Marshal.QueryInterface(obj, in iid, out var result);
        Check(hr, $"QueryInterface({iid})");
        return result;
    }

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) Marshal.Release(obj);
    }

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        var hs = CreateHString(className);
        try
        {
            Check(RoGetActivationFactory(hs, in iid, out var factory), $"RoGetActivationFactory({className})");
            return factory;
        }
        finally
        {
            WindowsDeleteString(hs);
        }
    }

    private static IntPtr CreateHString(string value)
    {
        Check(WindowsCreateString(value, value.Length, out var hs), "WindowsCreateString");
        return hs;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed: 0x{hr:X8}", hr);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
