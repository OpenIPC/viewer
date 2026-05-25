using System.IO;
using System.Runtime.Versioning;
using System.Text;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Infrastructure.Secrets;
using UIKit;

namespace OpenIPC.Viewer.iOS.Platform;

// Phase 10a uses the AES-GCM file fallback (same code path as headless
// Linux and Android), keyed off UIDevice.identifierForVendor — stable per
// app-vendor per device, gone when the user uninstalls every app from the
// vendor (acceptable: that's also when our credentials should disappear).
// Phase 10b will swap in real Keychain via Security.framework P/Invoke for
// keystore-backed protection.
[SupportedOSPlatform("ios")]
public sealed class IosSecretsStore : ISecretsStore
{
    private readonly EncryptedFileSecretsStore _inner;

    public IosSecretsStore(DirectoryInfo appDataDir)
    {
        var vendorId = UIDevice.CurrentDevice.IdentifierForVendor?.AsString()
                       ?? "openipc-viewer-fallback";
        var keyMaterial = Encoding.UTF8.GetBytes(vendorId);
        _inner = new EncryptedFileSecretsStore(appDataDir, keyMaterial);
    }

    public System.Threading.Tasks.Task<string?> GetAsync(string key, System.Threading.CancellationToken ct)
        => _inner.GetAsync(key, ct);

    public System.Threading.Tasks.Task SetAsync(string key, string value, System.Threading.CancellationToken ct)
        => _inner.SetAsync(key, value, ct);

    public System.Threading.Tasks.Task RemoveAsync(string key, System.Threading.CancellationToken ct)
        => _inner.RemoveAsync(key, ct);
}
