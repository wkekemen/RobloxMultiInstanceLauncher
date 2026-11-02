using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using RobloxMultiLauncher.Services;

namespace RobloxMultiLauncher.Interop;

public sealed class DwmThumbnailHost : FrameworkElement
{
    public static readonly DependencyProperty SourceHandleProperty =
        DependencyProperty.Register(
            nameof(SourceHandle),
            typeof(IntPtr),
            typeof(DwmThumbnailHost),
            new PropertyMetadata(IntPtr.Zero, OnSourceHandleChanged));

    private IntPtr _thumbnail;
    private NativeRect _lastDestination;
    private bool _hasLastDestination;
    private bool _thumbnailVisible;
    private ScrollViewer? _scrollViewer;
    private readonly ScrollChangedEventHandler _scrollChangedHandler;

    public IntPtr SourceHandle
    {
        get => (IntPtr)GetValue(SourceHandleProperty);
        set => SetValue(SourceHandleProperty, value);
    }

    public DwmThumbnailHost()
    {
        _scrollChangedHandler = (_, _) => UpdateThumbnail();
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            AttachScrollViewer();
            RegisterThumbnail();
        };
        Unloaded += (_, _) =>
        {
            DetachScrollViewer();
            UnregisterThumbnail();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RegisterThumbnail();
            else UnregisterThumbnail();
        };
        LayoutUpdated += (_, _) => UpdateThumbnail();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateThumbnail();
    }

    private static void OnSourceHandleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (DwmThumbnailHost)d;
        host.UnregisterThumbnail();
        host.RegisterThumbnail();
    }

    private void RegisterThumbnail()
    {
        if (!IsVisible || SourceHandle == IntPtr.Zero || _thumbnail != IntPtr.Zero) return;

        var window = Window.GetWindow(this);
        if (window == null) return;

        IntPtr destination = new WindowInteropHelper(window).Handle;
        if (destination == IntPtr.Zero) return;

        int result = DwmRegisterThumbnail(destination, SourceHandle, out _thumbnail);
        if (result != 0)
        {
            _thumbnail = IntPtr.Zero;
            Logger.Warn($"Could not create a live window preview (DWM error 0x{result:X8}).");
            return;
        }
        _thumbnailVisible = false;
        UpdateThumbnail();
    }

    private void UpdateThumbnail()
    {
        if (_thumbnail == IntPtr.Zero || !IsVisible) return;

        var window = Window.GetWindow(this);
        if (window == null) return;

        Rect bounds;
        try
        {
            bounds = TransformToAncestor(window).TransformBounds(new Rect(RenderSize));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!IsFullyInsideScrollViewport(bounds, window))
        {
            SetThumbnailVisibility(false);
            return;
        }

        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice;
        if (transform == null) return;

        var topLeft = transform.Value.Transform(bounds.TopLeft);
        var bottomRight = transform.Value.Transform(bounds.BottomRight);
        var destination = new NativeRect(
            (int)Math.Floor(topLeft.X),
            (int)Math.Floor(topLeft.Y),
            (int)Math.Ceiling(bottomRight.X),
            (int)Math.Ceiling(bottomRight.Y));
        if (destination.Right <= destination.Left || destination.Bottom <= destination.Top) return;
        if (_thumbnailVisible && _hasLastDestination &&
            destination.Left == _lastDestination.Left &&
            destination.Top == _lastDestination.Top &&
            destination.Right == _lastDestination.Right &&
            destination.Bottom == _lastDestination.Bottom) return;

        var properties = new ThumbnailProperties
        {
            Flags = ThumbnailPropertyFlags.DestinationRect | ThumbnailPropertyFlags.Visible |
                    ThumbnailPropertyFlags.Opacity | ThumbnailPropertyFlags.SourceClientAreaOnly,
            Destination = destination,
            Opacity = 255,
            Visible = 1,
            SourceClientAreaOnly = 1
        };
        int result = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        if (result != 0)
        {
            Logger.Warn($"Could not update a live window preview (DWM error 0x{result:X8}).");
            return;
        }
        _lastDestination = destination;
        _hasLastDestination = true;
        _thumbnailVisible = true;
    }

    private bool IsFullyInsideScrollViewport(Rect bounds, Window window)
    {
        if (_scrollViewer == null) return true;
        try
        {
            var viewport = _scrollViewer.TransformToAncestor(window).TransformBounds(new Rect(_scrollViewer.RenderSize));
            return bounds.Left >= viewport.Left && bounds.Top >= viewport.Top &&
                   bounds.Right <= viewport.Right && bounds.Bottom <= viewport.Bottom;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetThumbnailVisibility(bool visible)
    {
        if (_thumbnail == IntPtr.Zero || _thumbnailVisible == visible) return;
        var properties = new ThumbnailProperties
        {
            Flags = ThumbnailPropertyFlags.Visible,
            Visible = visible ? 1 : 0
        };
        int result = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        if (result != 0)
        {
            Logger.Warn($"Could not update live preview visibility (DWM error 0x{result:X8}).");
            return;
        }
        _thumbnailVisible = visible;
    }

    private void AttachScrollViewer()
    {
        DetachScrollViewer();
        DependencyObject? current = VisualTreeHelper.GetParent(this);
        while (current != null && current is not ScrollViewer)
            current = VisualTreeHelper.GetParent(current);
        _scrollViewer = current as ScrollViewer;
        _scrollViewer?.AddHandler(ScrollViewer.ScrollChangedEvent, _scrollChangedHandler);
    }

    private void DetachScrollViewer()
    {
        _scrollViewer?.RemoveHandler(ScrollViewer.ScrollChangedEvent, _scrollChangedHandler);
        _scrollViewer = null;
    }

    private void UnregisterThumbnail()
    {
        if (_thumbnail == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
        _hasLastDestination = false;
        _thumbnailVisible = false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(int left, int top, int right, int bottom) =>
            (Left, Top, Right, Bottom) = (left, top, right, bottom);
    }

    [Flags]
    private enum ThumbnailPropertyFlags
    {
        DestinationRect = 0x1,
        Visible = 0x8,
        Opacity = 0x4,
        SourceClientAreaOnly = 0x10
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public ThumbnailPropertyFlags Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        public int Visible;
        public int SourceClientAreaOnly;
    }
}
