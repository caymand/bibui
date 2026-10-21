using System.Runtime.InteropServices;

namespace Bibui.Core.FreeType.Native;

/// <summary>
/// The slice of HarfBuzz the engine needs, from the libharfbuzz bibui ships
/// beside libfreetype (see <see cref="FT"/>). Shaping uses HarfBuzz's own
/// OpenType font functions (not hb-ft) at a 16.16 scale, so advances are the
/// unhinted design advances at full precision: the numbers text measures with.
/// </summary>
internal static unsafe partial class HB
{
    private const string Lib = "harfbuzz";

    public const int MEMORY_MODE_READONLY = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphInfo { public uint Codepoint, Mask, Cluster, Var1, Var2; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphPosition { public int XAdvance, YAdvance, XOffset, YOffset, Var; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Feature { public uint Tag, Value, Start, End; }

    [LibraryImport(Lib)] public static partial IntPtr hb_blob_create(byte* data, uint length, int mode, IntPtr userData, IntPtr destroy);
    [LibraryImport(Lib)] public static partial void hb_blob_destroy(IntPtr blob);
    [LibraryImport(Lib)] public static partial IntPtr hb_face_create(IntPtr blob, uint index);
    [LibraryImport(Lib)] public static partial void hb_face_destroy(IntPtr face);
    [LibraryImport(Lib)] public static partial IntPtr hb_font_create(IntPtr face);
    [LibraryImport(Lib)] public static partial void hb_font_set_scale(IntPtr font, int xScale, int yScale);
    [LibraryImport(Lib)] public static partial void hb_font_destroy(IntPtr font);
    [LibraryImport(Lib)] public static partial IntPtr hb_buffer_create();
    [LibraryImport(Lib)] public static partial void hb_buffer_destroy(IntPtr buffer);
    [LibraryImport(Lib)] public static partial void hb_buffer_clear_contents(IntPtr buffer);
    [LibraryImport(Lib)] public static partial void hb_buffer_add_utf16(IntPtr buffer, char* text, int textLength, uint itemOffset, int itemLength);
    [LibraryImport(Lib)] public static partial void hb_buffer_guess_segment_properties(IntPtr buffer);
    [LibraryImport(Lib)] public static partial void hb_shape(IntPtr font, IntPtr buffer, Feature* features, uint numFeatures);
    [LibraryImport(Lib)] public static partial GlyphInfo* hb_buffer_get_glyph_infos(IntPtr buffer, out uint length);
    [LibraryImport(Lib)] public static partial GlyphPosition* hb_buffer_get_glyph_positions(IntPtr buffer, out uint length);
    [LibraryImport(Lib)] public static partial IntPtr hb_version_string();

    public static uint Tag(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);
}
