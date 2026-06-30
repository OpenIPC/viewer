using System;
using System.Security.Cryptography;
using System.Text;

namespace OpenIPC.Viewer.Infrastructure.Persistence;

// Symmetric encryption for camera credentials carried in a shared config file
// (Phase 20, optional). A passphrase known to the fleet derives an AES-256-GCM
// key via PBKDF2; only holders of the passphrase can read the passwords, so the
// file on a network share doesn't leak credentials on its own.
//
// Per-file salt (stored once at the document root) keeps one PBKDF2 derivation
// per export/import; each value gets its own random nonce. Blob layout:
// base64( nonce(12) || tag(16) || ciphertext ).
internal static class CredentialCipher
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static string Encrypt(string plaintext, string passphrase, byte[] salt)
    {
        var key = DeriveKey(passphrase, salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(blob);
    }

    // Returns null on any failure (wrong passphrase, tampered blob, malformed
    // input) — callers treat a failed decrypt as "no credentials available".
    public static string? TryDecrypt(string blobBase64, string passphrase, byte[] salt)
    {
        try
        {
            var blob = Convert.FromBase64String(blobBase64);
            if (blob.Length < NonceSize + TagSize) return null;

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipher = new byte[blob.Length - NonceSize - TagSize];
            Buffer.BlockCopy(blob, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(blob, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(blob, NonceSize + TagSize, cipher, 0, cipher.Length);

            var key = DeriveKey(passphrase, salt);
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, TagSize))
                gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
}
