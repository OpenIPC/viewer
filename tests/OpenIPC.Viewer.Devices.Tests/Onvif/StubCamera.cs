using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.Devices.Tests.Onvif;

// What the client saw arrive. The body is read once, here, so a test can look
// at it without racing the handler for the request stream.
internal sealed record StubRequest(
    string Body,
    string? ContentType,
    string? SoapAction,
    string? Authorization)
{
    public bool IsSoap12 =>
        (ContentType ?? "").Contains("application/soap+xml", StringComparison.OrdinalIgnoreCase);

    public bool IsSoap11 =>
        (ContentType ?? "").Contains("text/xml", StringComparison.OrdinalIgnoreCase);

    public bool Is(string action) => Body.Contains(action, StringComparison.Ordinal);
}

// A camera that answers however a test needs it to, over a real socket, so the
// client's own HTTP stack does the work — content types, SOAPAction, and the
// 401 handshake included. None of that would be exercised by a mocked handler.
internal sealed class StubCamera : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ConcurrentQueue<StubRequest> _requests = new();

    private StubCamera(HttpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public int Port { get; }

    public IReadOnlyList<StubRequest> Requests => _requests.ToArray();

    public OnvifEndpoint Endpoint(CameraCredentials? credentials) =>
        OnvifEndpoint.FromHost("127.0.0.1", Port, credentials);

    // `challenge`, when set, is sent as WWW-Authenticate with any 401 the
    // responder returns — that is what makes HttpClient try again with Digest.
    public static StubCamera Start(
        Func<StubRequest, (string Body, int Status)> respond,
        string? challenge = null)
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var camera = new StubCamera(listener, port);
        _ = Task.Run(() => camera.LoopAsync(respond, challenge));
        return camera;
    }

    private async Task LoopAsync(Func<StubRequest, (string Body, int Status)> respond, string? challenge)
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { return; }   // disposed mid-wait

            try
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                var request = new StubRequest(
                    body,
                    ctx.Request.ContentType,
                    ctx.Request.Headers["SOAPAction"],
                    ctx.Request.Headers["Authorization"]);
                _requests.Enqueue(request);

                var (payload, status) = respond(request);
                ctx.Response.StatusCode = status;
                if (status == 401 && challenge is not null)
                    ctx.Response.AddHeader("WWW-Authenticate", challenge);

                if (payload.Length > 0)
                {
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    ctx.Response.ContentType = "application/soap+xml; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
                else
                {
                    // The failure this suite is about: a status, and no body to
                    // explain it.
                    ctx.Response.ContentLength64 = 0;
                }
            }
            catch (Exception)
            {
                // A test that tore the camera down mid-request is not a failure.
            }
            finally
            {
                try { ctx.Response.Close(); } catch (Exception) { /* already gone */ }
            }
        }
    }

    // Ask the OS for a port, then hand it to HttpListener. Racy in principle,
    // never in practice on a test host.
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (Exception) { /* nothing to stop */ }
        try { _listener.Close(); } catch (Exception) { /* already closed */ }
    }
}
