using Bibui.Core.Draw;
using Bibui.Metal;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>
/// Draws a <see cref="DrawList"/> with Skia on the GPU through Metal, into a
/// macOS window's <see cref="MetalLayer"/>. Its textures are Metal textures on
/// the layer's device, made through <see cref="MetalTextures"/> and wrapped once
/// for Skia. <see cref="SkiaGlRenderer"/> is the same on a GL context.
///
/// <para>On the thread that runs the window, one at a time, between the layer's
/// <see cref="MetalLayer.BeginDraw"/> and <see cref="MetalLayer.EndDraw"/>.</para>
/// </summary>
public sealed class SkiaMetalRenderer : IDisposable
{
    /// <summary>A renderer on <paramref name="target"/>: Skia's GPU context is made on the layer's device and queue.</summary>
    public SkiaMetalRenderer(MetalLayer target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        using var backend = new GRMtlBackendContext { DeviceHandle = target.Device, QueueHandle = target.Queue };
        _gpu = GRContext.CreateMetal(backend) ?? throw new InvalidOperationException("Skia cannot draw with this Metal device.");
        // Bounded: a long-running window would otherwise grow toward Skia's
        // much larger default budget.
        _gpu.SetResourceCacheLimit(96 * 1024 * 1024);
    }

    private readonly MetalLayer _target;
    private readonly GRContext _gpu;
    private readonly SkiaOps _ops = new();

    /// <summary>Byte budget for Skia's GPU resource cache (glyph atlases, gradient ramps).</summary>
    public long GpuResourceCacheLimit
    {
        get => _gpu.GetResourceCacheLimit();
        set => _gpu.SetResourceCacheLimit(value);
    }

    /// <summary>
    /// Draws <paramref name="list"/> into this frame's drawable, then submits it on
    /// the layer's queue, ahead of the present that follows. Nothing when the layer
    /// gave no drawable this frame.
    /// </summary>
    public void Draw(DrawList list)
    {
        if (_target.Texture == 0) return;
        // A new drawable every frame: a surface over it is made each frame.
        using var drawable = new GRBackendRenderTarget(_target.Width, _target.Height, new GRMtlTextureInfo(_target.Texture));
        using var surface = SKSurface.Create(_gpu, drawable, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("Skia made no surface over the Metal drawable.");
        _ops.Draw(list, surface);
        surface.Canvas.Flush();
        _gpu.Flush(submit: true, synchronous: false);
    }

    // ── Textures ─────────────────────────────────────────────────────────────
    // Metal textures on the layer's device, made by usage code, which owns them:
    // a draw list only names a handle. Static and Stream are made alike; the
    // kind decides whether an update is taken.

    /// <summary>
    /// A <paramref name="width"/> × <paramref name="height"/> Metal texture of
    /// <paramref name="format"/>, from <paramref name="pixels"/> (rows
    /// <paramref name="rowBytes"/> apart, or packed tight when 0; RGBA8 is straight
    /// alpha), wrapped once for Skia. Empty pixels make a Stream texture of zeros.
    /// The caller owns it: <see cref="ReleaseTexture"/> frees it.
    /// </summary>
    public TextureHandle CreateTexture(TextureFormat format, TextureKind kind, int width, int height,
        ReadOnlySpan<byte> pixels, int rowBytes = 0)
    {
        bool blank = pixels.IsEmpty && kind == TextureKind.Stream;
        var texture = new SkiaTexture { Format = format, Kind = kind, Width = width, Height = height };
        texture.MtlTexture = MetalTextures.Create(_target.Device, format, width, height);
        // A new Metal texture holds whatever its memory held: zero it.
        if (blank)
            MetalTextures.Upload(texture.MtlTexture, width, height, new byte[width * height * format.BytesPerPixel()], width * format.BytesPerPixel());
        else
            MetalTextures.Upload(texture.MtlTexture, width, height, pixels, TexturePixels.RowBytes(format, width, height, pixels, rowBytes));
        var info = SkiaOps.Info(format, width, height);
        using var backend = new GRBackendTexture(width, height, false, new GRMtlTextureInfo(texture.MtlTexture));
        texture.Image = SKImage.FromTexture(_gpu, backend, GRSurfaceOrigin.TopLeft, info.ColorType, info.AlphaType)
            ?? throw new InvalidOperationException($"Skia took no {format} image over a Metal texture.");
        return _ops.Textures.Add(texture);
    }

    /// <summary>
    /// Overwrites a Stream texture's pixels, the whole texture, at the size and
    /// format it was made with: into the Metal texture Skia's image already wraps,
    /// no new image. A Static texture does not change: its update is ignored. So is
    /// a released handle.
    /// </summary>
    public void UpdateTexture(TextureHandle texture, ReadOnlySpan<byte> pixels, int rowBytes = 0)
    {
        if (_ops.Textures.Get(texture) is not { Kind: TextureKind.Stream } t) return;
        MetalTextures.Upload(t.MtlTexture, t.Width, t.Height, pixels, TexturePixels.RowBytes(t.Format, t.Width, t.Height, pixels, rowBytes));
        _ops.Textures.Touch(texture);
    }

    /// <summary>Frees <paramref name="texture"/>; drawing it afterwards draws nothing.</summary>
    public void ReleaseTexture(TextureHandle texture)
    {
        if (_ops.Textures.Remove(texture) is { } t) Free(t);
    }

    // The image first: it borrows the Metal texture, which goes after it.
    private static void Free(SkiaTexture t)
    {
        t.Image?.Dispose();
        MetalTextures.Delete(t.MtlTexture);
    }

    public void Dispose()
    {
        // Everything on the GPU context goes before the layer's device does:
        // dispose the renderer first.
        for (var node = _ops.Textures.FirstLive; node is not null; node = node.Next) Free(node.Item!);
        _ops.Dispose();
        _gpu.Dispose();
    }
}
