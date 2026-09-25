using System;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Controls;

public sealed partial class RtspVideoView : UserControl
{
    public static readonly StyledProperty<IVideoSession?> SessionProperty =
        AvaloniaProperty.Register<RtspVideoView, IVideoSession?>(nameof(Session));

    public IVideoSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    // Decoded frame size, 0 until the first frame. Lets zoom hosts fit the
    // letterboxed picture without the page's VM having to know it.
    public static readonly DirectProperty<RtspVideoView, int> FrameWidthProperty =
        AvaloniaProperty.RegisterDirect<RtspVideoView, int>(nameof(FrameWidth), o => o.FrameWidth);

    public static readonly DirectProperty<RtspVideoView, double> FrameAspectProperty =
        AvaloniaProperty.RegisterDirect<RtspVideoView, double>(nameof(FrameAspect), o => o.FrameAspect);

    private int _frameWidth;
    private double _frameAspect;

    public int FrameWidth { get => _frameWidth; private set => SetAndRaise(FrameWidthProperty, ref _frameWidth, value); }
    public double FrameAspect { get => _frameAspect; private set => SetAndRaise(FrameAspectProperty, ref _frameAspect, value); }

    private readonly Image _image;
    private WriteableBitmap? _bitmap;
    private IDisposable? _frameSub;

    // The bitmap the latest frame was copied into, for secondary views of the
    // same picture (the digital-zoom minimap). Read it on the UI thread only.
    public Bitmap? CurrentFrame => _bitmap;

    // Raised on the UI thread after each frame lands in CurrentFrame.
    public event EventHandler? FrameRendered;

    public RtspVideoView()
    {
        InitializeComponent();
        _image = this.FindControl<Image>("PART_Image")
                 ?? throw new InvalidOperationException("PART_Image missing");
    }

    static RtspVideoView()
    {
        SessionProperty.Changed.AddClassHandler<RtspVideoView>((view, _) => view.OnSessionChanged());
    }

    private void OnSessionChanged()
    {
        _frameSub?.Dispose();
        _frameSub = Session?.Frames.Subscribe(OnFrame);
    }

    // OnFrame fires on the decoder thread. We marshal to UI synchronously so the
    // frame's pooled buffer stays valid until the copy completes (see
    // FfmpegVideoSession comment in EmitFrame).
    private void OnFrame(VideoFrame frame)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            EnsureBitmap(frame.Width, frame.Height);
            if (_bitmap is null)
                return;

            using (var locked = _bitmap.Lock())
            {
                Marshal.Copy(frame.Bgra, 0, locked.Address, frame.Stride * frame.Height);
            }
            _image.InvalidateVisual();
            FrameRendered?.Invoke(this, EventArgs.Empty);
        });
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _bitmap.PixelSize.Width == width && _bitmap.PixelSize.Height == height)
            return;

        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        _image.Source = _bitmap;
        FrameWidth = width;
        FrameAspect = height > 0 ? (double)width / height : 0;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _frameSub?.Dispose();
        _frameSub = null;
        base.OnDetachedFromVisualTree(e);
    }
}
