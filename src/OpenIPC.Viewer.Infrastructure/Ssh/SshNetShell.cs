using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OpenIPC.Viewer.Infrastructure.Ssh;

/// <summary>Wraps SSH.NET's <see cref="ShellStream"/> behind <see cref="ISshShell"/>.</summary>
internal sealed class SshNetShell : ISshShell
{
    private readonly ShellStream _stream;
    private readonly ILogger _logger;

    public event EventHandler<byte[]>? DataReceived;

    public SshNetShell(ShellStream stream, ILogger logger)
    {
        _stream = stream;
        _logger = logger;
        _stream.DataReceived += OnStreamData;
    }

    private void OnStreamData(object? sender, ShellDataEventArgs e) =>
        DataReceived?.Invoke(this, e.Data);

    public Task SendAsync(string data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _stream.Write(data);
        _stream.Flush();
        return Task.CompletedTask;
    }

    // SSH.NET's ShellStream exposes no mid-session window-change; the PTY keeps
    // the size it was created with. Logged so the terminal UI knows the resize
    // was a no-op rather than silently dropped (basic-VT scope, phase-13 §13.3).
    public void Resize(uint columns, uint rows) =>
        _logger.LogDebug("SSH shell resize to {Cols}x{Rows} ignored (PTY fixed at open)", columns, rows);

    public ValueTask DisposeAsync()
    {
        _stream.DataReceived -= OnStreamData;
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
