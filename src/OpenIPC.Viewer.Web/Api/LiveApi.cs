using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.Web.Api;

// Live video over WebSocket (Phase 20 §20.4). The heavy lifting (ffmpeg remux/
// transcode, box parsing, fan-out) is in LiveStreamHub; this endpoint resolves
// the camera, taps the shared stream, and pumps its bytes to the socket.
public static class LiveApi
{
    public static void MapLiveEndpoints(this WebApplication app)
    {
        app.Map("/api/v1/cameras/{id}/live", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            var hub = ctx.RequestServices.GetService<LiveStreamHub>();
            if (dir is null || hub is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (!Guid.TryParse(id, out var guid))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var camera = await dir.GetAsync(new CameraId(guid), ct);
            if (camera is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var credentials = await dir.GetCredentialsAsync(camera.Id, ct);
            var rtspUrl = BuildRtspUrl(camera.RtspMainUri, credentials);
            // The browser reconnects with ?transcode=1 when it can't play the
            // source codec over MSE (e.g. H.265). Then we re-encode to H.264.
            var transcode = ctx.Request.Query.ContainsKey("transcode");

            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            using var subscription = hub.Subscribe(id, transcode, rtspUrl);
            await PumpAsync(socket, subscription, ct);
        });
    }

    private static async Task PumpAsync(WebSocket socket, LiveSubscription subscription, CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open)
                    break;
                await socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* socket closed by the viewer — normal */ }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stream ended", CancellationToken.None); }
                catch { /* best effort */ }
            }
        }
    }

    // Injects credentials into the RTSP URL for ffmpeg (they live in the secrets
    // store, not the entity's URI). Never surfaced back to any client.
    private static string BuildRtspUrl(Uri baseUri, CameraCredentials? credentials)
    {
        if (credentials is null || string.IsNullOrEmpty(credentials.Username))
            return baseUri.ToString();
        return new UriBuilder(baseUri)
        {
            UserName = credentials.Username,
            Password = credentials.Password,
        }.Uri.ToString();
    }
}
