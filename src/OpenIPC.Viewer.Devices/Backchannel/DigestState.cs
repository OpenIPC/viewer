using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Devices.Backchannel;

// RTSP auth state (Phase 17.5). Holds the WWW-Authenticate challenge and builds
// the Authorization header per request — RTSP digest is the same algorithm as
// HTTP (RFC 2617): HA2 hashes the method+URI, so it must be recomputed each verb.
internal sealed class DigestState
{
    private readonly CameraCredentials? _creds;
    private bool _basic;
    private bool _digest;
    private string _realm = "";
    private string _nonce = "";
    private string _qop = "";
    private string _opaque = "";
    private int _nc;

    public DigestState(CameraCredentials? creds) => _creds = creds;

    public bool CanAuthenticate => _creds is not null;

    public void Challenge(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("WWW-Authenticate", out var h)) return;
        if (h.StartsWith("Basic", StringComparison.OrdinalIgnoreCase)) { _basic = true; return; }
        if (!h.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) return;

        _digest = true;
        _realm = Param(h, "realm") ?? "";
        _nonce = Param(h, "nonce") ?? "";
        _qop = Param(h, "qop") ?? "";
        _opaque = Param(h, "opaque") ?? "";
    }

    public string? BuildHeader(string method, string uri)
    {
        if (_creds is null) return null;
        if (_basic)
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_creds.Username}:{_creds.Password}"));
            return "Basic " + token;
        }
        if (!_digest) return null;

        var ha1 = Md5Hex($"{_creds.Username}:{_realm}:{_creds.Password}");
        var ha2 = Md5Hex($"{method}:{uri}");

        var sb = new StringBuilder("Digest ");
        sb.Append($"username=\"{_creds.Username}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\", ");

        if (_qop.Length > 0)
        {
            var nc = (++_nc).ToString("x8");
            var cnonce = Guid.NewGuid().ToString("N")[..16];
            var response = Md5Hex($"{ha1}:{_nonce}:{nc}:{cnonce}:auth:{ha2}");
            sb.Append($"response=\"{response}\", qop=auth, nc={nc}, cnonce=\"{cnonce}\"");
        }
        else
        {
            var response = Md5Hex($"{ha1}:{_nonce}:{ha2}");
            sb.Append($"response=\"{response}\"");
        }

        if (_opaque.Length > 0) sb.Append($", opaque=\"{_opaque}\"");
        return sb.ToString();
    }

    private static string? Param(string header, string key)
    {
        var idx = header.IndexOf(key + "=\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + key.Length + 2;
        var end = header.IndexOf('"', start);
        return end < 0 ? null : header[start..end];
    }

    private static string Md5Hex(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
