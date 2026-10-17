using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Infrastructure.Notifications;

// E-mail through the user's default Windows mail client via Simple MAPI
// (MAPISendMailW in mapi32.dll) — the message goes out through whatever account
// is already set up there, so the app never needs SMTP settings or a password.
// Works with classic Outlook, Thunderbird, The Bat!, eM Client, …; the new
// Outlook and the built-in Mail app do not implement MAPI.
//
// Sent without a dialog (no MAPI_DIALOG). Some clients still show a compose
// window or a security prompt for programmatic sending — that is their policy.
// MAPI clients expect a single-threaded apartment, so each send gets its own
// STA thread.
[SupportedOSPlatform("windows")]
internal static class MapiMailSender
{
    private const uint MapiTo = 1;
    private const uint SuccessSuccess = 0;

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
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "mapi-send",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // The MAPI call itself can't be cancelled; ct only stops the wait.
        return tcs.Task.WaitAsync(ct);
    }

    private static void Send(OutgoingMail mail)
    {
        var recipSize = Marshal.SizeOf<MapiRecipDescW>();
        var allocated = new System.Collections.Generic.List<IntPtr>();
        IntPtr Str(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            allocated.Add(p);
            return p;
        }

        var recips = Marshal.AllocHGlobal(recipSize * mail.To.Count);
        try
        {
            for (var i = 0; i < mail.To.Count; i++)
            {
                var r = new MapiRecipDescW
                {
                    ulRecipClass = MapiTo,
                    lpszName = Str(mail.To[i]),
                    lpszAddress = Str("SMTP:" + mail.To[i]),
                };
                Marshal.StructureToPtr(r, recips + i * recipSize, false);
            }

            var msg = new MapiMessageW
            {
                lpszSubject = Str(mail.Subject),
                lpszNoteText = Str(mail.Body),
                nRecipCount = (uint)mail.To.Count,
                lpRecips = recips,
            };

            var rc = MAPISendMailW(IntPtr.Zero, IntPtr.Zero, ref msg, 0, 0);
            if (rc != SuccessSuccess)
                throw new InvalidOperationException(Describe(rc));
        }
        finally
        {
            Marshal.FreeHGlobal(recips);
            foreach (var p in allocated) Marshal.FreeHGlobal(p);
        }
    }

    private static string Describe(uint rc) => rc switch
    {
        1 => "The mail client cancelled sending (MAPI_USER_ABORT)",
        2 => "The mail client reported a failure (MAPI_E_FAILURE). Is a default mail program set and configured?",
        3 => "The mail client could not log on (MAPI_E_LOGON_FAILURE). Open it once and set up an account",
        14 => "A recipient address is unknown (MAPI_E_UNKNOWN_RECIPIENT)",
        21 => "A recipient address is ambiguous (MAPI_E_AMBIGUOUS_RECIPIENT)",
        23 => "The mail client reported a network failure (MAPI_E_NETWORK_FAILURE)",
        25 => "A recipient address is invalid (MAPI_E_INVALID_RECIPS)",
        26 => "The default mail client does not support MAPI (MAPI_E_NOT_SUPPORTED)",
        _ => $"Simple MAPI error {rc}",
    };

    [DllImport("mapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MAPISendMailW(IntPtr lhSession, IntPtr ulUIParam, ref MapiMessageW lpMessage, uint flFlags, uint ulReserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiMessageW
    {
        public uint ulReserved;
        public IntPtr lpszSubject;
        public IntPtr lpszNoteText;
        public IntPtr lpszMessageType;
        public IntPtr lpszDateReceived;
        public IntPtr lpszConversationID;
        public uint flFlags;
        public IntPtr lpOriginator;
        public uint nRecipCount;
        public IntPtr lpRecips;
        public uint nFileCount;
        public IntPtr lpFiles;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiRecipDescW
    {
        public uint ulReserved;
        public uint ulRecipClass;
        public IntPtr lpszName;
        public IntPtr lpszAddress;
        public uint ulEIDSize;
        public IntPtr lpEntryID;
    }
}
