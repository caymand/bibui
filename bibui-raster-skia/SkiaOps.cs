using System.Numerics;
using System.Runtime.InteropServices;
using Bibui.Core;
using Bibui.Core.Draw;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>
/// What both Skia renderers draw with: a <see cref="DrawList"/> onto an
/// <see cref="SKSurface"/>, op by op, and what that keeps — paints by content,
/// text paints and fonts per face and size, a text blob per glyph run, a typeface
/// per bibui face, a native path per kept <see cref="VectorPath"/>, each
/// touch-evicted when frames stop
/// drawing it — and the renderer's textures by handle, which the renderer that
/// owns this makes. Not thread-safe: one thread draws at a time.
/// </summary>
internal sealed class SkiaOps : IDisposable
{
    /// <summary>The owning renderer's textures: it makes and frees them; a texture op draws one by handle.</summary>
    public readonly TextureTable<SkiaTexture> Textures = new();

    // A paint owns the Skia children it was built with (shader, filters, dash),
    // so a child can never be evicted from under a live paint: they go together.
    private readonly ResourceCache<CacheKey, SkiaPaintEntry> _paints = new();
    private readonly ResourceCache<TextPaintKey, SKPaint> _textPaints = new();
    // The font each text paint draws a text blob with.
    private readonly ResourceCache<TextPaintKey, SKFont> _textFonts = new();
    // The native path each kept VectorPath was built into, by its Id.
    private readonly ResourceCache<int, SKPath> _paths = new();
    // A path the list carries in its payload, by its content: a callback that
    // draws the same path every frame builds it once, as a kept path is.
    private readonly ResourceCache<UInt128, SKPath> _payloadPaths = new();
    // What every native path is built with; Skia's paths are made, not edited.
    private readonly SKPathBuilder _builder = new();
    // What every glyph run's blob is built with: Build() leaves it empty again.
    private readonly SKTextBlobBuilder _blobBuilder = new();

    // Each run's blob, kept across frames under a 64-bit hash of what it holds
    // (face, size, glyph count, glyphs), so a run drawn again reuses
    // the blob, and Skia its own work for it: Skia keeps a blob's glyph lookups
    // and, on the GPU, its vertex data under the blob's identity, so a new blob
    // per run per frame was work redone every frame. Positions are from the
    // run's start and colour is the paint's, so one blob draws anywhere in any
    // colour. Touch-evicted.
    private readonly ResourceCache<ulong, SKTextBlob> _blobs = new();
    private readonly System.IO.Hashing.XxHash3 _runHash = new();

    // Skia's face for each of bibui's, made from its font file once: the glyph
    // ids FreeType resolves belong to that file, so Skia draws the same glyphs.
    // Kept for the renderer's life, like the FontFaces they mirror.
    private readonly Dictionary<int, SKTypeface> _typefaces = [];

    /// <summary>
    /// Draws <paramref name="list"/> onto <paramref name="surface"/>, then ages
    /// what this renderer caches: once per frame. The caller flushes a GPU
    /// surface and presents it.
    /// </summary>
    public void Draw(DrawList list, SKSurface surface)
    {
        var canvas = surface.Canvas;
        foreach (ref readonly var op in list.Ops)
        {
            switch (op.Kind)
            {
                case DrawOpKind.Clear: canvas.Clear(op.Color.ToSk()); break;
                case DrawOpKind.PushClip: canvas.Save(); canvas.ClipRect(op.Rect.ToSk()); break;
                case DrawOpKind.PushTransform: canvas.Save(); Transform(canvas, op.Matrix); break;
                case DrawOpKind.PushBlendMode:
                    // SrcOver composites as if there were no layer: none is made.
                    if (op.Blend == BlendMode.SrcOver) canvas.Save();
                    else canvas.SaveLayer(BlendPaint(op.Blend));
                    break;
                case DrawOpKind.PushColorMatrix:
                {
                    var m = list.Floats.Slice(op.Start, op.Count);
                    if (ColorMatrices.IsIdentity(m)) canvas.Save();
                    else if (ColorMatrices.IsOpacity(m, out byte alpha)) canvas.SaveLayer(Paint(new PaintSpec { Color = new Rgba(0, 0, 0, alpha) }));
                    else canvas.SaveLayer(MatrixPaint(m));
                    break;
                }
                case DrawOpKind.Pop: canvas.Restore(); break;
                case DrawOpKind.Rect: canvas.DrawRoundRect(op.Rect.ToSk(), op.A, op.B, Paint(op, list)); break;
                case DrawOpKind.Line: canvas.DrawLine(op.A, op.B, op.C, op.D, Paint(op, list)); break;
                case DrawOpKind.Path: canvas.DrawPath(Path(op, list), Paint(op, list)); break;
                case DrawOpKind.Points:
                    canvas.DrawPoints(op.Mode.ToSk(), list.Points.Slice(op.Start, op.Count).AsSk().ToArray(), Paint(op, list));
                    break;
                case DrawOpKind.Texture:
                    if (Textures.Get(op.Texture) is { } texture) Texture(canvas, texture, op, list);
                    break;
                case DrawOpKind.BackdropBlur: BackdropBlur(surface, op.Region, op.Rect, op.A); break;
                case DrawOpKind.Glyphs:
                    Glyphs(canvas, list.FaceAt(op.Resource), op.C, list.Glyphs.Slice(op.Start, op.Count),
                        list.Points.Slice(op.Start2, op.Count), op.A, op.B, op.Color);
                    break;
            }
        }

        _paints.EndFrame();
        _textPaints.EndFrame();
        _textFonts.EndFrame();
        _blobs.EndFrame();
        _paths.EndFrame();
        _payloadPaths.EndFrame();
        _lookupShaders.EndFrame();
        _matrixPaints.EndFrame();
    }

    // A translation as a translation, a uniform scale after it as a scale, and
    // anything else as the whole matrix before the canvas's own: the cheapest
    // call that draws it, and the calls the frame was drawn with before it was
    // recorded.
    private static void Transform(SKCanvas canvas, in Matrix3x2 m)
    {
        if (m.M12 != 0f || m.M21 != 0f || m.M11 != m.M22) canvas.SetMatrix((m * canvas.TotalMatrix.ToMatrix3x2()).ToSk());
        else
        {
            canvas.Translate(m.M31, m.M32);
            if (m.M11 != 1f) canvas.Scale(m.M11, m.M11);
        }
    }

    // A texture into its rect. The paint's alpha modulates it; an A8 one is
    // drawn in white, its default look, whatever colour the paint has.
    private void Texture(SKCanvas canvas, SkiaTexture texture, in DrawOp op, DrawList list)
    {
        var lookup = list.LookupAt(op.Resource);
        if (!lookup.IsNone)
        {
            Lookup(canvas, texture, lookup, op);
            return;
        }
        var paint = texture.Format == TextureFormat.A8
            ? Paint(op.Paint with { Color = new Rgba(255, 255, 255, op.Paint.Color.A) }, op.MaskBlurSigma, op.ImageBlurSigma)
            : Paint(op, list);
        canvas.DrawImage(texture.Image, op.Rect.ToSk(), op.Paint.FilterQuality.ToSk(), paint);
    }

    // ── Groups ───────────────────────────────────────────────────────────────
    // A layer's paint per blend mode, and per colour matrix by content: made
    // once, so pushing a group allocates nothing frame to frame.

    private readonly SKPaint?[] _blendPaints = new SKPaint?[6];
    private readonly ResourceCache<UInt128, SKPaint> _matrixPaints = new();

    private SKPaint BlendPaint(BlendMode mode) => _blendPaints[(int)mode] ??= new SKPaint
    {
        BlendMode = mode switch
        {
            BlendMode.Multiply => SKBlendMode.Multiply,
            BlendMode.Plus => SKBlendMode.Plus,
            BlendMode.Screen => SKBlendMode.Screen,
            BlendMode.Overlay => SKBlendMode.Overlay,
            BlendMode.Src => SKBlendMode.Src,
            _ => SKBlendMode.SrcOver,
        },
    };

    private SKPaint MatrixPaint(ReadOnlySpan<float> m)
    {
        var key = System.IO.Hashing.XxHash128.HashToUInt128(MemoryMarshal.AsBytes(m));
        if (_matrixPaints.TryGet(key, out var hit)) return hit;
        // The paint holds its own reference to the filter.
        using var filter = SKColorFilter.CreateColorMatrix(m.ToArray());
        return _matrixPaints.Add(key, new SKPaint { ColorFilter = filter });
    }

    // ── Lookups ──────────────────────────────────────────────────────────────
    // A texture drawn through a table: one runtime effect samples the source
    // with the op's filter, takes its raw channel(s) and reads that entry of the
    // table exactly (floor to an entry, sampled at its centre). The same SkSL
    // runs on the GPU and on the CPU.
    //
    // Nothing is made per draw once a picture holds still: the effect, its
    // uniforms and children blocks and the paint are this renderer's for life,
    // and the shader is kept by what it draws (source and table images, rect,
    // filter, channels). A Stream on the GPU keeps its image, so its shader is
    // kept across frames too; a CPU Stream's image is made again per update.

    private const string LookupSksl = """
        uniform shader src;
        uniform shader lut;
        uniform float4 rawMask;   // what the format's sample holds raw: Gray8 samples (g, g, g, 1), R8's G and B are 0
        uniform float straight;   // RGBA8/BGRA8: Skia samples them premultiplied; raw is straight
        uniform float4 pickX;     // the channel that picks the column, one-hot
        uniform float4 pickY;     // the channel that picks the row, one-hot; zero for a 1-D table
        half4 main(float2 p) {
            float4 s = src.eval(p);
            if (straight > 0.5 && s.a > 0.0) { s.rgb = s.rgb / s.a; }
            s = s * rawMask;
            float x = floor(dot(s, pickX) * 255.0 + 0.5) + 0.5;
            float y = floor(dot(s, pickY) * 255.0 + 0.5) + 0.5;
            return lut.eval(float2(x, y));
        }
        """;

    private SKRuntimeEffect? _lookupEffect;
    private SKRuntimeEffectUniforms? _lookupUniforms;
    private SKRuntimeEffectChildren? _lookupChildren;
    private readonly SKPaint _lookupPaint = new() { Color = SKColors.White };
    private readonly ResourceCache<UInt128, SKShader> _lookupShaders = new();

    private static readonly float[] AllRaw = [1f, 1f, 1f, 1f], R8Raw = [1f, 0f, 0f, 1f];
    private static readonly float[] PickR = [1f, 0f, 0f, 0f], PickG = [0f, 1f, 0f, 0f],
        PickB = [0f, 0f, 1f, 0f], PickA = [0f, 0f, 0f, 1f], PickNone = [0f, 0f, 0f, 0f];

    private void Lookup(SKCanvas canvas, SkiaTexture texture, TextureLookup lookup, in DrawOp op)
    {
        // A released table draws nothing, as a released texture does.
        if (Textures.Get(lookup.Table) is not { } table) return;
        var rect = op.Rect;
        var filter = op.Paint.FilterQuality;

        Span<long> parts =
        [
            texture.Image!.Handle, table.Image!.Handle,
            BitConverter.SingleToInt32Bits(rect.Left), BitConverter.SingleToInt32Bits(rect.Top),
            BitConverter.SingleToInt32Bits(rect.Right), BitConverter.SingleToInt32Bits(rect.Bottom),
            (long)filter, (long)lookup.X << 8 | (long)lookup.Y,
        ];
        var key = System.IO.Hashing.XxHash128.HashToUInt128(MemoryMarshal.AsBytes(parts));
        if (!_lookupShaders.TryGet(key, out var shader))
            shader = _lookupShaders.Add(key, LookupShader(texture, table, lookup, rect, filter));

        _lookupPaint.Shader = shader;
        _lookupPaint.Color = new SKColor(255, 255, 255, op.Paint.Color.A);   // the paint's alpha modulates, as for any texture
        _lookupPaint.IsAntialias = op.Paint.IsAntialias;
        canvas.DrawRect(rect.ToSk(), _lookupPaint);
    }

    private SKShader LookupShader(SkiaTexture texture, SkiaTexture table, TextureLookup lookup, RectF rect, FilterQuality filter)
    {
        _lookupEffect ??= SKRuntimeEffect.CreateShader(LookupSksl, out var errors)
            ?? throw new InvalidOperationException($"Skia did not compile the lookup shader: {errors}");
        _lookupUniforms ??= new SKRuntimeEffectUniforms(_lookupEffect);
        _lookupChildren ??= new SKRuntimeEffectChildren(_lookupEffect);

        var toRect = SKMatrix.CreateScale(rect.Width / texture.Width, rect.Height / texture.Height)
            .PostConcat(SKMatrix.CreateTranslation(rect.Left, rect.Top));
        // The children are only lent: the effect's shader holds its own references.
        using var src = texture.Image!.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, filter.ToSk(), toRect);
        using var lut = table.Image!.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, FilterQuality.None.ToSk());
        _lookupUniforms["rawMask"] = texture.Format == TextureFormat.R8 ? R8Raw : AllRaw;
        _lookupUniforms["straight"] = texture.Format is TextureFormat.RGBA8 or TextureFormat.BGRA8 ? 1f : 0f;
        _lookupUniforms["pickX"] = Pick(lookup.X);
        _lookupUniforms["pickY"] = Pick(lookup.Y);
        _lookupChildren["src"] = src;
        _lookupChildren["lut"] = lut;
        var shader = _lookupEffect.ToShader(_lookupUniforms, _lookupChildren);
        _lookupChildren["src"] = null;
        _lookupChildren["lut"] = null;
        return shader;
    }

    // One-hot over (r, g, b, a); None is all zero, so a 1-D table reads row 0.
    private static float[] Pick(TextureChannel channel) => channel switch
    {
        TextureChannel.R => PickR,
        TextureChannel.G => PickG,
        TextureChannel.B => PickB,
        TextureChannel.A => PickA,
        _ => PickNone,
    };

    // Snapshots region of what the surface holds, blurs it and draws it back
    // clipped: frosted glass. Device pixels; the transform is not applied to
    // the snapshot.
    private void BackdropBlur(SKSurface surface, PixelRect region, RectF clip, float sigma)
    {
        using var snapshot = surface.Snapshot(region.ToSk());
        if (snapshot is null) return;

        // Opaque black, not default: DrawImage modulates by the paint's alpha,
        // and default(Rgba) is transparent.
        var paint = Paint(new PaintSpec { Color = Rgba.Black, IsAntialias = true }, imageBlur: sigma);
        var canvas = surface.Canvas;
        canvas.Save();
        canvas.ClipRect(clip.ToSk());
        canvas.DrawImage(snapshot, new SKRect(region.Left, region.Top, region.Right, region.Bottom), FilterQuality.None.ToSk(), paint);
        canvas.Restore();
    }

    // There is one rendering mode: glyphs at subpixel positions, outlines
    // autohinted at full strength, grayscale antialiasing.
    private void Glyphs(SKCanvas canvas, FontFace face, float sizePx, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions,
        float x, float baselineY, Rgba color)
    {
        if (glyphs.IsEmpty) return;
        var sk = color.ToSk();
        var tf = Typeface(face);
        var key = TextPaintKey.For(sizePx, sk, face.Id);
        var paint = _textPaints.FromKey(key, sk, static c => new SKPaint { Color = c, IsAntialias = true });
        var font = _textFonts.FromKey(key, (Size: sizePx, tf), static p => new SKFont(p.tf, p.Size)
        {
            Subpixel = true,
            Edging = SKFontEdging.Antialias,   // grayscale, never LCD
            ForceAutoHinting = true,
            Hinting = SKFontHinting.Full,
        });
        ulong run = RunKey(face.Id, sizePx, glyphs);
        if (!_blobs.TryGet(run, out var blob))
        {
            _blobBuilder.AddPositionedRun(glyphs, font, positions.AsSk());
            blob = _blobBuilder.Build();
            if (blob is null) return;
            _blobs.Add(run, blob);
        }
        canvas.DrawText(blob, x, baselineY, paint);
    }

    // The positions are not in the key: a run's are its glyphs' advances summed
    // in order, so the face, the size and the glyphs decide them. That is the
    // core's promise, written where Glyphs ops are made (NOTE(positions) at
    // DrawContext.PushGlyphs, with an example); a run shaped by HarfBuzz breaks
    // it and would have to add its positions here. A collision would draw
    // another run's glyphs until it is evicted: 64 bits put that around n / 2^64
    // per new run with n kept, and the glyph count in the key rules out the
    // likeliest.
    private ulong RunKey(int faceId, float sizePx, ReadOnlySpan<ushort> glyphs)
    {
        Span<int> head = [faceId, BitConverter.SingleToInt32Bits(sizePx), glyphs.Length];
        _runHash.Reset();
        _runHash.Append(MemoryMarshal.AsBytes(head));
        _runHash.Append(MemoryMarshal.AsBytes(glyphs));
        return _runHash.GetCurrentHashAsUInt64();
    }

    private SKTypeface Typeface(FontFace face)
    {
        if (_typefaces.TryGetValue(face.Id, out var typeface)) return typeface;
        using var data = SKData.CreateCopy(face.FontFile);
        typeface = SKTypeface.FromData(data) ?? throw new InvalidOperationException($"Skia cannot open {face}.");
        _typefaces[face.Id] = typeface;
        return typeface;
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    // A kept path is built once and found by its Id; a payload path once per
    // content. Steady frames build none.
    private SKPath Path(in DrawOp op, DrawList list)
    {
        if (op.Resource >= 0)
        {
            var path = list.PathAt(op.Resource);
            return _paths.FromKey(path.Id, (path, _builder), static p => Build(p._builder, p.path.Verbs, p.path.Args, p.path));
        }
        var verbs = list.Verbs.Slice(op.Start, op.Count);
        var args = list.Floats.Slice(op.Start2, op.Count2);
        var hash = new System.IO.Hashing.XxHash128();
        hash.Append(MemoryMarshal.AsBytes(verbs));
        hash.Append(MemoryMarshal.AsBytes(args));
        var key = hash.GetCurrentHashAsUInt128();
        if (_payloadPaths.TryGet(key, out var hit)) return hit;
        return _payloadPaths.Add(key, Build(_builder, verbs, args, null));
    }

    // Verb for verb into a native path, through a builder left empty for the
    // next. An added path is built with its own builder first, then mapped through
    // its transform.
    private static SKPath Build(SKPathBuilder b, ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> a, VectorPath? owner)
    {
        b.Reset();
        bool empty = true;
        int at = 0, added = 0;
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.Move: b.MoveTo(a[at], a[at + 1]); break;
                case PathVerb.Line: b.LineTo(a[at], a[at + 1]); break;
                case PathVerb.Quad: b.QuadTo(a[at], a[at + 1], a[at + 2], a[at + 3]); break;
                case PathVerb.Cubic: b.CubicTo(a[at], a[at + 1], a[at + 2], a[at + 3], a[at + 4], a[at + 5]); break;
                case PathVerb.Arc:
                    b.ArcTo(a[at], a[at + 1], a[at + 2],
                        a[at + 3] != 0f ? SKPathArcSize.Large : SKPathArcSize.Small,
                        a[at + 4] != 0f ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
                        a[at + 5], a[at + 6]);
                    break;
                case PathVerb.AddArc: b.AddArc(new SKRect(a[at], a[at + 1], a[at + 2], a[at + 3]), a[at + 4], a[at + 5]); break;
                case PathVerb.Close: b.Close(); break;
                case PathVerb.AddPath:
                {
                    var (source, transform) = owner!.Added(added++);
                    using var inner = new SKPathBuilder();
                    using var built = Build(inner, source.Verbs, source.Args, source);
                    var m = transform.ToSk();
                    // Into an empty path, the mapped copy is written outright: the
                    // operation icons were scaled with when their pixels were
                    // blessed, so their geometry stays bit-identical.
                    if (empty) { built.Transform(in m); b.AddPath(built); }
                    else b.AddPath(built, in m);
                    break;
                }
            }
            empty = false;
            at += VectorPath.ArgCount(verb);
        }
        return b.Detach();
    }

    // ── Paints ───────────────────────────────────────────────────────────────

    // The op's paint: its spec, the effects beside it, and its gradient, whose
    // colours and stops are in the payload.
    private SKPaint Paint(in DrawOp op, DrawList list)
    {
        DashPattern? dash = op.Dashed ? op.Dash : null;
        if (!op.Shading.Present) return Paint(op.Paint, op.MaskBlurSigma, op.ImageBlurSigma, dash);
        var s = op.Shading;
        var colors = list.Colors.Slice(s.ColorStart, s.ColorCount);
        var stops = list.Floats.Slice(s.StopStart, s.StopCount);
        var key = Key(op.Paint, op.MaskBlurSigma, op.ImageBlurSigma, dash,
            CacheKey.OfGradient(s.Type == GradientType.Radial ? 1 : 0, s.Start, s.End, s.Radius, colors, stops));
        if (_paints.TryGet(key, out var hit)) return hit.Paint;
        return _paints.Add(key, SkiaPaintEntry.Build(op.Paint, Gradient(s, colors, stops), op.MaskBlurSigma, op.ImageBlurSigma, dash)).Paint;
    }

    /// <summary>
    /// The SKPaint for a spec and its effects, built on the first ask for its
    /// content and reused after. Every field that changes the object is in the key.
    /// </summary>
    private SKPaint Paint(in PaintSpec spec, float maskBlur = 0f, float imageBlur = 0f, DashPattern? dash = null)
        => _paints.FromKey(Key(spec, maskBlur, imageBlur, dash, default), (spec, maskBlur, imageBlur, dash),
            static p => SkiaPaintEntry.Build(p.spec, null, p.maskBlur, p.imageBlur, p.dash)).Paint;

    // Each child gets its own sub-key, so a mask blur and an image blur of the
    // same sigma — or a dash and no dash — never share a paint.
    private static CacheKey Key(in PaintSpec spec, float maskBlur, float imageBlur, DashPattern? dash, CacheKey shader)
    {
        CacheKey mask = maskBlur > 0f ? CacheKey.Of([CacheKey.Bits(maskBlur), (int)SKBlurStyle.Normal]) : default;
        CacheKey image = imageBlur > 0f ? CacheKey.Of([CacheKey.Bits(imageBlur), 1]) : default;
        CacheKey effect = dash is { } d ? CacheKey.Of([CacheKey.Bits(d.On), CacheKey.Bits(d.Off), CacheKey.Bits(d.Phase)]) : default;
        return CacheKey.OfSpec(spec, shader, mask, image, effect);
    }

    // A gradient as Skia takes it. Linear passes no stops as none (evenly
    // spread); radial passes its stops as they are, empty included: the calls
    // the pixels are pinned to.
    private static SKShader Gradient(in Shading s, ReadOnlySpan<Rgba> colors, ReadOnlySpan<float> stops)
    {
        var sk = colors.AsSk().ToArray();
        return s.Type == GradientType.Radial
            ? SKShader.CreateRadialGradient(s.Start.ToSk(), s.Radius, sk, stops.ToArray(), SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(s.Start.ToSk(), s.End.ToSk(), sk, stops.IsEmpty ? null : stops.ToArray(), SKShaderTileMode.Clamp);
    }

    /// <summary>
    /// How Skia reads a texture format. Gray8 and Rg88 sample as (g, g, g, 1) and
    /// (r, g, 0, 1) already: the formats' default looks. Alpha8 takes its colour
    /// from the paint, which a texture op makes white.
    /// </summary>
    public static SKImageInfo Info(TextureFormat format, int width, int height) => format switch
    {
        TextureFormat.R8 => new(width, height, SKColorType.Gray8, SKAlphaType.Opaque),
        TextureFormat.RG8 => new(width, height, SKColorType.Rg88, SKAlphaType.Opaque),
        TextureFormat.A8 => new(width, height, SKColorType.Alpha8, SKAlphaType.Premul),
        TextureFormat.BGRA8 => new(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
        _ => new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul),
    };

    /// <summary>The caches and faces. The textures are the owning renderer's to free.</summary>
    public void Dispose()
    {
        _paints.Dispose();
        _textPaints.Dispose();
        _textFonts.Dispose();
        _paths.Dispose();
        _payloadPaths.Dispose();
        _builder.Dispose();
        _blobBuilder.Dispose();
        _blobs.Dispose();
        _lookupShaders.Dispose();
        _matrixPaints.Dispose();
        foreach (var paint in _blendPaints) paint?.Dispose();
        _lookupPaint.Dispose();
        _lookupUniforms?.Dispose();
        _lookupChildren?.Dispose();
        _lookupEffect?.Dispose();
        foreach (var typeface in _typefaces.Values) typeface.Dispose();
        _typefaces.Clear();
    }
}

/// <summary>
/// One texture, as a Skia renderer keeps it: the image a texture op draws, and
/// what is behind it. On GL (<see cref="SkiaGlRenderer"/>): a GL texture the
/// renderer made, which the image wraps for its life. On the CPU
/// (<see cref="SkiaCpuRenderer"/>): an image over a copy of the pixels (Static),
/// or over a pinned buffer the renderer writes into and wraps again (Stream).
/// Draw thread only.
/// </summary>
internal sealed class SkiaTexture
{
    public TextureFormat Format;
    public TextureKind Kind;
    public int Width, Height;
    public SKImage? Image;
    public uint GlTexture;
    public nint MtlTexture;
    public byte[]? Pixels;
}

/// <summary>A paint and the Skia children it carries, disposed together.</summary>
internal sealed class SkiaPaintEntry : IDisposable
{
    public required SKPaint Paint { get; init; }
    private SKShader? _shader;
    private SKMaskFilter? _mask;
    private SKImageFilter? _image;
    private SKPathEffect? _dash;

    public static SkiaPaintEntry Build(in PaintSpec spec, SKShader? shader, float maskBlur, float imageBlur, DashPattern? dash)
    {
        var mask = maskBlur > 0f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, maskBlur) : null;
        var image = imageBlur > 0f ? SKImageFilter.CreateBlur(imageBlur, imageBlur) : null;
        var effect = dash is { } d ? SKPathEffect.CreateDash([d.On, d.Off], d.Phase) : null;
        return new SkiaPaintEntry
        {
            Paint = new SKPaint
            {
                Color = spec.Color.ToSk(),
                Style = spec.Style.ToSk(),
                StrokeWidth = spec.StrokeWidth,
                StrokeCap = spec.StrokeCap.ToSk(),
                StrokeJoin = spec.StrokeJoin.ToSk(),
                IsAntialias = spec.IsAntialias,
                Shader = shader,
                MaskFilter = mask,
                ImageFilter = image,
                PathEffect = effect,
            },
            _shader = shader,
            _mask = mask,
            _image = image,
            _dash = effect,
        };
    }

    public void Dispose()
    {
        Paint.Dispose();
        _shader?.Dispose();
        _mask?.Dispose();
        _image?.Dispose();
        _dash?.Dispose();
    }
}

/// <summary>
/// Text paint key: size, colour and the face the run resolved to. Keyed by the
/// face's id rather than the (family, weight) asked for, because a run is one
/// face, whatever was asked for.
/// </summary>
internal readonly record struct TextPaintKey(UInt128 Hash)
{
    public static TextPaintKey For(float sizePhysical, SKColor color, int typefaceId)
    {
        Span<int> parts =
        [
            BitConverter.SingleToInt32Bits(sizePhysical),
            (int)(uint)color,
            typefaceId,
        ];
        return new(System.IO.Hashing.XxHash128.HashToUInt128(MemoryMarshal.AsBytes(parts)));
    }
}
