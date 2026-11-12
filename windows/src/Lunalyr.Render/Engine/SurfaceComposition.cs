using System.Runtime.InteropServices;

namespace Lunalyr.Render.Engine;

/// <summary>
/// The DirectComposition calls a see-through wallpaper needs: a target for its window, one visual, and the swap chain
/// (made for composition, premultiplied alpha) as the visual's content. Made through the COM vtables, as the desktop
/// player's card does, so there is no package for so little.
/// </summary>
internal sealed unsafe partial class SurfaceComposition : IDisposable
{
    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    private nint _device, _target, _visual;

    /// <param name="dxgiDevice">The rendering device's <c>IDXGIDevice</c>.</param>
    /// <param name="hwnd">The surface's window.</param>
    /// <param name="swapChain">An <c>IDXGISwapChain1</c> made with <c>CreateSwapChainForComposition</c>.</param>
    public SurfaceComposition(nint dxgiDevice, nint hwnd, nint swapChain)
    {
        try
        {
            Check(DCompositionCreateDevice(dxgiDevice, IID_IDCompositionDevice, out _device), "DCompositionCreateDevice");
            nint target, visual;
            // IDCompositionDevice: 3 Commit, 6 CreateTargetForHwnd, 7 CreateVisual.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(_device, 6))(_device, hwnd, 1, &target), "CreateTargetForHwnd");
            _target = target;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_device, 7))(_device, &visual), "CreateVisual");
            _visual = visual;
            // IDCompositionVisual: 15 SetContent (after the overloaded offset, transform and clip setters).
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_visual, 15))(_visual, swapChain), "SetContent");
            // IDCompositionTarget: 3 SetRoot.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_target, 3))(_target, _visual), "SetRoot");
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_device, 3))(_device), "Commit");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Release(ref _visual);
        Release(ref _target);
        Release(ref _device);
    }

    private static void* Slot(nint com, int index) => (*(void***)com)[index];

    private static void Release(ref nint com)
    {
        if (com == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com);
        com = 0;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8}).");
    }

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionCreateDevice(nint dxgiDevice, in Guid iid, out nint device);
}
