using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Core.Services;

public sealed class CameraDirectoryService
{
    private readonly ICameraRepository _cameras;
    private readonly ISecretsStore _secrets;

    public CameraDirectoryService(ICameraRepository cameras, ISecretsStore secrets)
    {
        _cameras = cameras;
        _secrets = secrets;
    }

    public Task<IReadOnlyList<Camera>> ListAsync(CancellationToken ct) =>
        _cameras.GetAllAsync(ct);

    public Task<Camera?> GetAsync(CameraId id, CancellationToken ct) =>
        _cameras.GetAsync(id, ct);

    public async Task<CameraId> AddAsync(NewCameraRequest req, CancellationToken ct)
    {
        var id = CameraId.New();
        var (usernameRef, passwordRef) = await StoreCredentialsAsync(id, req.Credentials, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var camera = new Camera(
            Id: id,
            GroupId: req.GroupId,
            Name: req.Name,
            Host: req.Host,
            OnvifPort: req.OnvifPort,
            HttpPort: req.HttpPort,
            RtspMainUri: req.RtspMainUri,
            RtspSubUri: req.RtspSubUri,
            UsernameRef: usernameRef,
            PasswordRef: passwordRef,
            OnvifEnabled: false,
            OnvifProfileToken: null,
            ChipModel: null,
            FirmwareVersion: null,
            IncludedInGrid: true,
            SortOrder: 0,
            CreatedAt: now,
            UpdatedAt: now);

        return await _cameras.AddAsync(camera, ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(CameraId id, UpdateCameraRequest req, CancellationToken ct)
    {
        var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Camera {id} not found");

        var (usernameRef, passwordRef) = req.Credentials is null
            ? (existing.UsernameRef, existing.PasswordRef)
            : await StoreCredentialsAsync(id, req.Credentials, ct).ConfigureAwait(false);

        var updated = existing with
        {
            Name = req.Name,
            Host = req.Host,
            HttpPort = req.HttpPort,
            OnvifPort = req.OnvifPort,
            RtspMainUri = req.RtspMainUri,
            RtspSubUri = req.RtspSubUri,
            UsernameRef = usernameRef,
            PasswordRef = passwordRef,
            GroupId = req.GroupId,
            UpdatedAt = DateTime.UtcNow,
        };

        await _cameras.UpdateAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task SetIncludedInGridAsync(CameraId id, bool included, CancellationToken ct)
    {
        var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Camera {id} not found");
        var updated = existing with { IncludedInGrid = included, UpdatedAt = DateTime.UtcNow };
        await _cameras.UpdateAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(CameraId id, CancellationToken ct)
    {
        await _secrets.RemoveAsync(UsernameKey(id), ct).ConfigureAwait(false);
        await _secrets.RemoveAsync(PasswordKey(id), ct).ConfigureAwait(false);
        await _cameras.RemoveAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<CameraCredentials?> GetCredentialsAsync(CameraId id, CancellationToken ct)
    {
        var username = await _secrets.GetAsync(UsernameKey(id), ct).ConfigureAwait(false);
        var password = await _secrets.GetAsync(PasswordKey(id), ct).ConfigureAwait(false);
        if (username is null || password is null)
            return null;
        return new CameraCredentials(username, password);
    }

    private async Task<(string? UsernameRef, string? PasswordRef)> StoreCredentialsAsync(
        CameraId id, CameraCredentials? credentials, CancellationToken ct)
    {
        if (credentials is null)
        {
            await _secrets.RemoveAsync(UsernameKey(id), ct).ConfigureAwait(false);
            await _secrets.RemoveAsync(PasswordKey(id), ct).ConfigureAwait(false);
            return (null, null);
        }

        var usernameKey = UsernameKey(id);
        var passwordKey = PasswordKey(id);
        await _secrets.SetAsync(usernameKey, credentials.Username, ct).ConfigureAwait(false);
        await _secrets.SetAsync(passwordKey, credentials.Password, ct).ConfigureAwait(false);
        return (usernameKey, passwordKey);
    }

    private static string UsernameKey(CameraId id) => $"cam:{id}:username";
    private static string PasswordKey(CameraId id) => $"cam:{id}:password";
}
