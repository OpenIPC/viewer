using System.Threading.Tasks;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Services;

public interface IDialogService
{
    // Opens an interactive SSH terminal — a non-modal window on desktop, a
    // full-screen overlay on mobile (Phase 13.3).
    Task OpenSshTerminalAsync(SshTerminalViewModel viewModel);

    // Opens the remote file manager (Phase 13.4) — window on desktop, overlay on mobile.
    Task OpenFileManagerAsync(FileManagerViewModel viewModel);

    // Opens the in-app snapshot viewer (Phase 14.4) — modal window on desktop,
    // full-screen overlay on mobile. Completes when the viewer is closed.
    Task ShowImageViewerAsync(ImageViewerViewModel viewModel);

    Task<CameraEditorResult?> ShowCameraEditorAsync(CameraEditorViewModel viewModel);

    Task<DiscoveryDialogResult?> ShowDiscoveryDialogAsync(DiscoveryDialogViewModel viewModel);

    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Delete", string cancelLabel = "Cancel");

    // Single-line text prompt. Returns the trimmed text, or null if cancelled
    // or left empty (Phase 13.4 — new remote folder name).
    Task<string?> PromptAsync(string title, string initial, string okLabel, string cancelLabel);

    Task<WelcomeResult> ShowWelcomeAsync();

    Task<string?> PickFolderAsync(string? title = null);

    Task<string?> PickSaveFileAsync(string suggestedName, string title, string extension);

    Task<string?> PickImageFileAsync(string title);

    // File manager (Phase 13.4): pick any local file to upload, or a save
    // target for a download. Cross-platform via StorageProvider.
    Task<string?> PickAnyFileAsync(string title);

    Task<string?> PickSaveTargetAsync(string suggestedName, string title);

    Task CopyFileToClipboardAsync(string path);

    Task ShowManageGroupsAsync(ManageGroupsViewModel viewModel);

    // Returns the edited JSON if the user clicked Apply, null if cancelled.
    Task<string?> ShowRawConfigEditorAsync(string initialJson);

    // Opens a URI in the system browser via the platform launcher. Returns
    // false if no TopLevel is available or the launch was rejected.
    Task<bool> OpenUrlAsync(string url);
}
