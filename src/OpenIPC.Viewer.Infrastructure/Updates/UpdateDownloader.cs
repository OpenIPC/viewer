using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Updates;

namespace OpenIPC.Viewer.Infrastructure.Updates;

// Downloads an update archive into the user's Downloads folder — the user unpacks it themselves;
// nothing here replaces the running app. Writes to "<name>.part" and renames only after the hash
// matches, so a cut connection or a tampered file never leaves a plausible-looking archive behind.
public sealed class UpdateDownloader : IUpdateDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<UpdateDownloader> _log;

    public UpdateDownloader(HttpClient http, ILogger<UpdateDownloader> log)
    {
        _http = http;
        _log = log;
    }

    public bool SupportsInAppDownload => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    public (string Platform, string Arch) CurrentTarget => (CurrentPlatform(), CurrentArch());

    public async Task<string> DownloadAsync(UpdateAsset asset, IProgress<double>? progress, CancellationToken ct)
    {
        // Never a file nobody can check: the caller sends hashless assets to the browser instead.
        if (asset.Sha256 is not { } expected)
            throw new InvalidDataException($"Update {asset.FileName} has no published SHA-256.");
        if (asset.Url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"Update {asset.FileName} is not served over https.");

        var dir = Directory.CreateDirectory(DownloadsDirectory());
        var target = Path.Combine(dir.FullName, Path.GetFileName(asset.FileName));

        // A previous download of the same build: reuse it instead of fetching 200 MB again.
        if (File.Exists(target)
            && string.Equals(await HashAsync(target, ct).ConfigureAwait(false), expected, StringComparison.Ordinal))
        {
            progress?.Report(1);
            return target;
        }

        _log.LogInformation("Downloading update {File} from {Url}", asset.FileName, asset.Url);
        var tmp = target + ".part";
        try
        {
            using var resp = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? asset.Size;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var dst = File.Create(tmp))
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }

            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Update {asset.FileName} failed its integrity check (expected {expected}, got {actual}).");

            File.Move(tmp, target, overwrite: true);
            _log.LogInformation("Update saved to {Path}", target);
            return target;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string CurrentPlatform()
    {
        // Android first: it is Linux underneath, and the feeds tell the two apart.
        if (OperatingSystem.IsAndroid()) return UpdatePlatforms.Android;
        if (OperatingSystem.IsIOS()) return UpdatePlatforms.Ios;
        if (OperatingSystem.IsWindows()) return UpdatePlatforms.Windows;
        if (OperatingSystem.IsMacOS()) return UpdatePlatforms.MacOs;
        return UpdatePlatforms.Linux;
    }

    private static string CurrentArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "armv7",
        _ => "x64",
    };

    // The shell's Downloads folder (a user may have moved it); on Linux $XDG_DOWNLOAD_DIR, the
    // same env-only reading of the user-dirs spec LinuxFileSystem uses; else ~/Downloads.
    private static string DownloadsDirectory()
    {
        if (OperatingSystem.IsWindows()
            && SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var p) == 0)
        {
            try
            {
                var path = Marshal.PtrToStringUni(p);
                if (!string.IsNullOrEmpty(path)) return path;
            }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)) return xdg;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Downloads");
    }

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
