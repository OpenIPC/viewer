using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Majestic;

public interface IMajesticClient
{
    // Cheap reachability probe (`/api/v1/info.json` or `/api/v1/config.json`) —
    // false on 404 / non-JSON / connection refused so the IsMajestic flag stays
    // off for non-OpenIPC cameras.
    Task<bool> PingAsync(MajesticEndpoint endpoint, CancellationToken ct);

    Task<MajesticConfig> GetConfigAsync(MajesticEndpoint endpoint, CancellationToken ct);
    Task<MajesticInfo> GetInfoAsync(MajesticEndpoint endpoint, CancellationToken ct);

    // Read-modify-write: client fetches current config, merges non-null patch
    // fields, POSTs full JSON. Camera typically restarts streaming for video
    // changes — UI should be prepared to reconnect (LiveStreamCoordinator).
    Task UpdateConfigAsync(MajesticEndpoint endpoint, MajesticConfigPatch patch, CancellationToken ct);

    // Posts user-edited raw JSON straight to /api/v1/config.json. Validates
    // the body parses as JSON before sending — anything else is the caller's
    // responsibility (e.g. dialog accepts only when JSON is well-formed).
    // Throws ArgumentException if rawJson is not valid JSON.
    Task UpdateRawConfigAsync(MajesticEndpoint endpoint, string rawJson, CancellationToken ct);

    Task SetNightModeAsync(MajesticEndpoint endpoint, NightMode mode, CancellationToken ct);

    // Fetches the raw Prometheus exposition text from the camera's /metrics
    // endpoint (read-only telemetry). Throws MajesticHttpException on non-2xx
    // (e.g. firmware without the endpoint returns 404). Caller parses with
    // PrometheusTextParser.
    Task<string> GetMetricsAsync(MajesticEndpoint endpoint, CancellationToken ct);

    // Returns the JPEG bytes from /image.jpg — caller decides where to write.
    // Faster than decoding a video frame because no codec round-trip; phase-05
    // §5.7 says ~50–100ms on a typical SoC.
    Task<byte[]> SnapshotJpegAsync(MajesticEndpoint endpoint, MajesticSnapshotOptions options, CancellationToken ct);
}
