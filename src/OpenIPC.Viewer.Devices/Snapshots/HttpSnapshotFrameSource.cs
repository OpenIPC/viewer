using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;

namespace OpenIPC.Viewer.Devices.Snapshots;

/// <summary>
/// Grabs a still over HTTP without a decoder. Majestic exposes <c>/image.jpg</c>
/// (full-res, ~50–100 ms) — the primary source for OpenIPC cameras. Non-Majestic
/// ONVIF cameras will get a GetSnapshotUri path later; until then they return
/// null and the tile keeps the RTSP live view.
/// </summary>
public sealed class HttpSnapshotFrameSource : ISnapshotFrameSource
{
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(5);

    private readonly IMajesticClient _majestic;
    private readonly ICameraCredentialsProvider _credentials;
    private readonly ILogger<HttpSnapshotFrameSource> _logger;

    public HttpSnapshotFrameSource(
        IMajesticClient majestic,
        ICameraCredentialsProvider credentials,
        ILogger<HttpSnapshotFrameSource> logger)
    {
        _majestic = majestic;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<byte[]?> GrabAsync(Camera camera, CancellationToken ct)
    {
        if (camera.IsMajestic)
        {
            try
            {
                var creds = await _credentials.GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
                var endpoint = new MajesticEndpoint(camera.Host, camera.HttpPort, creds);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(GrabTimeout);
                var bytes = await _majestic
                    .SnapshotJpegAsync(endpoint, new MajesticSnapshotOptions(), cts.Token)
                    .ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                    return bytes;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Majestic snapshot grab failed for {Camera}", camera.Name);
            }
        }

        // TODO: ONVIF GetSnapshotUri → HTTP GET for non-Majestic cameras.
        return null;
    }
}
