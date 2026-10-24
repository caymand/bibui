using Bibui.Core;
using Bibui.Core.Draw;
using Bibui.Raster.Managed.Backend;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;

/// <summary>
/// Textures: pixels the gallery makes itself, drawn by bibui as a handle. bibui
/// has no image component; an app that shows an image knows which renderer it
/// chose, gets the pixels into memory its own way (decoded with
/// <c>Bibui.Core.Imaging.Png</c>, from a video decoder, or made, as here), and makes
/// a texture there. It owns the texture: this page keeps its three for the life
/// of the app. A Static texture is made once; a Stream one is one texture whose
/// pixels are overwritten, as a video's frames are. Any format draws as it is:
/// an R8 frame, one byte a pixel, draws grey.
/// </summary>
internal static class DemoTexture
{
    private const int Size = 32;

    // The renderer the gallery draws with: the window's (its Bibui.App package's,
    // chosen at build time), or Skia on the CPU headless. The one set is where
    // its textures live. An app with one renderer names it once instead
    // (global using Renderer = ...) and calls it directly.
    internal static SkiaGlRenderer? SkiaGl;
    internal static SkiaMetalRenderer? SkiaMetal;
    internal static SkiaCpuRenderer? SkiaCpu;
    internal static ManagedRenderer? Managed;

    private static TextureHandle s_still, s_moving, s_grey;
    private static readonly byte[] s_pixels = new byte[Size * Size * 4];
    private static readonly byte[] s_grey8 = new byte[Size * Size];
    private static float s_time;

    internal static void Build(BibuiRuntime runtime)
    {
        if (s_still.IsNone)
        {
            Checker(s_pixels);
            s_still = Create(TextureFormat.RGBA8, TextureKind.Static, s_pixels);
            s_moving = Create(TextureFormat.RGBA8, TextureKind.Stream, []);
            s_grey = Create(TextureFormat.R8, TextureKind.Stream, []);
        }

        // The same handle every frame, its pixels rewritten: a video frame's path.
        s_time += runtime.Dt;
        Sweep(s_pixels, s_time);
        Update(s_moving, s_pixels);
        // The same sweep's red, one byte a pixel: a NIR frame's shape.
        for (int i = 0; i < s_grey8.Length; i++) s_grey8[i] = s_pixels[i * 4];
        Update(s_grey, s_grey8);
        runtime.RequestFrame();

        Bold(runtime, "Made once, drawn scaled up", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(10f);
        Picture(runtime, s_still);
        runtime.Gap(22f);
        Bold(runtime, "One Stream texture, its pixels updated every frame", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(10f);
        Picture(runtime, s_moving);
        runtime.Gap(22f);
        Bold(runtime, "An R8 Stream texture: one byte a pixel, drawn grey", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(10f);
        Picture(runtime, s_grey);
    }

    // Nearest sampling: each texel stays a sharp square at 6x.
    private static void Picture(BibuiRuntime runtime, TextureHandle texture)
    {
        var picture = runtime.Box().Sized(Bibui.Core.Size.Px(Size * 6f), Bibui.Core.Size.Px(Size * 6f));
        picture.Draw = (_, ctx) => ctx.DrawTexture(texture, ctx.Rect,
            new Paint { Fill = Rgba.Black, Filter = FilterQuality.None, Antialias = false });
    }

    private static TextureHandle Create(TextureFormat format, TextureKind kind, byte[] pixels)
        => SkiaGl?.CreateTexture(format, kind, Size, Size, pixels)
           ?? SkiaMetal?.CreateTexture(format, kind, Size, Size, pixels)
           ?? SkiaCpu?.CreateTexture(format, kind, Size, Size, pixels)
           ?? Managed?.CreateTexture(format, kind, Size, Size, pixels) ?? TextureHandle.None;

    private static void Update(TextureHandle texture, byte[] pixels)
    {
        SkiaGl?.UpdateTexture(texture, pixels);
        SkiaMetal?.UpdateTexture(texture, pixels);
        SkiaCpu?.UpdateTexture(texture, pixels);
        Managed?.UpdateTexture(texture, pixels);
    }

    private static void Checker(byte[] px)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                bool dark = ((x / 4) + (y / 4)) % 2 == 0;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = dark ? ((byte)37, (byte)99, (byte)235, (byte)255) : ((byte)226, (byte)232, (byte)240, (byte)255);
            }
    }

    // A bright band sweeping across a dark field.
    private static void Sweep(byte[] px, float time)
    {
        float band = (time * 12f + Size / 2f) % (Size + 16) - 4;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                float glow = MathF.Max(0f, 1f - MathF.Abs(x + y * 0.5f - band) / 4f);
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = ((byte)(20 + 200 * glow), (byte)(24 + 150 * glow), (byte)(48 + 60 * glow), (byte)255);
            }
    }
}
