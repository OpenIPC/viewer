using System;
using System.Collections.Generic;

namespace OpenIPC.Viewer.Web;

// Bind knobs for the embedded web server. Defaults are deliberately safe:
// localhost-only, so nothing is exposed to the network until the user opts in
// (Phase 20 §20.7 — LAN/domain is an explicit, documented step behind a
// reverse-proxy, never the default).
public sealed record WebServerOptions
{
    public const int DefaultPort = 8787;

    // TCP port Kestrel listens on.
    public int Port { get; init; } = DefaultPort;

    // false (default) → bind 127.0.0.1, reachable only from this machine.
    // true → bind 0.0.0.0, reachable from the LAN. Opt-in and warned about.
    public bool BindLan { get; init; }

    public string BindHost => BindLan ? "0.0.0.0" : "127.0.0.1";

    public string Url => $"http://{BindHost}:{Port}";

    // Reverse-proxy IPs whose X-Forwarded-* headers are trusted (in addition to
    // loopback, which is always trusted). Set this when the proxy runs on a
    // different host than the server. Empty = only a same-host proxy is trusted.
    public IReadOnlyList<string> TrustedProxies { get; init; } = Array.Empty<string>();
}
