using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// The camera's own settings (Majestic config.json), over HTTP.
//
// Everything here is gated on Manage, including the reads — config.json is not
// a view of the picture, it is the installation: it carries the RTSP/ONVIF
// logins, and on many builds the Wi-Fi PSK. ViewLive means "may watch this
// camera", which is not the same permission at all.
//
// Writes are read-modify-write against a FRESH copy of the camera's config, not
// against whatever the browser last saw: the client sends only (path, value)
// pairs, so a stale tab can't silently revert a field someone else changed, and
// a malicious one can't post a wholesale payload through the field editor. The
// raw-JSON endpoint is the deliberate exception — that one is "I know what I'm
// doing", and it says so in the UI.
public static class MajesticApi
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);

    public static void MapMajesticEndpoints(this WebApplication app)
    {
        // Everything the panel needs in one round-trip: identity, the parsed
        // config and the current night mode. Individual failures degrade to
        // nulls rather than failing the whole read — an older firmware without
        // /api/v1/info.json still has an editable config.
        app.MapGet("/api/v1/cameras/{id}/majestic", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            return await InvokeAsync(ctx, "read", ct, async () =>
            {
                using var timeout = Timeout(ct);
                var config = await target!.Value.Client.GetConfigAsync(target.Value.Endpoint, timeout.Token);
                MajesticInfo? info = null;
                try
                {
                    info = await target.Value.Client.GetInfoAsync(target.Value.Endpoint, timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log(ctx).LogInformation(ex, "Majestic info unavailable for {Host}", target.Value.Endpoint.Host);
                }

                var model = target.Value.Schema.Parse(config.RawJson);
                return Results.Json(new
                {
                    info = info is null ? null : new
                    {
                        model = info.Model,
                        firmware = info.FirmwareVersion,
                        chip = info.ChipModel,
                        uptime = info.Uptime,
                    },
                    nightMode = config.NightMode.ToString().ToLowerInvariant(),
                    sections = model.Sections.Select(s => new
                    {
                        name = s.Name,
                        fields = s.Fields.Select(ToDto).ToList(),
                    }).ToList(),
                    rawJson = config.RawJson,
                });
            });
        });

        // Field edits. The body is a list of (path, value); anything that isn't
        // a field we just parsed is ignored, and values equal to the camera's
        // current one are dropped — so "save" with nothing changed is a no-op
        // instead of a needless config POST (which restarts the encoder).
        app.MapPost("/api/v1/cameras/{id}/majestic/config", async (
            string id, MajesticEditsRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;
            if (body?.Edits is null || body.Edits.Count == 0)
                return ValidationError("no edits supplied");

            return await InvokeAsync(ctx, "apply", ct, async () =>
            {
                using var timeout = Timeout(ct);
                var config = await target!.Value.Client.GetConfigAsync(target.Value.Endpoint, timeout.Token);
                var model = target.Value.Schema.Parse(config.RawJson);

                var editedByPath = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var edit in body.Edits)
                {
                    if (!string.IsNullOrEmpty(edit.Path) && edit.Value is not null)
                        editedByPath[edit.Path] = edit.Value;
                }

                var edits = model.ComputeEdits(editedByPath);
                if (edits.Count == 0)
                    return Results.Json(new { applied = 0, restart = false });

                var updated = target.Value.Schema.ApplyEdits(config.RawJson, edits);
                await target.Value.Client.UpdateRawConfigAsync(target.Value.Endpoint, updated, timeout.Token);
                Audit(ctx, "majestic.config", $"{id}/{string.Join(",", edits.Select(e => e.Section + "." + e.Key))}");

                // Whether the picture is about to blink, so the UI can warn
                // instead of leaving the viewer wondering why the tile froze.
                var restart = model.Fields
                    .Where(f => editedByPath.ContainsKey(f.Path))
                    .Any(f => f.RequiresRestart);
                return Results.Json(new { applied = edits.Count, restart });
            });
        });

        // The escape hatch: post the whole config.json as typed. Validated as
        // JSON by the client below us; everything past that is the operator's
        // call, exactly like editing the file over SSH.
        app.MapPost("/api/v1/cameras/{id}/majestic/raw", async (
            string id, MajesticRawRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;
            if (string.IsNullOrWhiteSpace(body?.Json))
                return ValidationError("json is required");

            return await InvokeAsync(ctx, "raw", ct, async () =>
            {
                using var timeout = Timeout(ct);
                try
                {
                    await target!.Value.Client.UpdateRawConfigAsync(target.Value.Endpoint, body!.Json!, timeout.Token);
                }
                catch (ArgumentException)
                {
                    return ValidationError("not valid JSON");
                }
                Audit(ctx, "majestic.raw", id);
                return Results.NoContent();
            });
        });

        app.MapPost("/api/v1/cameras/{id}/majestic/night", async (
            string id, MajesticNightRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;
            if (!Enum.TryParse<NightMode>(body?.Mode, ignoreCase: true, out var mode) || mode == NightMode.Unknown)
                return ValidationError("mode must be day, night or auto");

            return await InvokeAsync(ctx, "night", ct, async () =>
            {
                using var timeout = Timeout(ct);
                await target!.Value.Client.SetNightModeAsync(target.Value.Endpoint, mode, timeout.Token);
                Audit(ctx, "majestic.night", $"{id}/{mode}");
                return Results.NoContent();
            });
        });

        // Read-only telemetry. Parsed here rather than in the browser so the
        // Prometheus text format stays one implementation (Core's parser, which
        // the desktop uses too).
        app.MapGet("/api/v1/cameras/{id}/majestic/metrics", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            return await InvokeAsync(ctx, "metrics", ct, async () =>
            {
                using var timeout = Timeout(ct);
                var text = await target!.Value.Client.GetMetricsAsync(target.Value.Endpoint, timeout.Token);
                var samples = PrometheusTextParser.Parse(text);
                return Results.Json(samples.Select(s => new { name = s.Display, value = s.Value }).ToList());
            });
        });
    }

    private readonly record struct MajesticTarget(
        IMajesticClient Client, IMajesticConfigSchema Schema, MajesticEndpoint Endpoint);

    private static async Task<(MajesticTarget? Target, IResult? Error)> TryResolveAsync(
        HttpContext ctx, string id, CancellationToken ct)
    {
        if (ctx.Deny(WebPermission.Manage) is { } forbidden)
            return (null, forbidden);
        if (ctx.DenyCamera(id) is { } hidden)
            return (null, hidden);

        var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
        var majestic = ctx.RequestServices.GetService<IMajesticClient>();
        var schema = ctx.RequestServices.GetService<IMajesticConfigSchema>();
        if (dir is null || majestic is null || schema is null)
            return (null, BackendUnavailable());
        if (!Guid.TryParse(id, out var guid))
            return (null, ValidationError("invalid camera id"));

        var camera = await dir.GetAsync(new CameraId(guid), ct);
        if (camera is null)
            return (null, NotFound());

        var credentials = await dir.GetCredentialsAsync(camera.Id, ct);
        var endpoint = new MajesticEndpoint(camera.Host, camera.HttpPort, credentials);

        // The IsMajestic flag is only ever set by the discovery probe, so a
        // camera added by hand in the web UI would never show a settings panel
        // even while running Majestic. Ping once and remember the answer — the
        // same thing SingleCameraPageViewModel.InitMajesticAsync does on the
        // desktop, so both heads converge on the same flag.
        if (!camera.IsMajestic)
        {
            bool reachable;
            try
            {
                using var ping = Timeout(ct);
                reachable = await majestic.PingAsync(endpoint, ping.Token);
            }
            catch (Exception ex)
            {
                Log(ctx).LogInformation(ex, "Majestic ping of {Host} failed", camera.Host);
                reachable = false;
            }
            if (!reachable)
                return (null, Unavailable());
            await dir.SetIsMajesticAsync(camera.Id, true, ct);
        }

        return (new MajesticTarget(majestic, schema, endpoint), null);
    }

    // Camera-side failures are upstream problems: 502 with a flat code, details
    // to the log only (the exception text carries host and sometimes body).
    //
    // The cancellation case has to be split, or a camera that simply never
    // answers reads as success: our own call timeout also surfaces as an
    // OperationCanceledException, and answering 204 to that told the browser
    // "done" while nothing had happened. Only the caller's token going down
    // means the request was abandoned.
    private static async Task<IResult> InvokeAsync(
        HttpContext ctx, string operation, CancellationToken requestAborted, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            return Results.NoContent();
        }
        catch (OperationCanceledException)
        {
            Log(ctx).LogWarning("Majestic {Operation} timed out", operation);
            return Results.Json(new { error = "majestic_timeout" }, statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            Log(ctx).LogWarning(ex, "Majestic {Operation} failed", operation);
            return Results.Json(new { error = "majestic_failed" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult Unavailable() =>
        Results.Json(new { error = "majestic_unavailable" }, statusCode: StatusCodes.Status409Conflict);

    private static CancellationTokenSource Timeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        return cts;
    }

    private static ILogger Log(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web.Majestic");

    private static object ToDto(MajesticConfigField f) => new
    {
        path = f.Path,
        section = f.Section,
        key = f.Key,
        kind = f.Kind.ToString().ToLowerInvariant(),
        value = f.Value,
        options = f.Options,
        restart = f.RequiresRestart,
    };
}

public sealed record MajesticFieldEditRequest(string? Path, string? Value);
public sealed record MajesticEditsRequest(IReadOnlyList<MajesticFieldEditRequest>? Edits);
public sealed record MajesticRawRequest(string? Json);
public sealed record MajesticNightRequest(string? Mode);
