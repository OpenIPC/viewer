using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Notifications;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Infrastructure.Notifications;

// SMTP login/password as a generic credential in Windows Credential Manager
// (Control Panel → Credential Manager → Windows Credentials → "OpenIPC Viewer
// SMTP"), so the user can see, edit or remove it with the standard Windows tool.
// The blob is encrypted by Windows for the current user.
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialMailStore : IMailCredentialStore
{
    public const string TargetName = "OpenIPC Viewer SMTP";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string StorageName => "Windows Credential Manager";

    public Task<MailCredential?> GetAsync(CancellationToken ct)
    {
        if (!CredReadW(TargetName, CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return Task.FromResult<MailCredential?>(null);
            throw new InvalidOperationException($"CredRead failed: {err}");
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIALW>(ptr);
            var user = cred.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cred.UserName) ?? "";
            var password = "";
            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
            {
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                password = Encoding.Unicode.GetString(bytes);
            }
            return Task.FromResult<MailCredential?>(new MailCredential(user, password));
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public Task SetAsync(MailCredential credential, CancellationToken ct)
    {
        var blob = Encoding.Unicode.GetBytes(credential.Password ?? "");
        var target = Marshal.StringToHGlobalUni(TargetName);
        var user = Marshal.StringToHGlobalUni(credential.UserName ?? "");
        var comment = Marshal.StringToHGlobalUni("SMTP login used by OpenIPC Viewer to e-mail camera events");
        var blobPtr = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIALW
            {
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };
            if (!CredWriteW(ref cred, 0))
                throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            // Scrub the plaintext copy before releasing it.
            if (blob.Length > 0) Marshal.Copy(new byte[blob.Length], 0, blobPtr, blob.Length);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(comment);
            Marshal.FreeHGlobal(user);
            Marshal.FreeHGlobal(target);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        if (!CredDeleteW(TargetName, CredTypeGeneric, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ErrorNotFound)
                throw new InvalidOperationException($"CredDelete failed: {err}");
        }
        return Task.CompletedTask;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredWriteW(ref CREDENTIALW credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten; // FILETIME
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}

// Non-Windows heads: the same login/password in the platform secrets store
// (libsecret / Keychain / Android Keystore-backed file).
public sealed class SecretsStoreMailCredentialStore : IMailCredentialStore
{
    private const string UserKey = "notifications.smtp.user";
    private const string PasswordKey = "notifications.smtp.password";

    private readonly ISecretsStore _secrets;

    public SecretsStoreMailCredentialStore(ISecretsStore secrets) => _secrets = secrets;

    public string StorageName => "system secure storage";

    public async Task<MailCredential?> GetAsync(CancellationToken ct)
    {
        var user = await _secrets.GetAsync(UserKey, ct).ConfigureAwait(false);
        var password = await _secrets.GetAsync(PasswordKey, ct).ConfigureAwait(false);
        return user is null && password is null ? null : new MailCredential(user ?? "", password ?? "");
    }

    public async Task SetAsync(MailCredential credential, CancellationToken ct)
    {
        await _secrets.SetAsync(UserKey, credential.UserName ?? "", ct).ConfigureAwait(false);
        await _secrets.SetAsync(PasswordKey, credential.Password ?? "", ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await _secrets.RemoveAsync(UserKey, ct).ConfigureAwait(false);
        await _secrets.RemoveAsync(PasswordKey, ct).ConfigureAwait(false);
    }
}
