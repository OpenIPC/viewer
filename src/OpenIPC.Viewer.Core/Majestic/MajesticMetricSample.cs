namespace OpenIPC.Viewer.Core.Majestic;

// One scalar reading from the camera's Prometheus /metrics endpoint. Labels is
// the raw "{k=\"v\",…}" text (kept verbatim — we only display, never query).
public sealed record MajesticMetricSample(string Name, string? Labels, double Value)
{
    // "name{labels}" — what we show in the read-only metrics list.
    public string Display => Labels is null ? Name : Name + Labels;
}
