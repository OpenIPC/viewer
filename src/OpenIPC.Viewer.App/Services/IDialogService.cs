using System.Threading.Tasks;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Services;

public interface IDialogService
{
    // Opens an interactive SSH terminal — a non-modal window on desktop, a
    // full-screen overlay on mobile (Phase 13.3).
    Task OpenSshTerminalAsync(SshTerminalViewModel viewModel);

    Task<CameraEditorResult?> ShowCameraEditorAsync(CameraEditorViewModel viewModel);

    Task<DiscoveryDialogResult?> ShowDiscoveryDialogAsync(DiscoveryDialogViewModel viewModel);

    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Delete", string cancelLabel = "Cancel");

    Task<WelcomeResult> ShowWelcomeAsync();

    Task<string?> PickFolderAsync(string? title = null);

    Task<string?> PickSaveFileAsync(string suggestedName, string title, string extension);

    Task<string?> PickImageFileAsync(string title);

    Task CopyFileToClipboardAsync(string path);

    Task ShowManageGroupsAsync(ManageGroupsViewModel viewModel);

    // Returns the edited JSON if the user clicked Apply, null if cancelled.
    Task<string?> ShowRawConfigEditorAsync(string initialJson);

    // Opens a URI in the system browser via the platform launcher. Returns
    // false if no TopLevel is available or the launch was rejected.
    Task<bool> OpenUrlAsync(string url);
}
