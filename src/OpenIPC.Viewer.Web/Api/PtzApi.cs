using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// PTZ over HTTP. The desktop head drives PtzController — a background pump that
// re-sends ContinuousMove every 80ms while the joystick is held. That shape does
// not survive a browser: a viewer that closes the tab mid-drag would leave the
// pump (and the camera) running.
//
// So the web surface is stateless instead: every /move is one ContinuousMove
// carrying an explicit ONVIF Timeout, and the camera self-stops when the next
// refresh doesn't arrive. The client repeats /move while a button is held and
// sends /stop on release; a dead client costs at most one timeout of drift.
// Same safety property as PtzController's 2×tick timeout, no server-side state.
public static class PtzApi
{
    // Client refresh cadence is ~500ms (see PtzPad.tsx); the timeout must exceed
    // it or the camera stutters between ticks.
    private static readonly TimeSpan DefaultMoveTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan MaxMoveTimeout = TimeSpan.FromSeconds(3);

    public static void MapPtzEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/cameras/{id}/ptz/move", async (
            string id, PtzMoveRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            var velocity = new PtzVelocity(
                Clamp(body?.PanX), Clamp(body?.TiltY), Clamp(body?.Zoom));
            var timeout = ResolveTimeout(body?.TimeoutMs);

            return await InvokeAsync(ctx, "move", () =>
                target!.Value.Client.ContinuousMoveAsync(
                    target.Value.Endpoint, target.Value.ProfileToken, velocity, timeout, ct));
        });

        app.MapPost("/api/v1/cameras/{id}/ptz/stop", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            return await InvokeAsync(ctx, "stop", () =>
                target!.Value.Client.StopPtzAsync(target.Value.Endpoint, target.Value.ProfileToken, ct));
        });

        app.MapGet("/api/v1/cameras/{id}/ptz/presets", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            return await InvokeAsync(ctx, "presets", async () =>
            {
                var presets = await target!.Value.Client.GetPresetsAsync(
                    target.Value.Endpoint, target.Value.ProfileToken, ct);
                return Results.Json(presets);
            });
        });

        app.MapPost("/api/v1/cameras/{id}/ptz/presets", async (
            string id, PtzPresetRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            var name = body?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                return ValidationError("preset name is required");

            return await InvokeAsync(ctx, "preset.create", async () =>
            {
                var token = await target!.Value.Client.SetPresetAsync(
                    target.Value.Endpoint, target.Value.ProfileToken, name, ct);
                Audit(ctx, "ptz.preset.create", $"{id}/{name}");
                return Results.Json(new PtzPreset(token, name));
            });
        });

        app.MapPost("/api/v1/cameras/{id}/ptz/presets/{token}/goto", async (
            string id, string token, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            return await InvokeAsync(ctx, "preset.goto", () =>
                target!.Value.Client.GotoPresetAsync(
                    target.Value.Endpoint, target.Value.ProfileToken, token, ct));
        });

        app.MapDelete("/api/v1/cameras/{id}/ptz/presets/{token}", async (
            string id, string token, HttpContext ctx, CancellationToken ct) =>
        {
            var (target, error) = await TryResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            return await InvokeAsync(ctx, "preset.remove", async () =>
            {
                await target!.Value.Client.RemovePresetAsync(
                    target.Value.Endpoint, target.Value.ProfileToken, token, ct);
                Audit(ctx, "ptz.preset.remove", $"{id}/{token}");
                return Results.NoContent();
            });
        });
    }

    // Everything a PTZ call needs: the ONVIF transport plus the camera's endpoint
    // and media profile. Credentials come from the secrets store, never the API.
    private readonly record struct PtzTarget(IOnvifClient Client, OnvifEndpoint Endpoint, string ProfileToken);

    private static async Task<(PtzTarget? Target, IResult? Error)> TryResolveAsync(
        HttpContext ctx, string id, CancellationToken ct)
    {
        // Every PTZ endpoint funnels through here, so one check covers them all:
        // the caller needs the Ptz permission and must be allowed this camera.
        if (ctx.Deny(WebPermission.Ptz) is { } forbidden)
            return (null, forbidden);
        if (ctx.DenyCamera(id) is { } hidden)
            return (null, hidden);

        var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
        var onvif = ctx.RequestServices.GetService<IOnvifClient>();
        if (dir is null || onvif is null)
            return (null, BackendUnavailable());
        if (!Guid.TryParse(id, out var guid))
            return (null, ValidationError("invalid camera id"));

        var camera = await dir.GetAsync(new CameraId(guid), ct);
        if (camera is null)
            return (null, NotFound());
        // Mirrors SingleCameraPageViewModel.HasPtz: the flag alone is not enough,
        // ONVIF calls need a media profile token from the probe.
        if (!camera.HasPtz || string.IsNullOrEmpty(camera.OnvifProfileToken))
            return (null, PtzUnavailable());

        var credentials = await dir.GetCredentialsAsync(camera.Id, ct);
        var endpoint = OnvifEndpoint.FromHost(camera.Host, camera.OnvifPort ?? 80, credentials);
        return (new PtzTarget(onvif, endpoint, camera.OnvifProfileToken!), null);
    }

    private static Task<IResult> InvokeAsync(HttpContext ctx, string operation, Func<Task> action) =>
        InvokeAsync(ctx, operation, async () =>
        {
            await action();
            return Results.NoContent();
        });

    // One funnel for camera-side failures: an unreachable or grumpy ONVIF stack
    // is an upstream problem (502), logged server-side so the response body stays
    // free of host/credential detail.
    private static async Task<IResult> InvokeAsync(HttpContext ctx, string operation, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            // Viewer navigated away mid-call — nothing to report.
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web.Ptz");
            logger.LogWarning(ex, "PTZ {Operation} failed", operation);
            return Results.Json(new { error = "ptz_failed" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult PtzUnavailable() =>
        Results.Json(new { error = "ptz_unavailable" }, statusCode: StatusCodes.Status409Conflict);

    private static float Clamp(float? value) =>
        value is not { } v || float.IsNaN(v) ? 0f : Math.Clamp(v, -1f, 1f);

    private static TimeSpan ResolveTimeout(int? milliseconds)
    {
        if (milliseconds is not { } ms || ms <= 0)
            return DefaultMoveTimeout;
        var requested = TimeSpan.FromMilliseconds(ms);
        return requested > MaxMoveTimeout ? MaxMoveTimeout : requested;
    }
}

// Axes are ONVIF-normalized [-1, 1]; TimeoutMs is the self-stop window the camera
// applies when the next refresh doesn't arrive.
internal sealed record PtzMoveRequest(float? PanX, float? TiltY, float? Zoom, int? TimeoutMs);

internal sealed record PtzPresetRequest(string? Name);
