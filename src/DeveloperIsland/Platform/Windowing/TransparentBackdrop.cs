using System.Runtime.InteropServices;
using DeveloperIsland.Platform.Native;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// A system backdrop that paints nothing, so the window is per-pixel transparent.
/// WinUI hosts its content in a DirectComposition tree, and the backdrop brush is the bottom-most
/// layer; a fully transparent colour brush therefore lets the desktop show through wherever the
/// XAML content itself is transparent.
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? s_compositor;
    private static IntPtr s_dispatcherQueueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        DeveloperIsland.Core.Diagnostics.Log.Debug("window", "Transparent backdrop connected");
        EnsureSystemDispatcherQueue();
        s_compositor ??= new Windows.UI.Composition.Compositor();
        connectedTarget.SystemBackdrop = s_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }

    private static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || s_dispatcherQueueController != IntPtr.Zero)
        {
            return;
        }

        var options = new NativeMethods.DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<NativeMethods.DispatcherQueueOptions>(),
            threadType = 2,     // DQTYPE_THREAD_CURRENT
            apartmentType = 2,  // DQTAT_COM_STA
        };
        _ = NativeMethods.CreateDispatcherQueueController(options, out s_dispatcherQueueController);
    }
}
