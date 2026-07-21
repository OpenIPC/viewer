using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// Find cameras on the LAN and add them, from the browser — the last piece of
// camera management that needed the desktop app.
//
// Three steps, mirroring the desktop dialog: scan (background job, polled),
// probe one candidate with credentials, then add it. Probing separately is what
// makes the result reviewable before anything is written: ONVIF is the only
// source of the real RTSP URI, and behind NAT it can be wrong (phase-04 risks).
public static class DiscoveryApi
{
    private static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MaxScanTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    public static void MapDiscoveryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/discovery/scan", (DiscoveryScanRequest? body, HttpContext ctx) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var store = ctx.RequestServices.GetService<DiscoveryScanStore>();
            if (store is null)
                return BackendUnavailable();
            // One at a time: the sources bind sockets and the deep sweep is heavy.
            // A second viewer polls the running scan instead of starting a rival.
            if (store.HasRunningScan)
                return Results.Json(new { error = "scan_in_progress" }, statusCode: StatusCodes.Status409Conflict);

            var timeout = body?.TimeoutSeconds is { } s && s > 0
                ? TimeSpan.FromSeconds(Math.Min(s, MaxScanTimeout.TotalSeconds))
                : DefaultScanTimeout;
            var deep = body?.DeepScan ?? false;
            var scan = store.Start(new DiscoveryOptions(timeout, deep));
            Audit(ctx, "discovery.scan", deep ? "deep" : "passive");
            return Results.Json(Describe(scan));
        });

        app.MapGet("/api/v1/discovery/scan/{id}", (string id, HttpContext ctx) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var store = ctx.RequestServices.GetService<DiscoveryScanStore>();
            if (store is null)
                return BackendUnavailable();
            var scan = store.Get(id);
            return scan is null ? NotFound() : Results.Json(Describe(scan));
        });

        app.MapDelete("/api/v1/discovery/scan/{id}", (string id, HttpContext ctx) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var store = ctx.RequestServices.GetService<DiscoveryScanStore>();
            if (store is null)
                return BackendUnavailable();
            var scan = store.Get(id);
            if (scan is null)
                return NotFound();
            scan.Cancel();
            return Results.NoContent();
        });

        // Ask a candidate what it is. Answers 200 either way: a camera that
        // doesn't speak ONVIF (or rejects the login) still gets a usable draft
        // built from a guessed RTSP URI, which is what the desktop does too.
        app.MapPost("/api/v1/discovery/probe", async (
            DiscoveryProbeRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var probe = ctx.RequestServices.GetService<OnvifProbeService>();
            if (probe is null)
                return BackendUnavailable();
            if (string.IsNullOrWhiteSpace(body?.Host))
                return ValidationError("host is required");

            var draft = await ProbeAsync(ctx, probe, body, ct);
            return Results.Json(draft);
        });

        // Create the camera. Re-probes when credentials are supplied so the
        // ONVIF metadata (profile token, PTZ, audio) is persisted the same way
        // the desktop's add flow persists it — without it a discovered PTZ
        // camera would come out with no PTZ.
        app.MapPost("/api/v1/discovery/add", async (
            DiscoveryAddRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            var probeService = ctx.RequestServices.GetService<OnvifProbeService>();
            if (dir is null || probeService is null)
                return BackendUnavailable();
            if (body is null || string.IsNullOrWhiteSpace(body.Host))
                return ValidationError("host is required");

            // Probe only when there is something to learn: with credentials it
            // yields the ONVIF metadata to persist, and without an RTSP URI from
            // the client it's the only way to get one. Adding a plain RTSP camera
            // otherwise shouldn't sit through the ONVIF timeout.
            var hasCreds = !string.IsNullOrEmpty(body.Username) && !string.IsNullOrEmpty(body.Password);
            var draft = hasCreds || string.IsNullOrWhiteSpace(body.RtspMain)
                ? await ProbeAsync(ctx, probeService,
                    new DiscoveryProbeRequest(body.Host, body.OnvifPort, body.Username, body.Password), ct)
                : CameraDraft.Guessed(body.Host.Trim(), error: null);

            // The client may override what the probe suggested (a NAT-mangled
            // RTSP URI is the common case).
            var rtsp = !string.IsNullOrWhiteSpace(body.RtspMain) ? body.RtspMain! : draft.RtspMain;
            if (!Uri.TryCreate(rtsp, UriKind.Absolute, out var rtspUri))
                return ValidationError("rtspMain must be an absolute URI");

            var name = string.IsNullOrWhiteSpace(body.Name) ? draft.SuggestedName : body.Name!.Trim();
            var credentials = string.IsNullOrEmpty(body.Username) || string.IsNullOrEmpty(body.Password)
                ? null
                : new CameraCredentials(body.Username!, body.Password!);

            var id = await dir.AddAsync(new NewCameraRequest(
                Name: name,
                Host: body.Host.Trim(),
                HttpPort: body.HttpPort ?? 80,
                OnvifPort: body.OnvifPort,
                RtspMainUri: rtspUri,
                RtspSubUri: null,
                Credentials: credentials,
                GroupId: body.GroupId is { } g ? new GroupId(g) : null), ct);

            if (draft.Probe is { } result)
                await dir.SaveOnvifMetadataAsync(id, result, ct);

            Audit(ctx, "discovery.add", id);
            var created = await dir.GetAsync(id, ct);
            return created is null
                ? NotFound()
                : Results.Created($"/api/v1/cameras/{id}", CameraDto.From(created, null));
        });
    }

    // Runs the ONVIF probe chain, degrading to a guessed RTSP URI when the
    // device isn't ONVIF or the credentials are wrong. `Probe` is non-null only
    // when the real thing succeeded.
    private static async Task<CameraDraft> ProbeAsync(
        HttpContext ctx, OnvifProbeService probe, DiscoveryProbeRequest body, CancellationToken ct)
    {
        var host = body.Host!.Trim();
        var port = body.OnvifPort ?? 80;
        var credentials = string.IsNullOrEmpty(body.Username) || string.IsNullOrEmpty(body.Password)
            ? null
            : new CameraCredentials(body.Username!, body.Password!);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            var endpoint = OnvifEndpoint.FromHost(host, port, credentials);
            var result = await probe.ProbeAsync(endpoint, timeout.Token);
            return CameraDraft.FromProbe(host, result);
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("OpenIPC.Web.Discovery")
                .LogInformation(ex, "ONVIF probe of {Host} failed — falling back to a guessed RTSP URI", host);
            return CameraDraft.Guessed(host, ex.Message);
        }
    }

    private static object Describe(DiscoveryScan scan) => new
    {
        id = scan.Id,
        status = scan.Status,
        progress = Math.Round(scan.Progress, 3),
        error = scan.Error,
        devices = scan.Devices.Select(Describe).ToList(),
    };

    private static object Describe(DiscoveredDevice device) => new
    {
        host = device.Host,
        name = device.Name,
        model = device.Model,
        ports = device.Ports,
        protocols = Protocols(device.Protocols),
        confidence = device.Confidence.ToString(),
        onvifPort = device.OnvifServiceUri?.Port,
    };

    private static List<string> Protocols(DiscoveryProtocol flags) =>
        Enum.GetValues<DiscoveryProtocol>()
            .Where(p => p != DiscoveryProtocol.None && flags.HasFlag(p))
            .Select(p => p.ToString())
            .ToList();
}

// What the browser needs to fill an editor: the probed truth when ONVIF
// answered, a plausible guess otherwise.
internal sealed record CameraDraft(
    string Host,
    string SuggestedName,
    string RtspMain,
    bool OnvifOk,
    string? Manufacturer,
    string? Model,
    string? FirmwareVersion,
    bool HasPtz,
    bool HasAudioIn,
    bool HasAudioOut,
    string? Error)
{
    // Not serialized — the add endpoint reuses it to persist ONVIF metadata.
    [System.Text.Json.Serialization.JsonIgnore]
    public OnvifProbeResult? Probe { get; init; }

    public static CameraDraft FromProbe(string host, OnvifProbeResult probe) => new(
        Host: host,
        SuggestedName: probe.Model ?? probe.Manufacturer ?? host,
        RtspMain: CameraDto.SanitizeUri(probe.RtspMainUri),
        OnvifOk: true,
        Manufacturer: probe.Manufacturer,
        Model: probe.Model,
        FirmwareVersion: probe.FirmwareVersion,
        HasPtz: probe.HasPtz,
        HasAudioIn: probe.HasAudioIn,
        HasAudioOut: probe.HasAudioOut,
        Error: null)
    { Probe = probe };

    // OpenIPC's default main stream path; the user can correct it before saving.
    public static CameraDraft Guessed(string host, string? error) => new(
        Host: host,
        SuggestedName: host,
        RtspMain: $"rtsp://{host}:554/stream0",
        OnvifOk: false,
        Manufacturer: null,
        Model: null,
        FirmwareVersion: null,
        HasPtz: false,
        HasAudioIn: false,
        HasAudioOut: false,
        Error: error);
}

internal sealed record DiscoveryScanRequest(bool? DeepScan, int? TimeoutSeconds);

internal sealed record DiscoveryProbeRequest(string? Host, int? OnvifPort, string? Username, string? Password);

internal sealed record DiscoveryAddRequest(
    string? Host,
    string? Name,
    int? HttpPort,
    int? OnvifPort,
    string? Username,
    string? Password,
    string? RtspMain,
    int? GroupId);
