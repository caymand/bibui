namespace Bibui.Metal;

/// <summary>
/// A <c>CAMetalLayer</c> on a macOS window, what a renderer on Metal draws into:
/// the Metal device and command queue, and, between <see cref="BeginDraw"/> and
/// <see cref="EndDraw"/>, the texture of the drawable this frame is drawn into.
/// What <c>GlTarget</c> is to a GL framebuffer. A platform's window gives its
/// <c>NSWindow</c> (<c>SilkWindow.CocoaHandle</c>); the layer goes on its content
/// view, so the window needs no graphics API of its own.
///
/// <para>On the thread that runs the window, one at a time. Dispose the renderer
/// on it first: its GPU context goes before the device does.</para>
/// </summary>
public sealed class MetalLayer : IDisposable
{
    // MTLPixelFormatBGRA8Unorm: what a layer shows without a conversion.
    private const nuint Bgra8Unorm = 80;

    /// <summary>The <c>id&lt;MTLDevice&gt;</c> everything is made on.</summary>
    public nint Device { get; }

    /// <summary>The <c>id&lt;MTLCommandQueue&gt;</c> a renderer submits on, and the drawable is presented on after it.</summary>
    public nint Queue { get; }

    /// <summary>The <c>CAMetalLayer</c>.</summary>
    public nint Layer { get; }

    /// <summary>The <c>id&lt;MTLTexture&gt;</c> this frame is drawn into, between <see cref="BeginDraw"/> and <see cref="EndDraw"/>; 0 when there is none to draw into.</summary>
    public nint Texture { get; private set; }

    /// <summary>The drawable's size in physical px.</summary>
    public int Width { get; private set; }

    /// <inheritdoc cref="Width"/>
    public int Height { get; private set; }

    private float _scale;
    private nint _drawable, _pool;

    private MetalLayer(nint device, nint queue, nint layer) => (Device, Queue, Layer) = (device, queue, layer);

    /// <summary>
    /// Puts a Metal layer on the content view of <paramref name="nsWindow"/>, on
    /// the system's default GPU. Throws where there is no Metal: off macOS, or
    /// without a GPU that has it.
    /// </summary>
    public static MetalLayer Attach(nint nsWindow)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Metal is macOS only.");
        if (nsWindow == 0) throw new ArgumentException("No NSWindow: open the window first.", nameof(nsWindow));
        nint device = ObjC.MTLCreateSystemDefaultDevice();
        if (device == 0) throw new InvalidOperationException("This Mac has no Metal device.");
        nint queue = ObjC.Send(device, Sel.NewCommandQueue);
        nint layer = ObjC.Send(ObjC.Send(ObjC.Class("CAMetalLayer"), Sel.Alloc), Sel.Init);
        ObjC.Send(layer, Sel.SetDevice, device);
        ObjC.Send(layer, Sel.SetPixelFormat, Bgra8Unorm);
        // A renderer may read back what it drew, not only write it.
        ObjC.Send(layer, Sel.SetFramebufferOnly, ObjC.No);
        // The runtime paces the loop, as on GL with a swap interval of 0: a
        // present that waited for the display as well would pace twice.
        ObjC.Send(layer, Sel.SetDisplaySyncEnabled, ObjC.No);
        // Layer-hosting: the view shows this layer and nothing it draws itself.
        nint view = ObjC.Send(nsWindow, Sel.ContentView);
        ObjC.Send(view, Sel.SetLayer, layer);
        ObjC.Send(view, Sel.SetWantsLayer, ObjC.Yes);
        return new MetalLayer(device, queue, layer);
    }

    /// <summary>
    /// Takes the next drawable at <paramref name="width"/> × <paramref name="height"/>
    /// physical px and <paramref name="scale"/> (the window's framebuffer), to draw
    /// this frame into: after the build, before the renderer draws. False, and no
    /// <see cref="Texture"/>, when the layer has none to give in time (a window
    /// the system is not showing): the frame is not drawn.
    /// </summary>
    public bool BeginDraw(int width, int height, float scale)
    {
        // Drawables and command buffers are autoreleased: a pool per frame frees
        // them, or the layer runs out of drawables after a few frames.
        _pool = ObjC.AutoreleasePoolPush();
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width != Width || height != Height || scale != _scale)
        {
            (Width, Height, _scale) = (width, height, scale);
            ObjC.Send(Layer, Sel.SetContentsScale, (double)scale);
            ObjC.Send(Layer, Sel.SetDrawableSize, new CGSize(width, height));
        }
        _drawable = ObjC.Send(Layer, Sel.NextDrawable);
        Texture = _drawable == 0 ? 0 : ObjC.Send(_drawable, Sel.Texture);
        return Texture != 0;
    }

    /// <summary>Shows what was drawn: presents the drawable on <see cref="Queue"/>, after the renderer's work there.</summary>
    public void EndDraw()
    {
        if (_drawable != 0)
        {
            nint commands = ObjC.Send(Queue, Sel.CommandBuffer);
            ObjC.Send(commands, Sel.PresentDrawable, _drawable);
            ObjC.Send(commands, Sel.Commit);
        }
        (_drawable, Texture) = (0, 0);
        if (_pool != 0) ObjC.AutoreleasePoolPop(_pool);
        _pool = 0;
    }

    /// <summary>Releases the layer, the queue and the device. The window keeps its view.</summary>
    public void Dispose()
    {
        ObjC.Release(Layer);
        ObjC.Release(Queue);
        ObjC.Release(Device);
    }
}
