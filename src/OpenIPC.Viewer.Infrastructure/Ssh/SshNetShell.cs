using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Ssh;
using Renci.SshNet;

namespace OpenIPC.Viewer.Infrastructure.Ssh;

/// <summary>Wraps SSH.NET's <see cref="ShellStream"/> behind <see cref="ISshShell"/>.</summary>
/// <remarks>
/// Both directions run on their own thread, and neither is the caller's.
/// <para>
/// Reading: <see cref="ShellStream"/> keeps an internal buffer that only a read drains — its
/// <c>DataReceived</c> event hands out a copy and leaves the original in place. Listening to the
/// event alone therefore grows that buffer for the life of the session, every byte the camera
/// ever printed still held in memory. A read loop is the supported way to consume the stream, so
/// the event is left alone and the loop republishes what it reads.
/// </para>
/// <para>
/// Writing: a write goes out over the channel and blocks while the peer's window is full, so
/// sending straight from the caller froze whoever called — for the terminal that is the UI
/// thread, and a paste large enough to fill the window locked the app until the far end caught
/// up. Sends are queued instead and drained by one pump, which keeps them in order.
/// </para>
/// </remarks>
internal sealed class SshNetShell : ISshShell
{
    private readonly ShellStream _stream;
    private readonly ILogger _logger;
    private readonly Channel<string> _outgoing =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _reader;
    private readonly Task _writer;
    private int _disposed;

    // The loop starts with the stream, which is before the terminal has had a chance to subscribe,
    // so the first thing the camera says — the login banner and the first prompt — would arrive
    // with nobody listening and be lost. Data received before then waits here.
    private readonly object _sinkLock = new();
    private EventHandler<byte[]>? _sink;
    private List<byte[]>? _pending = new();

    public event EventHandler<byte[]>? DataReceived
    {
        add
        {
            lock (_sinkLock)
            {
                _sink += value;
                if (_pending is null)
                    return;
                foreach (var data in _pending)
                    value?.Invoke(this, data);
                _pending = null;
            }
        }
        remove
        {
            lock (_sinkLock)
                _sink -= value;
        }
    }

    private void Publish(byte[] data)
    {
        lock (_sinkLock)
        {
            if (_sink is not null)
            {
                _sink.Invoke(this, data);
                return;
            }
            // Bounded: nobody is coming for this if the terminal never attached.
            if (_pending is { Count: < 64 })
                _pending.Add(data);
        }
    }

    public SshNetShell(ShellStream stream, ILogger logger)
    {
        _stream = stream;
        _logger = logger;
        _reader = Task.Factory.StartNew(
            ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _writer = Task.Run(WriteLoopAsync);
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                // Blocks until the camera says something; returns 0 once the stream is closed.
                var read = _stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    return;

                var data = new byte[read];
                Array.Copy(buffer, data, read);
                Publish(data);
            }
        }
        catch (ObjectDisposedException)
        {
            // Closing the session while a read is parked — the normal way out.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSH shell read loop ended");
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var text in _outgoing.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await _stream.WriteAsync(bytes, _stopping.Token).ConfigureAwait(false);
                await _stream.FlushAsync(_stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSH shell write loop ended");
        }
    }

    public Task SendAsync(string data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _outgoing.Writer.TryWrite(data);
        return Task.CompletedTask;
    }

    // SSH.NET's ShellStream exposes no mid-session window-change; the PTY keeps
    // the size it was created with. Logged so the terminal UI knows the resize
    // was a no-op rather than silently dropped (basic-VT scope, phase-13 §13.3).
    public void Resize(uint columns, uint rows) =>
        _logger.LogDebug("SSH shell resize to {Cols}x{Rows} ignored (PTY fixed at open)", columns, rows);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_sinkLock)
        {
            _sink = null;
            _pending = null;
        }
        _outgoing.Writer.TryComplete();
        _stopping.Cancel();
        // Disposing the stream is what unblocks the parked read.
        _stream.Dispose();

        // Bounded: a pump stuck on a dead socket must not hold up closing the terminal.
        await Task.WhenAny(Task.WhenAll(_reader, _writer), Task.Delay(TimeSpan.FromSeconds(2)))
            .ConfigureAwait(false);
    }
}
