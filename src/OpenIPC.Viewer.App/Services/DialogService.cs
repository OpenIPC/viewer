using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.App.Views.Dialogs;

namespace OpenIPC.Viewer.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly UserSettingsService _settings;

    public DialogService(UserSettingsService settings) => _settings = settings;

    public Task<CameraEditorResult?> ShowCameraEditorAsync(CameraEditorViewModel viewModel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new CameraEditorContent { DataContext = viewModel };
            return OverlayDialogPresenter.ShowAsync(content, content.Completion);
        }

        var owner = ResolveOwner();
        if (owner is null) return Task.FromResult<CameraEditorResult?>(null);
        var dlg = new CameraEditorWindow { DataContext = viewModel };
        return dlg.ShowDialog<CameraEditorResult?>(owner);
    }

    public Task<DiscoveryDialogResult?> ShowDiscoveryDialogAsync(DiscoveryDialogViewModel viewModel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new DiscoveryDialogContent { DataContext = viewModel };
            return OverlayDialogPresenter.ShowAsync(content, content.Completion);
        }

        var owner = ResolveOwner();
        if (owner is null) return Task.FromResult<DiscoveryDialogResult?>(null);
        var dlg = new DiscoveryDialogWindow { DataContext = viewModel };
        return dlg.ShowDialog<DiscoveryDialogResult?>(owner);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Delete", string cancelLabel = "Cancel")
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new ConfirmDialogContent();
            content.Configure(title, message, confirmLabel, cancelLabel);
            return await OverlayDialogPresenter.ShowAsync(content, content.Completion).ConfigureAwait(true);
        }

        var owner = ResolveOwner();
        if (owner is null)
            return false;

        var dlg = new ConfirmDialog();
        dlg.Configure(title, message, confirmLabel, cancelLabel);
        var result = await dlg.ShowDialog<bool?>(owner);
        return result == true;
    }

    public async Task<string?> PromptAsync(string title, string initial, string okLabel, string cancelLabel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new PromptDialogContent();
            content.Configure(title, initial, okLabel, cancelLabel);
            return await OverlayDialogPresenter.ShowAsync(content, content.Completion).ConfigureAwait(true);
        }

        var owner = ResolveOwner();
        if (owner is null) return null;
        var dlg = new PromptDialog();
        dlg.Configure(title, initial, okLabel, cancelLabel);
        return await dlg.ShowDialog<string?>(owner).ConfigureAwait(true);
    }

    public Task<WelcomeResult> ShowWelcomeAsync()
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new WelcomeDialogContent();
            content.Configure(_settings);
            return OverlayDialogPresenter.ShowAsync(content, content.Completion);
        }

        var owner = ResolveOwner();
        if (owner is null) return Task.FromResult(WelcomeResult.Skip);
        var dlg = new WelcomeDialog();
        dlg.Configure(_settings);
        return dlg.ShowDialog<WelcomeResult>(owner);
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

    public async Task<string?> PickAnyFileAsync(string title)
    {
        var owner = ResolveTopLevel();
        if (owner is null) return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveTargetAsync(string suggestedName, string title)
    {
        var owner = ResolveTopLevel();
        if (owner is null) return null;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
        });
        return file?.TryGetLocalPath();
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
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new ManageGroupsContent { DataContext = viewModel };
            await OverlayDialogPresenter.ShowAsync(content, content.Completion).ConfigureAwait(true);
            return;
        }

        var owner = ResolveOwner();
        if (owner is null) return;
        var dlg = new ManageGroupsDialog { DataContext = viewModel };
        await dlg.ShowDialog(owner);
    }

    public async Task ShowFirmwareDialogAsync(FirmwareDialogViewModel viewModel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new FirmwareDialogContent { DataContext = viewModel };
            await OverlayDialogPresenter.ShowAsync(content, content.Completion, fullScreen: true).ConfigureAwait(true);
            return;
        }

        var owner = ResolveOwner();
        if (owner is null) return;
        var dlg = new FirmwareDialog { DataContext = viewModel };
        await dlg.ShowDialog(owner);
    }

    public async Task ShowHealthCenterAsync(HealthCenterViewModel viewModel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new HealthCenterContent { DataContext = viewModel };
            await OverlayDialogPresenter.ShowAsync(content, content.Completion).ConfigureAwait(true);
            return;
        }

        var owner = ResolveOwner();
        if (owner is null) return;
        var dlg = new HealthDialog { DataContext = viewModel };
        await dlg.ShowDialog(owner);
    }

    public async Task ShowManageLayoutCamerasAsync(ManageLayoutCamerasViewModel viewModel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new ManageLayoutCamerasContent { DataContext = viewModel };
            await OverlayDialogPresenter.ShowAsync(content, content.Completion).ConfigureAwait(true);
            return;
        }

        var owner = ResolveOwner();
        if (owner is null) return;
        var dlg = new ManageLayoutCamerasDialog { DataContext = viewModel };
        await dlg.ShowDialog(owner);
    }

    public Task<string?> ShowRawConfigEditorAsync(string initialJson, bool validateJson = true)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new RawConfigEditorContent();
            content.SetInitialText(initialJson);
            content.SetValidateJson(validateJson);
            return OverlayDialogPresenter.ShowAsync(content, content.Completion);
        }

        var owner = ResolveOwner();
        if (owner is null) return Task.FromResult<string?>(null);
        var dlg = new RawConfigEditorDialog();
        dlg.SetInitialText(initialJson);
        dlg.SetValidateJson(validateJson);
        return dlg.ShowDialog<string?>(owner);
    }

    public async Task OpenSshTerminalAsync(ViewModels.SshTerminalViewModel viewModel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new SshTerminalContent { DataContext = viewModel };
            await OverlayDialogPresenter.ShowAsync(content, content.Completion, fullScreen: true).ConfigureAwait(true);
            return;
        }

        var owner = ResolveOwner();
        // Non-modal: the user keeps the live view usable while a terminal is open.
        var window = new SshTerminalWindow { DataContext = viewModel };
        if (owner is null) window.Show();
        else window.Show(owner);
    }

    public async Task OpenFileManagerAsync(ViewModels.FileManagerViewModel viewModel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new FileManagerContent { DataContext = viewModel };
            await OverlayDialogPresenter.ShowAsync(content, content.Completion, fullScreen: true).ConfigureAwait(true);
            return;
        }

        var owner = ResolveOwner();
        var window = new FileManagerWindow { DataContext = viewModel };
        if (owner is null) window.Show();
        else window.Show(owner);
    }

    public async Task ShowImageViewerAsync(ViewModels.ImageViewerViewModel viewModel)
    {
        if (OverlayDialogPresenter.IsMobile)
        {
            var content = new ImageViewerContent { DataContext = viewModel };
            await OverlayDialogPresenter.ShowAsync(content, viewModel.Completion, fullScreen: true).ConfigureAwait(true);
            viewModel.Cleanup();
            return;
        }

        var owner = ResolveOwner();
        var window = new ImageViewerWindow { DataContext = viewModel };
        // Bridge both directions: the VM's Close command closes the window, and
        // closing the window (X / Esc-less) completes the VM so the awaiter
        // returns and cleanup runs.
        _ = viewModel.Completion.ContinueWith(
            _ => Dispatcher.UIThread.Post(window.Close),
            TaskScheduler.Default);
        window.Closing += (_, _) => viewModel.RequestClose();

        if (owner is null)
            window.Show();
        else
            await window.ShowDialog(owner).ConfigureAwait(true);

        viewModel.Cleanup();
    }

    public async Task<bool> OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var top = ResolveTopLevel();
        if (top?.Launcher is null)
            return false;

        return await top.Launcher.LaunchUriAsync(uri).ConfigureAwait(true);
    }

    private static Window? ResolveOwner() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    // Resolves the active TopLevel on both heads: the desktop MainWindow, or
    // the single-view MainView on mobile (where there is no Window).
    private static TopLevel? ResolveTopLevel()
    {
        Control? root = Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desk => desk.MainWindow,
            ISingleViewApplicationLifetime sv => sv.MainView,
            _ => null,
        };
        return root is null ? null : TopLevel.GetTopLevel(root);
    }
}
