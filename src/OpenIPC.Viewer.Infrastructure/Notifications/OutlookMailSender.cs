using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Infrastructure.Notifications;

// E-mail by automating an installed classic Outlook ("Outlook.Application" COM
// server): attaches to the running Outlook or starts it, creates a MailItem and
// sends it from the default account. Late-bound through IDispatch so there is
// no build-time dependency on the Office interop assemblies.
//
// Outlook's object-model guard may ask the user to allow programmatic sending
// when no up-to-date antivirus is registered — that prompt is Outlook's. The
// new Outlook (olk.exe) has no COM automation.
[SupportedOSPlatform("windows")]
internal static class OutlookMailSender
{
    private const int OlMailItem = 0;

    public static Task SendAsync(OutgoingMail mail, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Send(mail);
                tcs.TrySetResult();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                tcs.TrySetException(ex.InnerException);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "outlook-send",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task.WaitAsync(ct);
    }

    private static void Send(OutgoingMail mail)
    {
        var type = Type.GetTypeFromProgID("Outlook.Application")
            ?? throw new InvalidOperationException("Classic Outlook is not installed (no Outlook.Application COM server)");

        object? app = null, item = null;
        try
        {
            app = Activator.CreateInstance(type)
                  ?? throw new InvalidOperationException("Could not start Outlook");
            item = Invoke(app, "CreateItem", OlMailItem)
                   ?? throw new InvalidOperationException("Outlook did not create a mail item");
            Set(item, "To", string.Join("; ", mail.To));
            Set(item, "Subject", mail.Subject);
            Set(item, "Body", mail.Body);
            Invoke(item, "Send");
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            if (app is not null && Marshal.IsComObject(app)) Marshal.ReleaseComObject(app);
        }
    }

    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args,
            System.Globalization.CultureInfo.InvariantCulture);

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value },
            System.Globalization.CultureInfo.InvariantCulture);
}
