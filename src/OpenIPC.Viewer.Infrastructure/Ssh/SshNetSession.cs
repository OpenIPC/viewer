using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Settings;
using OpenIPC.Viewer.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OpenIPC.Viewer.Infrastructure.Ssh;

/// <summary>
/// SSH.NET-backed <see cref="ISshSession"/>. Holds a shell/exec client and a
/// separate SCP client over the same endpoint. Host keys are pinned on first
/// use (TOFU) via the secrets store.
/// </summary>
internal sealed class SshNetSession : ISshSession
{
    private readonly ISshHostKeyStore _hostKeys;
    private readonly IUserSettingsAccessor _settings;
    private readonly ILogger _logger;

    private SshClient? _ssh;
    private ScpClient? _scp;

    private string? _knownFingerprint;
    private bool _shouldStore;
    private bool _mismatch;
    private bool _strict = true;
    private string _host = "";
    private int _port;

    public SshNetSession(ISshHostKeyStore hostKeys, IUserSettingsAccessor settings, ILogger logger)
    {
        _hostKeys = hostKeys;
        _settings = settings;
        _logger = logger;
    }

    public async Task ConnectAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        _host = endpoint.Host;
        _port = endpoint.Port;
        _strict = _settings.SshStrictHostKey;
        _knownFingerprint = await _hostKeys.GetAsync(endpoint.Host, endpoint.Port, ct).ConfigureAwait(false);

        _ssh = new SshClient(BuildConnectionInfo(endpoint));
        _scp = new ScpClient(BuildConnectionInfo(endpoint));
        _ssh.HostKeyReceived += OnHostKeyReceived;
        _scp.HostKeyReceived += OnHostKeyReceived;

        await _ssh.ConnectAsync(ct).ConfigureAwait(false);
        await _scp.ConnectAsync(ct).ConfigureAwait(false);

        if (_mismatch)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new SshConnectionException(
                $"Host key for {endpoint.Host}:{endpoint.Port} changed — refusing to connect.");
        }

        if (_shouldStore && _knownFingerprint is { } fp)
        {
            await _hostKeys.SetAsync(endpoint.Host, endpoint.Port, fp, ct).ConfigureAwait(false);
            _logger.LogInformation("Pinned SSH host key for {Host}:{Port} (SHA256 {Fingerprint})",
                endpoint.Host, endpoint.Port, fp);
        }
    }

    // Synchronous TOFU check: the captured fingerprint was loaded before connect
    // (the event handler can't await). First use trusts and pins. A changed key
    // is rejected under strict checking, or accepted and re-pinned when the user
    // turned strict off (e.g. a camera was reflashed).
    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var presented = e.FingerPrintSHA256;
        if (_knownFingerprint is null)
        {
            _knownFingerprint = presented;
            _shouldStore = true;
            e.CanTrust = true;
        }
        else if (string.Equals(_knownFingerprint, presented, StringComparison.Ordinal))
        {
            e.CanTrust = true;
        }
        else if (_strict)
        {
            e.CanTrust = false;
            _mismatch = true;
        }
        else
        {
            _knownFingerprint = presented;
            _shouldStore = true;
            e.CanTrust = true;
        }
    }

    public async Task<ISshShell> OpenShellAsync(uint columns, uint rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stream = Client.CreateShellStream("xterm", columns, rows, 0, 0, 4096);
        return await Task.FromResult(new SshNetShell(stream, _logger)).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RemoteEntry> ListAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var result = await ExecAsync($"ls -la {ShellQuote(path)}", ct).ConfigureAwait(false);
        if (!result.Success)
            throw new SshException($"ls failed for '{path}': {result.StandardError.Trim()}");

        foreach (var entry in LsParser.Parse(result.StandardOutput))
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    public Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            void OnDownloading(object? s, ScpDownloadEventArgs e) => progress?.Report(e.Downloaded);
            Scp.Downloading += OnDownloading;
            try
            {
                Scp.Download(remotePath, new FileInfo(localPath));
            }
            finally
            {
                Scp.Downloading -= OnDownloading;
            }
        }, ct);

    public Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            void OnUploading(object? s, ScpUploadEventArgs e) => progress?.Report(e.Uploaded);
            Scp.Uploading += OnUploading;
            try
            {
                Scp.Upload(new FileInfo(localPath), remotePath);
            }
            finally
            {
                Scp.Uploading -= OnUploading;
            }
        }, ct);

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        if (RemotePathGuard.IsProtected(remotePath))
            throw new InvalidOperationException($"Refusing to delete root-level path '{remotePath}'.");

        var result = await ExecAsync($"rm -rf {ShellQuote(remotePath)}", ct).ConfigureAwait(false);
        if (!result.Success)
            throw new SshException($"rm failed for '{remotePath}': {result.StandardError.Trim()}");
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken ct)
    {
        var result = await ExecAsync($"mkdir -p {ShellQuote(remotePath)}", ct).ConfigureAwait(false);
        if (!result.Success)
            throw new SshException($"mkdir failed for '{remotePath}': {result.StandardError.Trim()}");
    }

    public Task<CommandResult> ExecAsync(string command, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var cmd = Client.CreateCommand(command);
            cmd.Execute();
            return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
        }, ct);

    private SshClient Client =>
        _ssh ?? throw new InvalidOperationException("SSH session is not connected.");

    private ScpClient Scp =>
        _scp ?? throw new InvalidOperationException("SSH session is not connected.");

    private static ConnectionInfo BuildConnectionInfo(SshEndpoint ep)
    {
        AuthenticationMethod auth = ep.Auth switch
        {
            SshAuth.Password p => new PasswordAuthenticationMethod(ep.Username, p.Value),
            SshAuth.PrivateKey k => new PrivateKeyAuthenticationMethod(
                ep.Username, new PrivateKeyFile(k.KeyPath, k.Passphrase)),
            _ => throw new NotSupportedException($"Unsupported SSH auth: {ep.Auth.GetType().Name}"),
        };
        return new ConnectionInfo(ep.Host, ep.Port, ep.Username, auth);
    }

    // Wrap a path in single quotes for the remote shell, escaping embedded
    // single quotes ('\'' is the standard sh trick).
    private static string ShellQuote(string path) =>
        "'" + path.Replace("'", "'\\''") + "'";

    public ValueTask DisposeAsync()
    {
        if (_ssh is not null)
        {
            _ssh.HostKeyReceived -= OnHostKeyReceived;
            _ssh.Dispose();
            _ssh = null;
        }
        if (_scp is not null)
        {
            _scp.HostKeyReceived -= OnHostKeyReceived;
            _scp.Dispose();
            _scp = null;
        }
        return ValueTask.CompletedTask;
    }
}
