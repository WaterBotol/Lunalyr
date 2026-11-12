using System.Runtime.InteropServices;
using Lunalyr.Core.Logging;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.App.Player;

/// <summary>
/// The card's window and what it draws into: a borderless pop-up with no redirection bitmap, whose one
/// DirectComposition visual is a premultiplied-alpha flip swap chain drawn with Direct2D. Windows 11 rounds the window
/// (DWM; no shadow for a pop-up), so the window is exactly the card: clicks beside it reach the desktop. Everything
/// here runs on the UI thread; the device is small and separate from the wallpaper's.
/// </summary>
internal sealed unsafe class CardSurface : IDisposable
{
    public const string WindowTitle = "Lunalyr player";

    private readonly string _className;
    private readonly CategoryLogger _log;
    private ID3D11Device? _d3d;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2d;
    private IDXGISwapChain1? _swap;
    private ID2D1Bitmap1? _target;
    private Composition? _composition;
    private int _width, _height;

    public CardSurface(string className, CategoryLogger log)
    {
        _className = className;
        _log = log;
    }

    public nint Hwnd { get; private set; }

    public ID2D1DeviceContext? Dc { get; private set; }

    public ID2D1Factory1 Factory => _d2dFactory ??= D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);

    public int Width => _width;

    public int Height => _height;

    /// <summary>Creates the (hidden) window with <paramref name="wndProc"/> as its procedure.</summary>
    public void CreateWindow(delegate* unmanaged<nint, uint, nuint, nint, nint> wndProc)
    {
        var instance = Lunalyr.App.Interop.Win32.GetModuleHandle(null);
        fixed (char* name = _className)
        {
            var wc = new PlayerNative.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(PlayerNative.WNDCLASSEXW),
                lpfnWndProc = wndProc,
                hInstance = instance,
                hCursor = PlayerNative.LoadCursor(0, (nint)PlayerNative.IDC_ARROW),
                lpszClassName = name,
            };
            if (PlayerNative.RegisterClassEx(wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
        }

        // A tool window (no taskbar button, not in Alt+Tab) that is not activated when shown; a click activates it so
        // the keys reach it.
        Hwnd = PlayerNative.CreateWindowEx(PlayerNative.WS_EX_TOOLWINDOW | PlayerNative.WS_EX_NOREDIRECTIONBITMAP, _className,
            WindowTitle, PlayerNative.WS_POPUP, 0, 0, 1, 1, 0, 0, instance, 0);
        if (Hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        var round = PlayerNative.DWMWCP_ROUND;
        PlayerNative.DwmSetWindowAttribute(Hwnd, PlayerNative.DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
    }

    /// <summary>The window's 1 px DWM border, as 0x00BBGGRR (or <c>0xFFFFFFFE</c> for none).</summary>
    public void SetBorderColour(uint colorRef) =>
        PlayerNative.DwmSetWindowAttribute(Hwnd, PlayerNative.DWMWA_BORDER_COLOR, &colorRef, sizeof(uint));

    /// <summary>Makes sure the swap chain is <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public void EnsureSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_swap is null) CreateDevice(width, height);
        else if (width != _width || height != _height)
        {
            Dc!.Target = null;
            _target?.Dispose();
            _target = null;
            _swap.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            _width = width;
            _height = height;
            CreateTarget();
        }
    }

    /// <summary>Draws one frame with <paramref name="draw"/> and presents it. False when the device was lost (it is rebuilt next time).</summary>
    public bool Draw(Action<ID2D1DeviceContext> draw)
    {
        if (Dc is null || _target is null || _swap is null) return false;
        Dc.Target = _target;
        Dc.BeginDraw();
        try
        {
            draw(Dc);
        }
        finally
        {
            var result = Dc.EndDraw();
            Dc.Target = null;
            if (result.Failure) _log.Warn($"Desktop player: drawing failed (0x{result.Code:X8}).");
        }

        var present = _swap.Present(1, PresentFlags.None);
        if (present.Failure)
        {
            _log.Warn($"Desktop player: present failed (0x{present.Code:X8}); building the card's device again.");
            ReleaseDevice();
            return false;
        }

        return true;
    }

    private void CreateDevice(int width, int height)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0],
            out ID3D11Device? d3d, out ID3D11DeviceContext? context).CheckError();
        context!.Dispose();
        _d3d = d3d!;
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        _d2d = Factory.CreateDevice(dxgi);
        Dc = _d2d.CreateDeviceContext(DeviceContextOptions.None);
        Dc.TextAntialiasMode = TextAntialiasMode.Grayscale;
        using var adapter = dxgi.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential, AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied, Scaling = Scaling.Stretch,
        };
        _swap = factory.CreateSwapChainForComposition(_d3d, desc);
        _width = width;
        _height = height;
        CreateTarget();
        _composition = new Composition(dxgi.NativePointer, Hwnd, _swap.NativePointer);
    }

    private void CreateTarget()
    {
        using var surface = _swap!.GetBuffer<IDXGISurface>(0);
        _target = Dc!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    /// <summary>Lets go of the device (it is made again on the next <see cref="EnsureSize"/>).</summary>
    public void ReleaseDevice()
    {
        if (Dc is not null) Dc.Target = null;
        _target?.Dispose();
        _target = null;
        _composition?.Dispose();
        _composition = null;
        _swap?.Dispose();
        _swap = null;
        Dc?.Dispose();
        Dc = null;
        _d2d?.Dispose();
        _d2d = null;
        _d3d?.Dispose();
        _d3d = null;
        _width = _height = 0;
    }

    /// <summary>Raised when the device was released: resources made on it (bitmaps, brushes) must be made again.</summary>
    public bool HasDevice => Dc is not null;

    public void Dispose()
    {
        ReleaseDevice();
        _d2dFactory?.Dispose();
        _d2dFactory = null;
        if (Hwnd != 0)
        {
            PlayerNative.DestroyWindow(Hwnd);
            Hwnd = 0;
            PlayerNative.UnregisterClass(_className, Lunalyr.App.Interop.Win32.GetModuleHandle(null));
        }
    }
}
