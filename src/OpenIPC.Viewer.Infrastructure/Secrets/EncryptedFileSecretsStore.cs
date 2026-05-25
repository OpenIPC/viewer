using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Infrastructure.Secrets;

// Linux fallback for when libsecret / D-Bus is unavailable (headless servers,
// CI runners, etc). AES-GCM with a per-install key derived via PBKDF2 from
// "machine entropy" — on Linux that's /etc/machine-id; tests inject a fixed
// value via the byte[] constructor.
//
// Threat model: same as DpapiSecretsStore — protects against drive-by file
// reads, NOT against a local attacker with root who can read /etc/machine-id
// and the salt file. Honest about being weaker than DPAPI/Keychain; see
// phase-08-linux-macos.md §8.1 for the "слабее, но не plaintext" framing.
public sealed class EncryptedFileSecretsStore : ISecretsStore
{
    private const int SaltLength = 32;
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int Pbkdf2Iterations = 100_000;

    private readonly string _storePath;
    private readonly string _saltPath;
    private readonly byte[] _keyMaterial;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedFileSecretsStore(DirectoryInfo appDataDir, byte[] keyMaterial)
    {
        if (keyMaterial is null || keyMaterial.Length == 0)
            throw new ArgumentException("Key material must be non-empty", nameof(keyMaterial));
        _storePath = Path.Combine(appDataDir.FullName, "secrets.bin");
        _saltPath = Path.Combine(appDataDir.FullName, "secrets.salt");
        _keyMaterial = (byte[])keyMaterial.Clone();
    }

    public static EncryptedFileSecretsStore ForLinux(DirectoryInfo appDataDir)
        => new(appDataDir, ReadMachineId());

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadAsync(ct).ConfigureAwait(false);
            if (!store.TryGetValue(key, out var encoded))
                return null;

            var blob = Convert.FromBase64String(encoded);
            if (blob.Length < NonceLength + TagLength)
                throw new CryptographicException("Stored secret is shorter than the AES-GCM envelope");

            var key32 = await DeriveKeyAsync(ct).ConfigureAwait(false);
            using var aes = new AesGcm(key32, TagLength);
            var nonce = blob.AsSpan(0, NonceLength);
            var tag = blob.AsSpan(blob.Length - TagLength, TagLength);
            var cipher = blob.AsSpan(NonceLength, blob.Length - NonceLength - TagLength);
            var plain = new byte[cipher.Length];
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        finally { _gate.Release(); }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadAsync(ct).ConfigureAwait(false);
            var key32 = await DeriveKeyAsync(ct).ConfigureAwait(false);
            var plain = Encoding.UTF8.GetBytes(value);
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagLength];

            using (var aes = new AesGcm(key32, TagLength))
                aes.Encrypt(nonce, plain, cipher, tag);

            var blob = new byte[NonceLength + cipher.Length + TagLength];
            Buffer.BlockCopy(nonce, 0, blob, 0, NonceLength);
            Buffer.BlockCopy(cipher, 0, blob, NonceLength, cipher.Length);
            Buffer.BlockCopy(tag, 0, blob, NonceLength + cipher.Length, TagLength);

            store[key] = Convert.ToBase64String(blob);
            await SaveAsync(store, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadAsync(ct).ConfigureAwait(false);
            if (store.Remove(key))
                await SaveAsync(store, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_storePath))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        await using var stream = File.OpenRead(_storePath);
        var data = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct)
            .ConfigureAwait(false);
        return data ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task SaveAsync(Dictionary<string, string> store, CancellationToken ct)
    {
        var tempPath = _storePath + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, store, cancellationToken: ct).ConfigureAwait(false);
        File.Move(tempPath, _storePath, overwrite: true);
    }

    private async Task<byte[]> DeriveKeyAsync(CancellationToken ct)
    {
        var salt = await LoadOrCreateSaltAsync(ct).ConfigureAwait(false);
        return Rfc2898DeriveBytes.Pbkdf2(_keyMaterial, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyLength);
    }

    private async Task<byte[]> LoadOrCreateSaltAsync(CancellationToken ct)
    {
        if (File.Exists(_saltPath))
            return await File.ReadAllBytesAsync(_saltPath, ct).ConfigureAwait(false);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var tempPath = _saltPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, salt, ct).ConfigureAwait(false);
        File.Move(tempPath, _saltPath, overwrite: true);
        return salt;
    }

    private static byte[] ReadMachineId()
    {
        // systemd places the install-unique ID in /etc/machine-id; D-Bus
        // sometimes uses /var/lib/dbus/machine-id (older distros). If neither
        // exists we synthesise from hostname — weaker, but deterministic per
        // host so secrets round-trip on the same machine.
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length > 0)
                    return Encoding.UTF8.GetBytes(text);
            }
        }
        return Encoding.UTF8.GetBytes("openipc-viewer-fallback:" + Environment.MachineName);
    }
}
