using System.Runtime.InteropServices;

namespace Bibui.Metal;

/// <summary>
/// The slice of the Objective-C runtime and Metal the layer and its textures
/// need, called directly. A message is <c>objc_msgSend</c> with the receiver,
/// the selector and the method's arguments as their C types, so each shape of
/// arguments is an overload of <see cref="Send(nint, nint)"/>. None returns a
/// struct, which on x86-64 would need <c>objc_msgSend_stret</c>.
/// </summary>
internal static partial class ObjC
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string Metal = "/System/Library/Frameworks/Metal.framework/Metal";

    /// <summary>The layer's class lives in QuartzCore, which must be loaded before it can be looked up.</summary>
    static ObjC() => NativeLibrary.TryLoad("/System/Library/Frameworks/QuartzCore.framework/QuartzCore", out _);

    [LibraryImport(Objc, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Class(string name);

    [LibraryImport(Objc, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Selector(string name);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")] public static partial nint Send(nint receiver, nint selector);
    [LibraryImport(Objc, EntryPoint = "objc_msgSend")] public static partial nint Send(nint receiver, nint selector, nint arg);
    [LibraryImport(Objc, EntryPoint = "objc_msgSend")] public static partial void Send(nint receiver, nint selector, nuint arg);
    [LibraryImport(Objc, EntryPoint = "objc_msgSend")] public static partial void Send(nint receiver, nint selector, byte arg);   // BOOL
    [LibraryImport(Objc, EntryPoint = "objc_msgSend")] public static partial void Send(nint receiver, nint selector, double arg); // CGFloat
    [LibraryImport(Objc, EntryPoint = "objc_msgSend")] public static partial void Send(nint receiver, nint selector, CGSize arg);
    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nuint a, nuint b, nuint c, byte d);
    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    public static unsafe partial void Send(nint receiver, nint selector, MTLRegion region, nuint level, byte* bytes, nuint rowBytes);

    [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPush")] public static partial nint AutoreleasePoolPush();
    [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPop")] public static partial void AutoreleasePoolPop(nint pool);

    [LibraryImport(Metal)] public static partial nint MTLCreateSystemDefaultDevice();

    public const byte Yes = 1, No = 0;

    /// <summary>Gives up a +1 reference: what <c>new…</c>, <c>alloc</c>/<c>init</c> and <c>retain</c> return.</summary>
    public static void Release(nint obj)
    {
        if (obj != 0) Send(obj, Sel.Release);
    }
}

/// <summary>The selectors sent, registered once.</summary>
internal static class Sel
{
    public static readonly nint Alloc = ObjC.Selector("alloc");
    public static readonly nint Init = ObjC.Selector("init");
    public static readonly nint Release = ObjC.Selector("release");
    public static readonly nint ContentView = ObjC.Selector("contentView");
    public static readonly nint SetLayer = ObjC.Selector("setLayer:");
    public static readonly nint SetWantsLayer = ObjC.Selector("setWantsLayer:");
    public static readonly nint NewCommandQueue = ObjC.Selector("newCommandQueue");
    public static readonly nint SetDevice = ObjC.Selector("setDevice:");
    public static readonly nint SetPixelFormat = ObjC.Selector("setPixelFormat:");
    public static readonly nint SetFramebufferOnly = ObjC.Selector("setFramebufferOnly:");
    public static readonly nint SetDisplaySyncEnabled = ObjC.Selector("setDisplaySyncEnabled:");
    public static readonly nint SetContentsScale = ObjC.Selector("setContentsScale:");
    public static readonly nint SetDrawableSize = ObjC.Selector("setDrawableSize:");
    public static readonly nint NextDrawable = ObjC.Selector("nextDrawable");
    public static readonly nint Texture = ObjC.Selector("texture");
    public static readonly nint CommandBuffer = ObjC.Selector("commandBuffer");
    public static readonly nint PresentDrawable = ObjC.Selector("presentDrawable:");
    public static readonly nint Commit = ObjC.Selector("commit");
    public static readonly nint TextureDescriptor = ObjC.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:");
    public static readonly nint NewTextureWithDescriptor = ObjC.Selector("newTextureWithDescriptor:");
    public static readonly nint ReplaceRegion = ObjC.Selector("replaceRegion:mipmapLevel:withBytes:bytesPerRow:");
}

/// <summary>CoreGraphics' size: two CGFloats, doubles on 64-bit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGSize(double width, double height)
{
    public double Width = width, Height = height;
}

/// <summary>Metal's region: an origin and a size, three NSUIntegers each.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MTLRegion(nuint width, nuint height)
{
    public nuint X, Y, Z;
    public nuint Width = width, Height = height, Depth = 1;
}
