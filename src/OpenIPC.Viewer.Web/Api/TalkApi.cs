using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Audio;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// Push-to-talk from the browser: the mic is on the viewer's machine, the RTSP
// backchannel is ours, and this is the bridge between them.
//
// The browser sends 16-bit mono PCM at the negotiated rate over a WebSocket, one
// binary message per 20 ms chunk; we companded it to G.711, wrap it in RTP and
// push it into the session. Encoding stays here rather than in JavaScript so the
// wire format is decided in exactly one place — Core's G711/RtpPacketizer, the
// same code the desktop talks with.
//
// PushToTalkController is deliberately NOT used: it owns an IAudioInput, i.e. a
// microphone on the machine running it. Here the microphone is three network
// hops away. Same reason PtzApi skips PtzController.
//
// One talker per camera. A speaker is a physical, exclusive thing — two people
// talking at once would interleave RTP into one stream and come out as noise —
// so a second attempt is refused rather than mixed.
public static class TalkApi
{
    // 8 kHz G.711: 20 ms = 160 samples = 320 bytes of PCM16. Anything much
    // larger than a couple of frames is either a confused client or an attempt
    // to make us allocate; the session is dropped rather than trusted.
    private const int MaxChunkBytes = 4096;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly ConcurrentDictionary<string, byte> Talking = new(StringComparer.OrdinalIgnoreCase);

    public static void MapTalkEndpoints(this WebApplication app)
    {
        // Does this camera even have a speaker? OPTIONS + DESCRIBE only, so the
        // UI can hide the button instead of offering something that will fail.
        app.MapGet("/api/v1/cameras/{id}/talk", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeout);
                var supported = await target!.Value.Client.ProbeAsync(target.Value.Endpoint, timeout.Token);
                return Results.Json(new { supported, busy = Talking.ContainsKey(id) });
            }
            catch (Exception ex)
            {
                // A camera that won't answer the probe isn't a "no" — say so and
                // let the viewer try, exactly as the desktop treats it.
                Log(ctx).LogInformation(ex, "Backchannel probe of {Camera} failed", id);
                return Results.Json(new { supported = (bool?)null, busy = Talking.ContainsKey(id) });
            }
        });

        app.Map("/api/v1/cameras/{id}/talk/stream", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
                return ValidationError("expected a websocket request");

            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            if (!Talking.TryAdd(id, 0))
                return Results.Json(new { error = "talk_busy" }, statusCode: StatusCodes.Status409Conflict);

            var logger = Log(ctx);
            try
            {
                IAudioBackchannelSession? session;
                try
                {
                    session = await target!.Value.Client.OpenAsync(target.Value.Endpoint, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Opening backchannel to {Camera} failed", id);
                    return Results.Json(new { error = "talk_failed" }, statusCode: StatusCodes.Status502BadGateway);
                }

                // No backchannel track advertised — the common "this camera has
                // no speaker" case, which is a 409, not a failure.
                if (session is null)
                    return Results.Json(new { error = "talk_unsupported" }, statusCode: StatusCodes.Status409Conflict);

                await using (session)
                {
                    var socket = await ctx.WebSockets.AcceptWebSocketAsync();
                    Audit(ctx, "talk.start", id);
                    await PumpAsync(socket, session, logger, ct);
                }
                return Results.Empty;
            }
            finally
            {
                Talking.TryRemove(id, out _);
            }
        });
    }

    // Read PCM chunks until the viewer stops talking (closes the socket) and
    // push each one to the camera. Nothing is buffered: a chunk that arrives
    // late is still played late, and holding it back would only add delay to a
    // conversation.
    private static async Task PumpAsync(
        WebSocket socket, IAudioBackchannelSession session, ILogger logger, CancellationToken ct)
    {
        var rtp = new RtpPacketizer(
            session.ALaw ? RtpPacketizer.PayloadTypePcma : RtpPacketizer.PayloadTypePcmu,
            ssrc: (uint)Random.Shared.Next());

        // Tell the client what was negotiated, so it can resample to the right
        // rate instead of guessing 8000 and being wrong on a 16 kHz camera.
        var hello = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"sampleRate\":{session.SampleRate},\"codec\":\"{(session.ALaw ? "pcma" : "pcmu")}\"}}");
        await socket.SendAsync(hello, WebSocketMessageType.Text, endOfMessage: true, ct);

        var buffer = new byte[MaxChunkBytes];
        while (!ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct);
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "Talk socket closed mid-stream");
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return;
            if (result.MessageType != WebSocketMessageType.Binary || result.Count == 0)
                continue;
            // A chunk split across frames would desynchronise the 16-bit samples;
            // drop the session rather than emit noise into someone's room.
            if (!result.EndOfMessage)
            {
                logger.LogWarning("Talk chunk exceeded {Max} bytes — dropping the session", MaxChunkBytes);
                return;
            }

            // Odd byte counts can't be whole PCM16 samples.
            var count = result.Count - (result.Count % 2);
            if (count == 0)
                continue;

            try
            {
                var g711 = G711.Encode(buffer.AsSpan(0, count), session.ALaw);
                var packet = rtp.Packetize(g711, samplesInPayload: g711.Length);
                await session.SendRtpAsync(packet, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sending talk audio failed — ending the session");
                return;
            }
        }
    }

    private readonly record struct TalkTarget(IAudioBackchannelClient Client, BackchannelEndpoint Endpoint);

    private static async Task<(TalkTarget? Target, IResult? Error)> TryResolveAsync(
        HttpContext ctx, string id, CancellationToken ct)
    {
        if (ctx.Deny(WebPermission.Talk) is { } forbidden)
            return (null, forbidden);
        if (ctx.DenyCamera(id) is { } hidden)
            return (null, hidden);

        var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
        var client = ctx.RequestServices.GetService<IAudioBackchannelClient>();
        if (dir is null || client is null)
            return (null, BackendUnavailable());
        if (!Guid.TryParse(id, out var guid))
            return (null, ValidationError("invalid camera id"));

        var camera = await dir.GetAsync(new CameraId(guid), ct);
        if (camera is null)
            return (null, NotFound());
        // Mirrors SingleCameraPageViewModel.CanTalk: a probed camera that says it
        // has no audio output is a definite no; anything else is worth trying.
        if (camera.OnvifEnabled && !camera.HasAudioOut)
            return (null, Results.Json(new { error = "talk_unsupported" }, statusCode: StatusCodes.Status409Conflict));

        var credentials = await dir.GetCredentialsAsync(camera.Id, ct);
        return (new TalkTarget(client, new BackchannelEndpoint(camera.RtspMainUri, credentials)), null);
    }

    private static ILogger Log(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web.Talk");
}
