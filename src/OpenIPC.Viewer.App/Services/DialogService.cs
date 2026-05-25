using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.App.Views.Dialogs;

namespace OpenIPC.Viewer.App.Services;

public sealed class DialogService : IDialogService
{
    public async Task<CameraEditorResult?> ShowCameraEditorAsync(CameraEditorViewModel viewModel)
    {
        var owner = ResolveOwner();
        if (owner is null)
            return null;

        var dlg = new CameraEditorWindow { DataContext = viewModel };
        return await dlg.ShowDialog<CameraEditorResult?>(owner);
    }

    public async Task<DiscoveryDialogResult?> ShowDiscoveryDialogAsync(DiscoveryDialogViewModel viewModel)
    {
        var owner = ResolveOwner();
        if (owner is null)
            return null;

        var dlg = new DiscoveryDialogWindow { DataContext = viewModel };
        return await dlg.ShowDialog<DiscoveryDialogResult?>(owner);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Delete", string cancelLabel = "Cancel")
    {
        var owner = ResolveOwner();
        if (owner is null)
            return false;

        var dlg = new ConfirmDialog();
        dlg.Configure(title, message, confirmLabel, cancelLabel);
        var result = await dlg.ShowDialog<bool?>(owner);
        return result == true;
    }

    public async Task<WelcomeResult> ShowWelcomeAsync()
    {
        var owner = ResolveOwner();
        if (owner is null)
            return WelcomeResult.Skip;

        var dlg = new WelcomeDialog();
        return await dlg.ShowDialog<WelcomeResult>(owner);
    }

    public async Task<string?> PickFolderAsync(string? title = null)
    {
        var owner = ResolveOwner();
        if (owner is null) return null;

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title ?? "Pick a folder",
            AllowMultiple = false,
        });
        var first = folders.FirstOrDefault();
        return first?.TryGetLocalPath();
    }

    public async Task<string?> PickImageFileAsync(string title)
    {
        var owner = ResolveOwner();
        if (owner is null) return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image files")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" },
                },
            },
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string title, string extension)
    {
        var owner = ResolveOwner();
        if (owner is null) return null;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType($"{extension.ToUpperInvariant()} file")
                {
                    Patterns = new[] { $"*.{extension}" },
                },
            },
        });
        return file?.TryGetLocalPath();
    }

    public async Task CopyFileToClipboardAsync(string path)
    {
        var owner = ResolveOwner();
        var clipboard = owner?.Clipboard;
        if (clipboard is null) return;

        if (File.Exists(path))
        {
            var file = await owner!.StorageProvider.TryGetFileFromPathAsync(path);
            if (file is not null)
            {
                await clipboard.SetValueAsync(DataFormat.File, (IStorageItem)file);
                return;
            }
        }

        // Fallback: copy the path as text (works in chat apps as a link/string).
        await clipboard.SetTextAsync(path);
    }

    public async Task ShowManageGroupsAsync(ManageGroupsViewModel viewModel)
    {
        var owner = ResolveOwner();
        if (owner is null) return;
        var dlg = new ManageGroupsDialog { DataContext = viewModel };
        await dlg.ShowDialog(owner);
    }

    public async Task<string?> ShowRawConfigEditorAsync(string initialJson)
    {
        var owner = ResolveOwner();
        if (owner is null) return null;
        var dlg = new RawConfigEditorDialog();
        dlg.SetInitialText(initialJson);
        return await dlg.ShowDialog<string?>(owner);
    }

    private static Window? ResolveOwner() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
