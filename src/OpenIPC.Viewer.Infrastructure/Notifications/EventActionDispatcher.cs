using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.Infrastructure.Notifications;

// Executes the external actions for one camera event (see EventActionSettings):
// a raw TCP/UDP signal, an HTTP webhook, an e-mail and a local program. Every
// enabled channel runs concurrently with its own timeout; one failing channel
// never blocks or cancels the others.
public sealed class EventActionDispatcher : IEventActionDispatcher, IDisposable
{
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly IMailCredentialStore _credentials;
    private readonly ILogger<EventActionDispatcher> _log;
    private readonly HttpClient _http;

    public EventActionDispatcher(IMailCredentialStore credentials, ILogger<EventActionDispatcher> log)
    {
        _credentials = credentials;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenIPC-Viewer");
    }

    public async Task DispatchAsync(EventActionContext context, EventActionSettings settings, CancellationToken ct)
    {
        var runs = new List<Task>();
        if (settings.SignalEnabled && !string.IsNullOrWhiteSpace(settings.SignalHost) && settings.SignalPort > 0)
            runs.Add(RunLoggedAsync(EventActionChannel.Signal, context, settings, ct));
        if (settings.HttpEnabled && !string.IsNullOrWhiteSpace(settings.HttpUrl))
            runs.Add(RunLoggedAsync(EventActionChannel.Http, context, settings, ct));
        if (settings.EmailEnabled && !string.IsNullOrWhiteSpace(settings.EmailTo))
            runs.Add(RunLoggedAsync(EventActionChannel.Email, context, settings, ct));
        if (settings.ProgramEnabled && !string.IsNullOrWhiteSpace(settings.ProgramPath))
            runs.Add(RunLoggedAsync(EventActionChannel.Program, context, settings, ct));

        await Task.WhenAll(runs).ConfigureAwait(false);
    }

    public Task TestAsync(EventActionChannel channel, EventActionContext context, EventActionSettings settings, CancellationToken ct)
        => RunAsync(channel, context, settings, ct);

    private async Task RunLoggedAsync(EventActionChannel channel, EventActionContext context, EventActionSettings settings, CancellationToken ct)
    {
        try
        {
            await RunAsync(channel, context, settings, ct).ConfigureAwait(false);
            _log.LogInformation("Event action {Channel} done for camera {Camera}", channel, context.CameraName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Event action {Channel} failed for camera {Camera}", channel, context.CameraName);
        }
    }

    private async Task RunAsync(EventActionChannel channel, EventActionContext context, EventActionSettings settings, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ChannelTimeout);
        switch (channel)
        {
            case EventActionChannel.Signal:
                await SendSignalAsync(context, settings, cts.Token).ConfigureAwait(false);
                break;
            case EventActionChannel.Http:
                await SendHttpAsync(context, settings, cts.Token).ConfigureAwait(false);
                break;
            case EventActionChannel.Email:
                await SendEmailAsync(context, settings, cts.Token).ConfigureAwait(false);
                break;
            case EventActionChannel.Program:
                StartProgram(context, settings);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channel), channel, null);
        }
    }

    // --- TCP / UDP ---------------------------------------------------------

    private static async Task SendSignalAsync(EventActionContext context, EventActionSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.SignalHost) || s.SignalPort is <= 0 or > 65535)
            throw new InvalidOperationException("Signal host and port (1–65535) are required");

        var payload = SignalPayload.Encode(EventActionTemplate.Render(s.SignalPayload, context));
        var host = s.SignalHost.Trim();

        if (string.Equals(s.SignalProtocol, EventActionSettings.ProtocolUdp, StringComparison.OrdinalIgnoreCase))
        {
            using var udp = new UdpClient();
            await udp.SendAsync(payload, host, s.SignalPort, ct).ConfigureAwait(false);
            return;
        }

        using var tcp = new TcpClient { NoDelay = true };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(host, s.SignalPort, connect.Token).ConfigureAwait(false);
        }
        var stream = tcp.GetStream();
        await using (stream.ConfigureAwait(false))
        {
            if (payload.Length > 0)
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    // --- HTTP webhook ------------------------------------------------------

    private async Task SendHttpAsync(EventActionContext context, EventActionSettings s, CancellationToken ct)
    {
        var url = EventActionTemplate.Render(s.HttpUrl.Trim(), context, Uri.EscapeDataString);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"Not an http(s) URL: {url}");

        var method = (s.HttpMethod ?? "POST").Trim().ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "PUT" => HttpMethod.Put,
            _ => HttpMethod.Post,
        };

        using var request = new HttpRequestMessage(method, uri);
        if (method != HttpMethod.Get)
        {
            var body = EventActionTemplate.Render(s.HttpBody ?? "", context, EventActionTemplate.JsonEscape);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Webhook answered {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    // --- E-mail ------------------------------------------------------------

    private async Task SendEmailAsync(EventActionContext context, EventActionSettings s, CancellationToken ct)
    {
        var to = EventActionTemplate.SplitAddresses(s.EmailTo);
        if (to.Count == 0) throw new InvalidOperationException("No e-mail recipient configured");

        var message = new OutgoingMail(
            to,
            EventActionTemplate.Render(s.EmailSubject ?? "", context),
            EventActionTemplate.Render(s.EmailBody ?? "", context));

        switch ((s.EmailTransport ?? "").Trim().ToLowerInvariant())
        {
            case EventActionSettings.TransportMapi:
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("The default mail client (MAPI) is only available on Windows");
                await MapiMailSender.SendAsync(message, ct).ConfigureAwait(false);
                break;
            case EventActionSettings.TransportOutlook:
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Outlook automation is only available on Windows");
                await OutlookMailSender.SendAsync(message, ct).ConfigureAwait(false);
                break;
            default:
                var credential = await _credentials.GetAsync(ct).ConfigureAwait(false);
                await SmtpMailSender.SendAsync(message, s, credential, ct).ConfigureAwait(false);
                break;
        }
    }

    // --- Program -----------------------------------------------------------

    private static void StartProgram(EventActionContext context, EventActionSettings s)
    {
        var path = Environment.ExpandEnvironmentVariables(s.ProgramPath.Trim().Trim('"'));
        if (!File.Exists(path))
            throw new FileNotFoundException("Program not found", path);

        var args = EventActionTemplate.Render(s.ProgramArguments ?? "", context);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        ProcessStartInfo psi;
        if (ext == ".ps1")
        {
            // A script the user picked themselves; run it without the profile and
            // without tripping over the machine's default execution policy.
            psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\" {args}");
        }
        else if (ext is ".exe" or ".bat" or ".cmd" or ".com" || !OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(path, args);
        }
        else
        {
            // .vbs, .py, … — let the shell pick the associated interpreter.
            psi = new ProcessStartInfo(path, args) { UseShellExecute = true };
        }

        if (!psi.UseShellExecute)
        {
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
            // The same facts as environment variables, so a script needn't parse
            // its command line.
            var e = context.Event;
            psi.Environment["OPENIPC_EVENT_KIND"] = context.KindToken;
            psi.Environment["OPENIPC_EVENT_TITLE"] = context.Title;
            psi.Environment["OPENIPC_EVENT_ID"] = e.Id.ToString();
            psi.Environment["OPENIPC_CAMERA"] = context.CameraName;
            psi.Environment["OPENIPC_CAMERA_ID"] = e.CameraId.ToString();
            psi.Environment["OPENIPC_SUMMARY"] = e.Summary ?? "";
            psi.Environment["OPENIPC_CONFIDENCE"] = EventActionTemplate.Render("{confidence}", context);
            psi.Environment["OPENIPC_OBJECTS"] = EventActionTemplate.Render("{objects}", context);
            psi.Environment["OPENIPC_TIME_UTC"] = EventActionTemplate.Render("{timeUtc}", context);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {path}");
    }

    public void Dispose() => _http.Dispose();
}

// One rendered message, transport-neutral.
internal sealed record OutgoingMail(IReadOnlyList<string> To, string Subject, string Body);
