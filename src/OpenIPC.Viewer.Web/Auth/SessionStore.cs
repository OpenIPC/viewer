using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenIPC.Viewer.Web.Auth;

// In-memory session store. Tokens are 256-bit opaque values handed to the
// client; only their SHA-256 digest is kept here — the raw token is never
// stored, so a store dump can't be replayed. Sliding TTL: each successful
// validation extends the expiry. Thread-safe for concurrent requests.
//
// In-memory is fine for a single-process self-host server; a fresh start simply
// invalidates all sessions (users log in again). Durable sessions can come with
// the backend slice if needed.
public sealed class SessionStore
{
    private sealed class Entry
    {
        public required WebIdentity Identity { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _byDigest = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public SessionStore(WebAuthOptions options) => _ttl = options.SessionTtl;

    public int ActiveCount => _byDigest.Count;

    // Mints a new token for an authenticated identity. Returns the raw token
    // (shown to the client once) and its expiry.
    public (string Token, DateTimeOffset ExpiresUtc) Create(WebIdentity identity)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var entry = new Entry { Identity = identity, CreatedUtc = now, ExpiresUtc = now + _ttl };
        _byDigest[Digest(token)] = entry;
        return (token, entry.ExpiresUtc);
    }

    // Returns the identity for a live token, sliding its expiry forward. Null
    // when unknown or expired (expired entries are evicted on touch).
    public WebIdentity? Validate(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var digest = Digest(token);
        if (!_byDigest.TryGetValue(digest, out var entry))
            return null;

        var now = DateTimeOffset.UtcNow;
        if (now >= entry.ExpiresUtc)
        {
            _byDigest.TryRemove(digest, out _);
            return null;
        }

        entry.ExpiresUtc = now + _ttl;
        return entry.Identity;
    }

    public bool Revoke(string token) => _byDigest.TryRemove(Digest(token), out _);

    public int RevokeAll()
    {
        var count = _byDigest.Count;
        _byDigest.Clear();
        return count;
    }

    private static string Digest(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
