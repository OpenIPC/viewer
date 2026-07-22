using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Web.Auth;

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

            // Watching is a permission, and a camera outside the caller's subset
            // must not stream — this is the endpoint that would actually leak
            // pixels, so it checks before touching the backend.
            if (ctx.GetIdentity() is not { } identity || !identity.Can(WebPermission.ViewLive))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!identity.CanSee(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
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
        // A viewer leaving closes the socket, which arrives as a close *frame* —
        // and a frame is only seen by someone receiving. Without this loop the
        // send side keeps writing into a socket the browser has already given
        // up on, so the shared ffmpeg process outlives its last viewer (a grid
        // switched to stills mode, or a tab navigated away, would leave one
        // running per camera). We never expect data from the client, so the
        // first receive to complete means "gone" either way.
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchClose = WatchForCloseAsync(socket, leaving);

        try
        {
            await foreach (var chunk in subscription.Reader.ReadAllAsync(leaving.Token).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open)
                    break;
                await socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, leaving.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* socket closed by the viewer — normal */ }
        finally
        {
            leaving.Cancel();
            try { await watchClose.ConfigureAwait(false); } catch { /* best effort */ }
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stream ended", CancellationToken.None); }
                catch { /* best effort */ }
            }
        }
    }

    // Reads until the client closes (or errors), then trips the token so the
    // send loop stops and the subscription is disposed.
    private static async Task WatchForCloseAsync(WebSocket socket, CancellationTokenSource leaving)
    {
        var scratch = new byte[256];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(scratch, leaving.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { /* we're the ones shutting down */ }
        catch (Exception) { /* connection gone — same outcome */ }
        finally
        {
            try { leaving.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
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
