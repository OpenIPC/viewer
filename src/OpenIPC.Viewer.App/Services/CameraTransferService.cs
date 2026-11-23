using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Persistence;

namespace OpenIPC.Viewer.App.Services;

// One-shot copy of cameras (including passwords) from the Viewer that already
// has them to another screen on the same LAN. The two sides agree on a key with
// ECDH, so a listener on the network never sees the passphrase, and both screens
// show the same short code. Nothing is exported until that code is allowed.
public sealed class CameraTransferService
{
    public const int DiscoveryPort = 28787;
    private const int MaxHandlers = 4;
    private static readonly TimeSpan HeaderDeadline = TimeSpan.FromSeconds(15);

    private readonly IConfigBackupService _backup;
    private readonly ILogger<CameraTransferService> _log;
    private int _approving;

    public CameraTransferService(IConfigBackupService backup, ILogger<CameraTransferService> log)
    {
        _backup = backup;
        _log = log;
    }

    public static string DeviceName
    {
        get
        {
            var name = Environment.MachineName;
            if (string.IsNullOrWhiteSpace(name)) name = "Viewer";
            name = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return name.Length <= 40 ? name : name[..40];
        }
    }

    // Listen until cancelled, or until one copy has been sent. approve runs on a
    // thread-pool thread and may block on a UI dialog; true sends this machine's
    // cameras, false refuses. onFailure reports a problem with one client and
    // does not stop the listener.
    public async Task HostAsync(
        Func<CameraTransferApproval, CancellationToken, Task<bool>> approve,
        CancellationToken ct,
        Action<string>? onFailure = null)
    {
        using var udp = new UdpClient(DiscoveryPort);
        using var tcp = new TcpListener(IPAddress.Any, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(MaxHandlers);
        var handlers = new List<Task>();

        var discovery = ReplyToDiscoveryAsync(udp, port, ct);
        try
        {
            while (!session.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await tcp.AcceptTcpClientAsync(session.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }

                if (!await slots.WaitAsync(0).ConfigureAwait(false))
                {
                    client.Dispose();
                    continue;
                }

                handlers.Add(RunHandler());

                async Task RunHandler()
                {
                    try
                    {
                        if (await HandleClientAsync(client, approve, ct, onFailure).ConfigureAwait(false))
                            session.Cancel();
                    }
                    finally { slots.Release(); }
                }
            }
        }
        finally
        {
            session.Cancel();
            tcp.Stop();
            udp.Close();
            try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await Task.WhenAll(handlers).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    public async Task<IReadOnlyList<CameraTransferOffer>> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(0);
        udp.EnableBroadcast = true;
        var probe = Encoding.ASCII.GetBytes("OIPC1\nDISCOVER\n");
        var found = new Dictionary<string, CameraTransferOffer>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + timeout;

        foreach (var target in BroadcastTargets())
        {
            try { await udp.SendAsync(probe, target, ct).ConfigureAwait(false); }
            catch (SocketException) { }
        }

        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(remaining);
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }

            if (!TryParseHostReply(packet.Buffer, out var name, out var port)) continue;
            var address = packet.RemoteEndPoint.Address.ToString();
            found[address + ":" + port] = new CameraTransferOffer(name, address, port);
        }

        return new List<CameraTransferOffer>(found.Values);
    }

    // Blocks until the host allows or refuses. onCode receives the pairing code
    // as soon as both sides can compute it, which is before the host decides.
    public async Task<CameraTransferPayload> PullAsync(
        CameraTransferOffer offer, CancellationToken ct, Action<string>? onCode = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Parse(offer.Address), offer.Port, token).ConfigureAwait(false);
        tcp.ReceiveTimeout = 120_000;
        tcp.SendTimeout = 15_000;
        await using var stream = tcp.GetStream();

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var hello = Encoding.ASCII.GetBytes($"OIPC1\nPULL\n{DeviceName}\n{mine}\n");
        await stream.WriteAsync(hello, token).ConfigureAwait(false);

        var magic = await ReadLineAsync(stream, 16, token).ConfigureAwait(false);
        var status = await ReadLineAsync(stream, 16, token).ConfigureAwait(false);
        if (magic != "OIPC1")
            throw new IOException("Unexpected reply from the other Viewer.");
        if (status == "NO")
            throw new CameraTransferDeniedException();
        if (status != "CODE")
            throw new IOException("Unexpected reply from the other Viewer.");

        var theirs = await ReadLineAsync(stream, 256, token).ConfigureAwait(false);
        var secret = SharedSecret(ecdh, theirs);
        onCode?.Invoke(PairingCode(secret));

        magic = await ReadLineAsync(stream, 16, token).ConfigureAwait(false);
        status = await ReadLineAsync(stream, 16, token).ConfigureAwait(false);
        if (magic != "OIPC1")
            throw new IOException("Unexpected reply from the other Viewer.");
        if (status == "NO")
            throw new CameraTransferDeniedException();
        if (status != "OK")
            throw new IOException("Unexpected reply from the other Viewer.");

        var lengthText = await ReadLineAsync(stream, 16, token).ConfigureAwait(false);
        if (!int.TryParse(lengthText, out var length) || length < 0 || length > 8_000_000)
            throw new IOException("The other Viewer sent a config this app cannot read.");
        var jsonBytes = await ReadExactAsync(stream, length, token).ConfigureAwait(false);
        return new CameraTransferPayload(Encoding.UTF8.GetString(jsonBytes), Passphrase(secret));
    }

    private async Task ReplyToDiscoveryAsync(UdpClient udp, int tcpPort, CancellationToken ct)
    {
        var reply = Encoding.ASCII.GetBytes($"OIPC1\nHOST\n{DeviceName}\n{tcpPort}\n");
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }

            var text = Encoding.ASCII.GetString(packet.Buffer);
            if (!text.StartsWith("OIPC1\nDISCOVER", StringComparison.Ordinal)) continue;
            try { await udp.SendAsync(reply, packet.RemoteEndPoint, ct).ConfigureAwait(false); }
            catch (SocketException) { }
            catch (OperationCanceledException) { break; }
        }
    }

    // True once a copy was actually sent, so the host can stop listening.
    private async Task<bool> HandleClientAsync(
        TcpClient client,
        Func<CameraTransferApproval, CancellationToken, Task<bool>> approve,
        CancellationToken ct,
        Action<string>? onFailure)
    {
        using (client)
        {
            client.ReceiveTimeout = 15_000;
            client.SendTimeout = 30_000;
            try
            {
                var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                await using var stream = client.GetStream();
                // A client that never finishes its hello must not pin a handler for the
                // whole time sharing stays up. The approval itself waits on the host token.
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headers.CancelAfter(HeaderDeadline);
                var magic = await ReadLineAsync(stream, 16, headers.Token).ConfigureAwait(false);
                var verb = await ReadLineAsync(stream, 16, headers.Token).ConfigureAwait(false);
                var name = await ReadLineAsync(stream, 80, headers.Token).ConfigureAwait(false);
                var theirKey = await ReadLineAsync(stream, 256, headers.Token).ConfigureAwait(false);
                if (magic != "OIPC1" || verb != "PULL" || string.IsNullOrEmpty(theirKey))
                {
                    await WriteAsciiAsync(stream, "OIPC1\nNO\n", ct).ConfigureAwait(false);
                    return false;
                }

                // One dialog at a time. Anyone else is refused instead of stacking prompts.
                if (Interlocked.CompareExchange(ref _approving, 1, 0) != 0)
                {
                    await WriteAsciiAsync(stream, "OIPC1\nNO\n", ct).ConfigureAwait(false);
                    return false;
                }

                try
                {
                    using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                    var secret = SharedSecret(ecdh, theirKey);
                    var code = PairingCode(secret);
                    var mine = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
                    await WriteAsciiAsync(stream, $"OIPC1\nCODE\n{mine}\n", ct).ConfigureAwait(false);

                    var allowed = false;
                    try
                    {
                        allowed = await approve(new CameraTransferApproval(name, address, code), ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { allowed = false; }

                    if (!allowed || ct.IsCancellationRequested)
                    {
                        await WriteAsciiAsync(stream, "OIPC1\nNO\n", ct).ConfigureAwait(false);
                        return false;
                    }

                    var json = await _backup.ExportAsync(Passphrase(secret), ct).ConfigureAwait(false);
                    var body = Encoding.UTF8.GetBytes(json);
                    await WriteAsciiAsync(stream, $"OIPC1\nOK\n{body.Length}\n", ct).ConfigureAwait(false);
                    await stream.WriteAsync(body, ct).ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    Interlocked.Exchange(ref _approving, 0);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                or ObjectDisposedException or FormatException or CryptographicException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Camera transfer failed");
                onFailure?.Invoke(ex.Message);
                return false;
            }
        }
    }

    private static byte[] SharedSecret(ECDiffieHellman local, string otherPublicKey)
    {
        var bytes = Convert.FromBase64String(otherPublicKey);
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(bytes, out _);
        return local.DeriveKeyMaterial(other.PublicKey);
    }

    private static string Passphrase(byte[] secret) =>
        Convert.ToBase64String(SHA256.HashData(secret));

    private static string PairingCode(byte[] secret)
    {
        var hash = SHA256.HashData(secret);
        var n = (hash[0] << 16) | (hash[1] << 8) | hash[2];
        return (n % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static bool TryParseHostReply(byte[] buffer, out string name, out int port)
    {
        name = "";
        port = 0;
        var text = Encoding.ASCII.GetString(buffer);
        var lines = text.Split('\n');
        if (lines.Length < 4 || lines[0] != "OIPC1" || lines[1] != "HOST") return false;
        if (string.IsNullOrWhiteSpace(lines[2])) return false;
        if (!int.TryParse(lines[3], out port) || port is < 1 or > 65535) return false;
        name = lines[2].Trim();
        return true;
    }

    private static IEnumerable<IPEndPoint> BroadcastTargets()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(uni.Address)) continue;
                var broadcast = BroadcastAddress(uni.Address, uni.IPv4Mask);
                if (broadcast is not null)
                    yield return new IPEndPoint(broadcast, DiscoveryPort);
            }
        }
    }

    private static IPAddress? BroadcastAddress(IPAddress address, IPAddress? mask)
    {
        if (mask is null) return null;
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (ip.Length != 4 || m.Length != 4) return null;
        var b = new byte[4];
        for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~m[i]);
        return new IPAddress(b);
    }

    private static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(Stream stream, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length <= maxChars)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("The other Viewer closed the connection.");
            if (one[0] == (byte)'\n') return sb.ToString().TrimEnd('\r');
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
        }
        throw new IOException("The other Viewer sent a header this app cannot read.");
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("The other Viewer closed the connection.");
            read += n;
        }
        return buffer;
    }
}

public sealed record CameraTransferOffer(string Name, string Address, int Port);

public sealed record CameraTransferApproval(string Name, string Address, string Code);

public sealed record CameraTransferPayload(string Json, string Passphrase);

public sealed class CameraTransferDeniedException : Exception;
