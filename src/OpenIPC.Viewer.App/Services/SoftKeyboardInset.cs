using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;

namespace OpenIPC.Viewer.App.Services;

// How much room the app still has to make for the soft keyboard, after whatever the platform
// already did about it.
//
// The activity asks for AdjustResize, and for a long time that was the whole story: Android shrank
// the window, ClientSize shrank with it, and every layout above simply had less room. Under the
// edge-to-edge display a targetSdk-36 app gets, that is no longer guaranteed — the keyboard can
// slide over the content instead, leaving a terminal prompt or a password field underneath it.
//
// Which of the two happens is not something to guess at, and padding by the keyboard's full height
// on a window that was already shrunk lifts the content twice as far as it should. So measure it:
// remember how tall the client area is while the keyboard is down, and when it comes up, ask for
// only the part of it that the window did not already give up.
public static class SoftKeyboardInset
{
    // A keyboard takes roughly a third to a half of the screen. Anything past this is a number we
    // don't understand, and acting on it would push the whole UI off the top.
    private const double MaxShareOfScreen = 0.7;

    // The client size with the keyboard down, per window. The width is stored with it because that
    // is what tells rotation apart from the keyboard: turning the phone changes both dimensions, and
    // a baseline taken in portrait says nothing about how tall a landscape window should be. Weak so
    // a closed window is not held alive by this.
    private static readonly ConditionalWeakTable<TopLevel, StrongBox<Size>> Baselines = new();

    /// <summary>
    /// Height the app must keep clear at the bottom for the soft keyboard, in DIPs. Zero when the
    /// keyboard is down, and zero when the platform already resized the window for it.
    /// </summary>
    public static double Of(TopLevel? top)
    {
        if (top?.InputPane is not { } pane)
            return 0;

        var height = top.ClientSize.Height;
        if (height <= 0)
            return 0;

        if (pane.State != InputPaneState.Open)
        {
            // The keyboard is down, so this is the full size to measure against later.
            Baselines.GetValue(top, _ => new StrongBox<Size>(top.ClientSize)).Value = top.ClientSize;
            return 0;
        }

        // A rect positioned in the window says where the keyboard starts; one reported at the
        // origin is only saying how tall it is. Both readings mean the same height.
        var rect = pane.OccludedRect;
        var keyboard = rect.Y > 0 ? height - rect.Y : rect.Height;

        // What the platform already took out of the window on its own — but only if the baseline is
        // still about this window shape. Rotating with the keyboard up left a portrait height on
        // record, which made the landscape window look as though the platform had already given up
        // 400dp for the keyboard: the inset came out zero and the keys covered the prompt.
        var surrendered = Baselines.TryGetValue(top, out var baseline)
            && Math.Abs(baseline.Value.Width - top.ClientSize.Width) < 0.5
                ? Math.Max(0, baseline.Value.Height - height)
                : 0;

        return Math.Clamp(keyboard - surrendered, 0, height * MaxShareOfScreen);
    }

    /// <summary>Calls <paramref name="onChanged"/> whenever the keyboard opens or closes.</summary>
    public static IDisposable Subscribe(TopLevel? top, Action onChanged)
    {
        if (top?.InputPane is not { } pane)
            return NoSubscription.Instance;

        void Handler(object? sender, InputPaneStateEventArgs e) => onChanged();
        pane.StateChanged += Handler;
        return new Unsubscribe(() => pane.StateChanged -= Handler);
    }

    private sealed class Unsubscribe : IDisposable
    {
        private Action? _dispose;
        public Unsubscribe(Action dispose) => _dispose = dispose;
        public void Dispose()
        {
            var d = _dispose;
            _dispose = null;
            d?.Invoke();
        }
    }

    private sealed class NoSubscription : IDisposable
    {
        public static readonly NoSubscription Instance = new();
        public void Dispose() { }
    }
}
