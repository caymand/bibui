using Bibui.Core.Draw;

namespace Bibui.Metal;

/// <summary>
/// Metal textures, made, filled and released: what a renderer on a
/// <see cref="MetalLayer"/>'s device makes its textures with, as
/// <c>GlTextures</c> is on GL. The renderer keeps the textures in its own table
/// and hands out handles; this knows nothing of handles.
/// </summary>
public static class MetalTextures
{
    /// <summary>A new <paramref name="width"/> × <paramref name="height"/> texture of <paramref name="format"/> on <paramref name="device"/>: its <c>id&lt;MTLTexture&gt;</c>, +1 retained. Its pixels are undefined until <see cref="Upload"/>.</summary>
    public static nint Create(nint device, TextureFormat format, int width, int height)
    {
        nint descriptor = ObjC.Send(ObjC.Class("MTLTextureDescriptor"), Sel.TextureDescriptor,
            PixelFormat(format), (nuint)width, (nuint)height, ObjC.No);
        nint texture = ObjC.Send(device, Sel.NewTextureWithDescriptor, descriptor);
        return texture != 0 ? texture
            : throw new InvalidOperationException($"Metal made no {width} × {height} {format} texture.");
    }

    /// <summary>Overwrites the whole texture with <paramref name="pixels"/>, rows <paramref name="rowBytes"/> apart.</summary>
    public static unsafe void Upload(nint texture, int width, int height, ReadOnlySpan<byte> pixels, int rowBytes)
    {
        fixed (byte* p = pixels)
            ObjC.Send(texture, Sel.ReplaceRegion, new MTLRegion((nuint)width, (nuint)height), 0, p, (nuint)rowBytes);
    }

    /// <summary>Releases a texture <see cref="Create"/> made.</summary>
    public static void Delete(nint texture) => ObjC.Release(texture);

    /// <summary>The <c>MTLPixelFormat</c> a texture of <paramref name="format"/> is stored as. A8 is one red channel, read as alpha by whoever samples it, as on GL.</summary>
    public static nuint PixelFormat(TextureFormat format) => format switch
    {
        TextureFormat.R8 or TextureFormat.A8 => 10,   // R8Unorm
        TextureFormat.RG8 => 30,                      // RG8Unorm
        TextureFormat.BGRA8 => 80,                    // BGRA8Unorm
        _ => 70,                                      // RGBA8Unorm
    };
}
