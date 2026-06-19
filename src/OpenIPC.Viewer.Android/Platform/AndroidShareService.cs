using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.Core.Content;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Android.Platform;

/// <summary>
/// Android native share (Phase 14.6 / Phase 11 "snapshot share sheet"). Hands
/// the receiving app a temporary content:// URI via FileProvider — file:// URIs
/// throw FileUriExposedException on modern Android.
/// </summary>
[SupportedOSPlatform("android")]
public sealed class AndroidShareService : IShareService
{
    private readonly Context _context;

    public AndroidShareService(Context context) => _context = context;

    public bool SupportsSystemShare => true;

    public Task ShareFileAsync(string path, string? mimeType, CancellationToken ct)
    {
        var file = new Java.IO.File(path);
        var authority = _context.PackageName + ".fileprovider";
        var uri = FileProvider.GetUriForFile(_context, authority, file);

        var intent = new Intent(Intent.ActionSend);
        intent.SetType(mimeType ?? "image/jpeg");
        intent.PutExtra(Intent.ExtraStream, uri);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);

        var chooser = Intent.CreateChooser(intent, (string?)null);
        // Started from the application context (not an Activity), so a new task
        // is required.
        chooser?.AddFlags(ActivityFlags.NewTask);
        _context.StartActivity(chooser);
        return Task.CompletedTask;
    }

    public Task RevealInFolderAsync(string path) => Task.CompletedTask;
}
