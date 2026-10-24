using Bibui.Core.Draw;
using Bibui.Metal;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A Silk window on macOS, drawn by Skia through Metal: everything on the GPU,
// and no GL. The window opens with no graphics API; the Metal layer goes on it.
public sealed partial class BibuiApp
{
    // Test-server screenshots: Metal draws into the window, not into memory, so a
    // CPU renderer of its own draws them. Textures made on Renderer are not in it.
    private SkiaCpuRenderer? _screenshots;

    // The window's Metal layer, which Renderer draws into.
    private MetalLayer _layer = null!;

    private static partial SilkWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options)
    {
        var opened = SilkWindow.Open(SilkWindow.DefaultOptions(window));
        runtime.WakeUp = opened.Wake;
        runtime.WindowClipboard = (opened.GetClipboardText, opened.SetClipboardText);
        return opened;
    }

    private partial SkiaMetalRenderer MakeRenderer() => new(_layer = MetalLayer.Attach(Window.CocoaHandle));

    public partial void Present()
    {
        // No drawable (a window the system is not showing): this frame is not drawn.
        if (_layer.BeginDraw(Window.FramebufferWidth, Window.FramebufferHeight, Window.Scale))
            Renderer.Draw(Runtime.DrawList);
        _layer.EndDraw();
    }

    private partial DrawToPixels ScreenshotDraw() => (_screenshots ??= new SkiaCpuRenderer()).Draw;

    private partial void DisposeRenderer()
    {
        // The window goes next: nothing may wake it after.
        if (Runtime.WakeUp == Window.Wake) Runtime.WakeUp = null;
        Renderer.Dispose();
        _layer.Dispose();
        _screenshots?.Dispose();
    }
}
