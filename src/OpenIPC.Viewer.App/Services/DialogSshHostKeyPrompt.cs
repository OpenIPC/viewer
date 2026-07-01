using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.App.Services;

/// <summary>
/// Surfaces the SSH host-key-changed decision through the app's confirm dialog,
/// which the DialogService renders as a modal window on desktop and a bottom
/// sheet on mobile — so the same trust flow works on every platform. SSH.NET
/// raises the key event on a background thread, so we marshal to the UI thread.
/// </summary>
public sealed class DialogSshHostKeyPrompt : ISshHostKeyPrompt
{
    private readonly IDialogService _dialogs;

    public DialogSshHostKeyPrompt(IDialogService dialogs) => _dialogs = dialogs;

    public Task<bool> ConfirmChangedKeyAsync(
        string host, int port, string? knownFingerprint, string presentedFingerprint, CancellationToken ct)
    {
        var title = Localizer.Instance["Ssh.HostKeyChanged.Title"];
        var message = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["Ssh.HostKeyChanged.Message"],
            $"{host}:{port}",
            presentedFingerprint,
            string.IsNullOrEmpty(knownFingerprint) ? "—" : knownFingerprint);

        return Dispatcher.UIThread.InvokeAsync(() => _dialogs.ConfirmAsync(
            title,
            message,
            Localizer.Instance["Ssh.HostKeyChanged.Trust"],
            Localizer.Instance["Common.Cancel"]));
    }
}
