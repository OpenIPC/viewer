using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using OpenIPC.Viewer.App;

namespace OpenIPC.Viewer.Desktop;

// Opt-in single-instance mode (Settings → Advanced). The first process always
// takes the named mutex and serves the activation pipe, even with the setting
// off — that way enabling the toggle later protects against copies launched
// afterwards without restarting the app that's already running. A losing
// process consults the setting itself (read straight from usersettings.json,
// before DI or Avalonia spin up) and, when enforcement is on, pings the pipe
// so the existing window comes to the foreground, then exits.
internal static class SingleInstanceGuard
{
    // User-scoped names so two OS sessions each get their own slot. "Local\"
    // confines the mutex to the current session on Windows; other platforms
    // treat the prefix as part of the name, which is equally fine.
    private static readonly string MutexName =
        @"Local\OpenIPC.Viewer.instance." + Uri.EscapeDataString(Environment.UserName);
    private static readonly string PipeName =
        "OpenIPC.Viewer.activate." + Uri.EscapeDataString(Environment.UserName);

    private static Mutex? _mutex; // held for the process lifetime on purpose

    // True → this process is the primary: it owns the mutex and answers
    // activation pings. False → another copy already runs.
    public static bool TryBecomePrimary()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }
        _mutex = mutex;
        StartActivationListener();
        return true;
    }

    // The user's toggle, read without the service stack: Main needs the answer
    // before Composition.Build() opens logs/db. Property casing matches what
    // UserSettingsService writes (PascalCase, no naming policy).
    public static bool IsEnabledInSettings()
    {
        try
        {
            var path = Path.Combine(AppPaths.AppDataDir.FullName, "usersettings.json");
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.TryGetProperty("SingleInstance", out var v)
                   && v.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false; // corrupt/unreadable settings must never block startup
        }
    }

    // Second-instance side: ask the primary to come to the foreground.
    // Best-effort — if the primary is mid-startup or wedged, we just exit.
    public static void ActivatePrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            client.WriteByte(1);
            client.Flush();
        }
        catch
        {
            // Nothing useful to do: the user still sees the primary window
            // (possibly minimized), and this process is about to exit anyway.
        }
    }

    private static void StartActivationListener()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);
                    server.WaitForConnection();
                    server.ReadByte(); // any payload = "bring yourself to front"
                    Dispatcher.UIThread.Post(ActivateMainWindow);
                }
                catch
                {
                    // Client vanished mid-handshake or the dispatcher isn't up
                    // yet — drop this ping and keep serving the next one.
                }
            }
        })
        { IsBackground = true, Name = "single-instance-activate" };
        thread.Start();
    }

    private static void ActivateMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            return;
        window.Show(); // un-hides a close-to-tray window
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
