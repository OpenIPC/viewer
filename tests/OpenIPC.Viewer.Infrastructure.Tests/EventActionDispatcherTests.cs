using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Notifications;
using OpenIPC.Viewer.Infrastructure.Notifications;

namespace OpenIPC.Viewer.Infrastructure.Tests;

// Drives each external-action channel against a real loopback listener.
public sealed class EventActionDispatcherTests : IDisposable
{
    private readonly EventActionDispatcher _dispatcher =
        new(new NoCredentials(), NullLogger<EventActionDispatcher>.Instance);

    private static EventActionContext Ctx(string camera = "Gate") => new(
        new CameraEvent(EventId.New(), new CameraId(Guid.NewGuid()), EventKind.Detection, EventSeverity.Info,
            DateTime.UtcNow.AddSeconds(-2), null, "analytics", "person ×1", 0.91f, 1),
        camera, "Object detected", DateTime.UtcNow);

    [Fact]
    public async Task Tcp_SendsRenderedPayload()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = ReadAllAsync(listener);

        var s = new EventActionSettings(SignalEnabled: true, SignalHost: "127.0.0.1", SignalPort: port,
            SignalPayload: "ALARM {camera} {confidence}\\r\\n");
        await _dispatcher.TestAsync(EventActionChannel.Signal, Ctx(), s, CancellationToken.None);

        Assert.Equal("ALARM Gate 91\r\n", Encoding.UTF8.GetString(await received.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Udp_SendsHexDatagram()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var receive = udp.ReceiveAsync();

        var s = new EventActionSettings(SignalEnabled: true, SignalProtocol: EventActionSettings.ProtocolUdp,
            SignalHost: "127.0.0.1", SignalPort: port, SignalPayload: "hex: A0 01 01 A2");
        await _dispatcher.TestAsync(EventActionChannel.Signal, Ctx(), s, CancellationToken.None);

        var result = await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0xA0, 0x01, 0x01, 0xA2 }, result.Buffer);
    }

    [Fact]
    public async Task Tcp_Refused_Throws()
    {
        // Grab a free port, then close it so nothing listens there.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var s = new EventActionSettings(SignalEnabled: true, SignalHost: "127.0.0.1", SignalPort: port);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _dispatcher.TestAsync(EventActionChannel.Signal, Ctx(), s, CancellationToken.None));
    }

    [Fact]
    public async Task Http_PostsJsonBody_AndEncodesUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var request = ServeOneHttpAsync(listener, 200);

        var s = new EventActionSettings(HttpEnabled: true,
            HttpUrl: $"http://127.0.0.1:{port}/hook?cam={{camera}}&what={{summary}}");
        await _dispatcher.TestAsync(EventActionChannel.Http, Ctx(), s, CancellationToken.None);

        var raw = await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("POST /hook?cam=Gate&what=person%20%C3%971 HTTP/1.1", raw);
        var body = raw[(raw.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("detection", json.RootElement.GetProperty("event").GetString());
        Assert.Equal("Gate", json.RootElement.GetProperty("camera").GetString());
    }

    [Fact]
    public async Task Http_ErrorStatus_Throws()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = ServeOneHttpAsync(listener, 500);

        var s = new EventActionSettings(HttpEnabled: true, HttpMethod: "GET", HttpUrl: $"http://127.0.0.1:{port}/");
        var ex = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            _dispatcher.TestAsync(EventActionChannel.Http, Ctx(), s, CancellationToken.None));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Dispatch_OneChannelFailing_DoesNotStopTheOthers()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var receive = udp.ReceiveAsync();

        var s = new EventActionSettings(
            Enabled: true,
            SignalEnabled: true, SignalProtocol: EventActionSettings.ProtocolUdp,
            SignalHost: "127.0.0.1", SignalPort: port, SignalPayload: "ok",
            HttpEnabled: true, HttpUrl: "not a url");
        await _dispatcher.DispatchAsync(Ctx(), s, CancellationToken.None); // must not throw

        Assert.Equal("ok", Encoding.UTF8.GetString((await receive.WaitAsync(TimeSpan.FromSeconds(5))).Buffer));
    }

    [Fact]
    public async Task Smtp_WithoutServer_Throws()
    {
        var s = new EventActionSettings(EmailEnabled: true, EmailTo: "a@example.com");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _dispatcher.TestAsync(EventActionChannel.Email, Ctx(), s, CancellationToken.None));
        Assert.Contains("SMTP", ex.Message);
    }

    [SkippableFact]
    public async Task Program_RunsBatchWithArgumentsAndEnvironment()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "batch files are Windows-only");
        var dir = Directory.CreateTempSubdirectory("openipc-action-");
        try
        {
            var output = Path.Combine(dir.FullName, "out.txt");
            var script = Path.Combine(dir.FullName, "hook.bat");
            await File.WriteAllTextAsync(script, $"@echo %1 %OPENIPC_EVENT_KIND% %OPENIPC_CONFIDENCE%> \"{output}\"\r\n");

            var s = new EventActionSettings(ProgramEnabled: true, ProgramPath: script, ProgramArguments: "{camera}");
            await _dispatcher.TestAsync(EventActionChannel.Program, Ctx(), s, CancellationToken.None);

            for (var i = 0; i < 50 && !File.Exists(output); i++) await Task.Delay(100);
            await Task.Delay(200);
            Assert.Equal("Gate detection 91", (await File.ReadAllTextAsync(output)).Trim());
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    // A camera can name itself (auto-detect fills the name from the device), so
    // its name must not be able to break out of the argument into cmd.exe.
    [SkippableFact]
    public async Task Program_HostileCameraName_CannotRunCommands()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "batch files are Windows-only");
        var dir = Directory.CreateTempSubdirectory("openipc-action-");
        try
        {
            var output = Path.Combine(dir.FullName, "out.txt");
            var marker = Path.Combine(dir.FullName, "pwned.txt");
            var script = Path.Combine(dir.FullName, "hook.bat");
            await File.WriteAllTextAsync(script, $"@echo %~1> \"{output}\"\r\n");

            var hostile = $"x\" & echo pwned> \"{marker}\" & \"";
            var s = new EventActionSettings(ProgramEnabled: true, ProgramPath: script, ProgramArguments: "\"{camera}\" {kind}");
            await _dispatcher.TestAsync(EventActionChannel.Program, Ctx(camera: hostile), s, CancellationToken.None);

            for (var i = 0; i < 50 && !File.Exists(output); i++) await Task.Delay(100);
            await Task.Delay(300);
            Assert.False(File.Exists(marker), "the camera name ran a command");
            var echoed = (await File.ReadAllTextAsync(output)).Trim();
            Assert.Contains("echo pwned", echoed);   // arrived as data, metacharacters stripped
            Assert.DoesNotContain("&", echoed);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [SkippableFact]
    public async Task Program_UnsupportedExtension_IsRefused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the extension allow-list is Windows-only");
        var dir = Directory.CreateTempSubdirectory("openipc-action-");
        try
        {
            var script = Path.Combine(dir.FullName, "hook.vbs");
            await File.WriteAllTextAsync(script, "' nothing");
            var s = new EventActionSettings(ProgramEnabled: true, ProgramPath: script);
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                _dispatcher.TestAsync(EventActionChannel.Program, Ctx(), s, CancellationToken.None));
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Tcp_EventDataCannotInjectBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = ReadAllAsync(listener);

        var s = new EventActionSettings(SignalEnabled: true, SignalHost: "127.0.0.1", SignalPort: port,
            SignalPayload: "ALARM {camera}\\r\\n");
        await _dispatcher.TestAsync(EventActionChannel.Signal, Ctx(camera: "Gate\r\nDISARM\\x00"), s, CancellationToken.None);

        Assert.Equal("ALARM GateDISARM\\x00\r\n", Encoding.UTF8.GetString(await received.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Hex_WithPlaceholder_Throws()
    {
        var s = new EventActionSettings(SignalEnabled: true, SignalHost: "127.0.0.1", SignalPort: 9,
            SignalPayload: "hex: A0 {objects}");
        await Assert.ThrowsAsync<FormatException>(() =>
            _dispatcher.TestAsync(EventActionChannel.Signal, Ctx(), s, CancellationToken.None));
    }

    // "auto" + a stored login on a server that offers no STARTTLS (or had the
    // offer stripped on the way) must fail instead of sending AUTH in clear text.
    [Fact]
    public async Task Smtp_AutoWithLogin_NeverAuthenticatesInPlainText()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var authSeen = false;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 plain.local ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)) authSeen = true;
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-plain.local");
                    await writer.WriteLineAsync("250 AUTH PLAIN LOGIN"); // no STARTTLS
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }
        });

        using var dispatcher = new EventActionDispatcher(
            new FixedCredentials(new MailCredential("alerts@example.com", "secret")),
            NullLogger<EventActionDispatcher>.Instance);
        var s = new EventActionSettings(EmailEnabled: true, EmailTo: "guard@example.com",
            SmtpHost: "127.0.0.1", SmtpPort: port, SmtpSecurity: "auto");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            dispatcher.TestAsync(EventActionChannel.Email, Ctx(), s, CancellationToken.None));
        await Task.WhenAny(server, Task.Delay(2000));
        Assert.False(authSeen, "the password was offered over a plain connection");
    }

    public void Dispose() => _dispatcher.Dispose();

    private static async Task<byte[]> ReadAllAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var ms = new MemoryStream();
        await client.GetStream().CopyToAsync(ms);
        return ms.ToArray();
    }

    // Minimal HTTP/1.1 server: reads one request (headers + Content-Length body),
    // answers with the given status, returns the raw request text.
    private static async Task<string> ServeOneHttpAsync(TcpListener listener, int status)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        int headerEnd = -1, contentLength = 0;
        while (true)
        {
            var n = await stream.ReadAsync(chunk);
            if (n == 0) break;
            buffer.AddRange(chunk.AsSpan(0, n).ToArray());
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            if (headerEnd < 0)
            {
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    foreach (var line in text[..headerEnd].Split("\r\n"))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            contentLength = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            if (headerEnd >= 0 && Encoding.UTF8.GetByteCount(text) - (headerEnd + 4) >= contentLength) break;
        }
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed class FixedCredentials(MailCredential credential) : IMailCredentialStore
    {
        public string StorageName => "test";
        public Task<MailCredential?> GetAsync(CancellationToken ct) => Task.FromResult<MailCredential?>(credential);
        public Task SetAsync(MailCredential c, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoCredentials : IMailCredentialStore
    {
        public string StorageName => "none";
        public Task<MailCredential?> GetAsync(CancellationToken ct) => Task.FromResult<MailCredential?>(null);
        public Task SetAsync(MailCredential credential, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
