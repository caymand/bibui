using System.Runtime.InteropServices;

namespace Bibui.Core.FreeType.Native;

/// <summary>
/// The slice of FreeType the engine needs, from the libfreetype bibui ships
/// (libfreetype.so or libfreetype.dylib in bibui-core/native/&lt;rid&gt;).
/// Struct fields are read by offset rather than mirrored as C# structs, because only a handful are touched.
/// The offsets are LP64 (x86-64 and arm64 Linux and macOS), or ILP32 for wasm32, and were checked with offsetof
/// against the freetype2 2.14.3 headers; FT_FaceRec, FT_GlyphSlotRec and
/// FT_Bitmap are public structs whose layout FreeType 2.x keeps stable.
/// </summary>
internal static unsafe partial class FT
{
    private const string Lib = "freetype";

    public const int LOAD_NO_HINTING = 1 << 1;
    public const int LOAD_NO_BITMAP = 1 << 3;
    public const int LOAD_FORCE_AUTOHINT = 1 << 5;
    public const int LOAD_IGNORE_GLOBAL_ADVANCE_WIDTH = 1 << 9;
    public const int LOAD_TARGET_NORMAL = 0;
    public const int LOAD_TARGET_LIGHT = 1 << 16;

    public const int RENDER_MODE_NORMAL = 0;

    // Offsets for LP64 (x86-64, arm64) and ILP32 (wasm32, where FT_Long and
    // pointers are 4 bytes; checked with offsetof under Emscripten 3.1.56).
    private static readonly bool P64 = IntPtr.Size == 8;

    // FT_FaceRec
    public static readonly int Face_NumGlyphs = P64 ? 32 : 16;
    public static readonly int Face_UnitsPerEm = P64 ? 136 : 68;
    public static readonly int Face_Ascender = P64 ? 138 : 70;
    public static readonly int Face_Descender = P64 ? 140 : 72;
    public static readonly int Face_Height = P64 ? 142 : 74;
    public static readonly int Face_Glyph = P64 ? 152 : 84;

    // FT_GlyphSlotRec
    public static readonly int Slot_LinearHoriAdvance = P64 ? 112 : 56;
    public static readonly int Slot_Bitmap = P64 ? 152 : 76;
    public static readonly int Slot_BitmapLeft = P64 ? 192 : 100;
    public static readonly int Slot_BitmapTop = P64 ? 196 : 104;
    public static readonly int Slot_Outline = P64 ? 200 : 108;

    // FT_Bitmap
    public const int Bitmap_Rows = 0;
    public const int Bitmap_Width = 4;
    public const int Bitmap_Pitch = 8;
    public static readonly int Bitmap_Buffer = P64 ? 16 : 12;

    [LibraryImport(Lib)] public static partial int FT_Init_FreeType(out IntPtr library);
    [LibraryImport(Lib)] public static partial int FT_Done_FreeType(IntPtr library);
    [LibraryImport(Lib)] public static partial int FT_New_Memory_Face(IntPtr library, byte* data, nint size, nint faceIndex, out IntPtr face);
    [LibraryImport(Lib)] public static partial int FT_Done_Face(IntPtr face);
    [LibraryImport(Lib)] public static partial int FT_New_Size(IntPtr face, out IntPtr size);
    [LibraryImport(Lib)] public static partial int FT_Activate_Size(IntPtr size);
    [LibraryImport(Lib)] public static partial int FT_Set_Char_Size(IntPtr face, nint charWidth, nint charHeight, uint hres, uint vres);
    [LibraryImport(Lib)] public static partial uint FT_Get_Char_Index(IntPtr face, nuint charcode);
    [LibraryImport(Lib)] public static partial int FT_Load_Glyph(IntPtr face, uint glyphIndex, int loadFlags);
    [LibraryImport(Lib)] public static partial int FT_Get_Advance(IntPtr face, uint glyphIndex, int loadFlags, out nint advance);
    [LibraryImport(Lib)] public static partial int FT_Render_Glyph(IntPtr slot, int renderMode);
    [LibraryImport(Lib)] public static partial void FT_Outline_Translate(IntPtr outline, nint xOffset, nint yOffset);
    [LibraryImport(Lib)] public static partial void FT_Library_Version(IntPtr library, out int major, out int minor, out int patch);

    public static T Read<T>(IntPtr p, int offset) where T : unmanaged => *(T*)((byte*)p + offset);

    public static void Check(int err, string what)
    {
        if (err != 0) throw new InvalidOperationException($"{what} failed: FreeType error {err}");
    }
}
