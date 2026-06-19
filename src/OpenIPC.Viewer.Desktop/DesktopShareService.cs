using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop;

/// <summary>
/// Desktop has no native share sheet, so "share" reveals the file in the OS
/// file manager (Explorer / Finder / the default file manager on Linux). The
/// viewer/browser also offer copy-to-clipboard separately.
/// </summary>
public sealed class DesktopShareService : IShareService
{
    public bool SupportsSystemShare => false;

    public Task ShareFileAsync(string path, string? mimeType, CancellationToken ct) => RevealInFolderAsync(path);

    public Task RevealInFolderAsync(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"-R \"{path}\""));
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\""));
            }
        }
        catch
        {
            // Best-effort — a missing file manager shouldn't surface as an error.
        }
        return Task.CompletedTask;
    }
}
