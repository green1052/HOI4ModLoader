using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HOI4ModLoader;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Signatures.Tests")]

namespace Fonts;

/// <summary>
/// The game's text in a TrueType/OpenType font of the player's choice (installed, or a .ttf/.otf/.ttc file): at startup every
/// bitmap font of the game's language (gfx\fonts\&lt;language&gt;\*.fnt with its .dds/.tga) is drawn anew with it, with the
/// same characters, line height and baseline, and an outline where the original has one; characters the font lacks keep the
/// original's glyph. The results are cached (HOI4ModLoader\cache\fonts) and put in front of the game's own files
/// (<see cref="GameFiles.Add"/>), so nothing is installed as a mod. Display only. Takes effect at the next start.
/// </summary>
[Plugin("com.green1052.fonts", "Fonts", "1.0.0")]
public sealed class Fonts : Plugin
{
    private const int Format = 1; // bump when the output changes: every cache is redrawn

    public override void Load()
    {
        var font = Config.Bind("Fonts", "Font", "",
            "An installed font's name (e.g. Malgun Gothic) or the path of a .ttf/.otf/.ttc file, for the game's text. Empty: the game's own fonts. Takes effect at the next start.").Trim();
        var header = Config.Bind("Fonts", "HeaderFont", "", "The same for titles (the fonts named *header*). Empty: Font.").Trim();
        var size = Math.Clamp(Config.Bind("Fonts", "SizePercent", 100,
            "Glyph size against the original font's line height (%): bigger or smaller text in the same lines."), 50, 150);
        var keep = Config.Bind("Fonts", "Keep", "typewriter",
            "Fonts that stay the game's own: comma-separated parts of their file names (e.g. typewriter,mapfont).").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (font == "") return;
        if (header == "") header = font;

        var gameDir = Path.GetDirectoryName(Loader.ModsDir)!;
        var dir = FontDir();
        var fnts = Directory.GetFiles(Path.Combine(gameDir, dir), "*.fnt")
            .Where(f => !keep.Any(k => Path.GetFileName(f).Contains(k, StringComparison.OrdinalIgnoreCase))).Order().ToList();
        var faces = new[] { font, header }.Distinct().ToDictionary(spec => spec, Face);
        foreach (var (spec, face) in faces)
        {
            using var gdi = new Gdi(face, 20, false);
            if (gdi.FamilyNames().Contains(face, StringComparer.OrdinalIgnoreCase)) continue;
            Log.Error($"font \"{spec}\" isn't installed (Windows would draw \"{gdi.Face}\" instead): the game's own fonts stay");
            return;
        }

        var key = new StringBuilder($"{Format}|{dir}|{size}|{font}|{header}");
        foreach (var f in fnts.Concat(faces.Keys.Where(File.Exists))) key.Append($"|{f}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f).Ticks}");
        var root = Path.Combine(Loader.ModsDir, "cache", "fonts");
        var cache = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..16]);
        if (!Directory.Exists(cache))
        {
            var clock = Stopwatch.StartNew();
            if (Directory.Exists(root)) // older settings' fonts
                foreach (var old in Directory.GetDirectories(root)) Directory.Delete(old, true);
            var building = cache + ".tmp";
            Directory.CreateDirectory(building);
            var drawn = 0;
            Parallel.ForEach(fnts, fnt =>
            {
                var name = Path.GetFileNameWithoutExtension(fnt);
                try
                {
                    if (Draw(fnt, faces[name.Contains("header", StringComparison.OrdinalIgnoreCase) ? header : font], size, building)) Interlocked.Increment(ref drawn);
                    else Log.Info($"{name}: its texture isn't one this can read, left as it is");
                }
                catch (Exception e) { Log.Error($"{name}: {e.Message}"); }
            });
            Directory.Move(building, cache);
            Log.Info($"drew {drawn} of {fnts.Count} fonts in {clock.ElapsedMilliseconds} ms (cached until the settings or the game's fonts change)");
        }
        foreach (var file in Directory.GetFiles(cache)) GameFiles.Add($"{dir}/{Path.GetFileName(file)}", file);
    }

    /// <summary>gfx/fonts/&lt;language&gt; for the game's language (pdx_settings.txt), else gfx/fonts.</summary>
    private static string FontDir()
    {
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paradox Interactive", "Hearts of Iron IV", "pdx_settings.txt");
        var language = File.Exists(settings)
            ? Regex.Match(File.ReadAllText(settings), "\"language\"\\s*=\\s*\\{\\s*value\\s*=\\s*\"(\\w+)\"").Groups[1].Value : "";
        return language switch
        {
            "l_korean" => "gfx/fonts/korean",
            "l_japanese" => "gfx/fonts/japanese",
            "l_simp_chinese" => "gfx/fonts/chinese",
            _ => "gfx/fonts",
        };
    }

    /// <summary>The GDI face name for a font setting: an installed font's name as is, a font file's family (loaded for this process only).</summary>
    private string Face(string spec)
    {
        if (!File.Exists(spec)) return spec;
        if (Gdi.AddFontResourceExW(spec, Gdi.FrPrivate, 0) == 0) throw new IOException($"can't load the font file {spec}");
        return FamilyName(File.ReadAllBytes(spec));
    }

    private static int U16(byte[] f, int at) => f[at] << 8 | f[at + 1];
    private static int U32(byte[] f, int at) => U16(f, at) << 16 | U16(f, at + 2);

    /// <summary>The family name in a .ttf/.otf file (or a .ttc's first font): English if it has one.</summary>
    internal static string FamilyName(byte[] f)
    {
        var font = Encoding.ASCII.GetString(f, 0, 4) == "ttcf" ? U32(f, 12) : 0;
        for (var i = 0; i < U16(f, font + 4); i++)
        {
            var record = font + 12 + i * 16;
            if (Encoding.ASCII.GetString(f, record, 4) == "name" && FamilyNames(f, U32(f, record + 8)) is [var name, ..]) return name;
        }
        throw new InvalidDataException("no family name in the font file");
    }

    /// <summary>The Windows family names in the 'name' table at <paramref name="table"/>, English first.</summary>
    internal static List<string> FamilyNames(byte[] f, int table)
    {
        var strings = table + U16(f, table + 4);
        var names = new List<string>();
        for (var j = 0; j < U16(f, table + 2); j++)
        {
            var r = table + 6 + j * 12;
            if (U16(f, r) != 3 || U16(f, r + 6) != 1) continue; // Windows platform, family name
            var name = Encoding.BigEndianUnicode.GetString(f, strings + U16(f, r + 10), U16(f, r + 8));
            if (U16(f, r + 4) == 0x409) names.Insert(0, name); // en-US
            else names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Draws <paramref name="fntPath"/>'s characters with <paramref name="face"/> into <paramref name="outDir"/> (a .fnt and its
    /// texture, same names). False: the original texture isn't uncompressed 32-bit, so its glyphs can't be kept for characters
    /// the face lacks; the font stays the game's.
    /// </summary>
    internal static bool Draw(string fntPath, string face, int sizePercent, string outDir)
    {
        var name = Path.GetFileNameWithoutExtension(fntPath);
        var lines = File.ReadAllLines(fntPath);
        var common = lines.First(l => l.StartsWith("common "));
        int Field(string line, string field) => int.Parse(Regex.Match(line, $@"\b{field}=(-?\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var lineHeight = Field(common, "lineHeight");
        var baseline = Field(common, "base");
        var textures = new[] { ".dds", ".tga" }.Select(e => Path.ChangeExtension(fntPath, e)).Where(File.Exists).ToList();
        var original = textures.Select(Bitmap.Read).FirstOrDefault(b => b != null);
        if (original == null) return false;

        // The original's look: dark ink (documents), white ink with a dark outline and shadow (most of the UI), or plain white.
        long bright = 0, dark = 0;
        for (var i = 0; i < original.Pixels.Length; i += 4)
            if (original.Pixels[i + 3] >= 128)
                if (original.Pixels[i] + original.Pixels[i + 1] + original.Pixels[i + 2] >= 384) bright++;
                else dark++;
        var darkInk = dark * 10 > (bright + dark) * 9;
        var outline = !darkInk && dark * 5 > bright + dark ? lineHeight >= 30 ? 2 : 1 : 0;
        var bold = Regex.IsMatch(name, @"_bold|\d+m?bs?$|header", RegexOptions.IgnoreCase); // b: bold, mbs: medium bold with a shadow

        var glyphs = new List<Glyph>();
        using (var gdi = new Gdi(face, lineHeight * sizePercent / 100, bold))
        {
            foreach (var line in lines.Where(l => l.StartsWith("char ")))
            {
                var id = Field(line, "id");
                var drawn = id <= 0xFFFF ? gdi.Draw((char)id, outline, baseline, darkInk) : null;
                glyphs.Add(drawn is { } g ? g with { Id = id } : original.Cut(id, Field(line, "x"), Field(line, "y"), Field(line, "width"), Field(line, "height"),
                    Field(line, "xoffset"), Field(line, "yoffset"), Field(line, "xadvance")));
            }
        }

        var atlas = Pack(glyphs, lineHeight > 48 ? 2048 : 1024);
        var fnt = new StringBuilder();
        foreach (var line in lines.TakeWhile(l => !l.StartsWith("chars ") && !l.StartsWith("char ")))
            fnt.AppendLine(line.StartsWith("common ")
                ? Regex.Replace(Regex.Replace(Regex.Replace(line, @"\bscaleW=\d+", $"scaleW={atlas.Width}"), @"\bscaleH=\d+", $"scaleH={atlas.Height}"), @"\bpages=\d+", "pages=1")
                : line);
        fnt.AppendLine($"chars count={glyphs.Count}");
        foreach (var g in glyphs)
            fnt.AppendLine($"char id={g.Id} x={g.X} y={g.Y} width={g.Width} height={g.Height} xoffset={g.XOffset} yoffset={g.YOffset} xadvance={g.XAdvance} page=0");
        File.WriteAllText(Path.Combine(outDir, name + ".fnt"), fnt.ToString(), new UTF8Encoding(false));
        foreach (var texture in textures) // both when the game has both: whichever it reads is ours
            File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(texture)),
                texture.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) ? atlas.Tga() : atlas.Dds());
        return true;
    }

    /// <summary>Shelves, tallest glyphs first, one pixel apart; the texture as tall as they need (a multiple of 4).</summary>
    private static Bitmap Pack(List<Glyph> glyphs, int width)
    {
        int x = 1, y = 1, shelf = 0;
        foreach (var g in glyphs.OrderByDescending(g => g.Height))
        {
            if (x + g.Width + 1 > width)
            {
                (x, y, shelf) = (1, y + shelf + 1, 0);
            }
            (g.X, g.Y) = (x, y);
            x += g.Width + 1;
            shelf = Math.Max(shelf, g.Height);
        }
        var atlas = new Bitmap(width, (y + shelf + 1 + 3) / 4 * 4);
        foreach (var g in glyphs)
            for (var row = 0; row < g.Height; row++)
                Array.Copy(g.Pixels, row * g.Width * 4, atlas.Pixels, ((g.Y + row) * width + g.X) * 4, g.Width * 4);
        return atlas;
    }
}

/// <summary>A glyph's pixels (BGRA, top row first) and BMFont metrics; X, Y: its place in the texture, set when packed.</summary>
internal sealed record Glyph(int Id, byte[] Pixels, int Width, int Height, int XOffset, int YOffset, int XAdvance)
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>A BGRA image, top row first: read from and written to uncompressed 32-bit DDS and TGA.</summary>
internal sealed class Bitmap(int width, int height)
{
    public readonly int Width = width, Height = height;
    public readonly byte[] Pixels = new byte[width * height * 4];

    /// <summary>The image in an uncompressed 32-bit DDS (A8R8G8B8, the game's own font format) or TGA; null for any other kind.</summary>
    public static Bitmap? Read(string path)
    {
        var f = File.ReadAllBytes(path);
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            if (f.Length < 128 || BitConverter.ToInt32(f, 0) != 0x20534444 || BitConverter.ToInt32(f, 80) != 0x41 || BitConverter.ToInt32(f, 88) != 32
                || BitConverter.ToUInt32(f, 92) != 0x00FF0000 || BitConverter.ToUInt32(f, 104) != 0xFF000000) return null;
            var dds = new Bitmap(BitConverter.ToInt32(f, 16), BitConverter.ToInt32(f, 12));
            if (f.Length < 128 + dds.Pixels.Length) return null;
            Array.Copy(f, 128, dds.Pixels, 0, dds.Pixels.Length);
            return dds;
        }
        if (f.Length < 18 || f[2] != 2 || f[16] != 32) return null; // uncompressed true colour, 32 bits
        var tga = new Bitmap(BitConverter.ToUInt16(f, 12), BitConverter.ToUInt16(f, 14));
        var start = 18 + f[0];
        var row = tga.Width * 4;
        if (f.Length < start + tga.Pixels.Length) return null;
        for (var y = 0; y < tga.Height; y++) // bottom row first unless the descriptor says top
            Array.Copy(f, start + ((f[17] & 0x20) != 0 ? y : tga.Height - 1 - y) * row, tga.Pixels, y * row, row);
        return tga;
    }

    /// <summary>The original's glyph, kept as it was.</summary>
    public Glyph Cut(int id, int x, int y, int w, int h, int xOffset, int yOffset, int xAdvance)
    {
        w = Math.Clamp(w, 0, Math.Max(0, Width - x));
        h = Math.Clamp(h, 0, Math.Max(0, Height - y));
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++) Array.Copy(Pixels, ((y + row) * Width + x) * 4, pixels, row * w * 4, w * 4);
        return new Glyph(id, pixels, w, h, xOffset, yOffset, xAdvance);
    }

    public byte[] Dds()
    {
        var f = new byte[128 + Pixels.Length];
        void Put(int at, uint value) => BitConverter.GetBytes(value).CopyTo(f, at);
        Put(0, 0x20534444);          // "DDS "
        Put(4, 124);
        Put(8, 0x100F);              // caps, height, width, pitch, pixel format
        Put(12, (uint)Height);
        Put(16, (uint)Width);
        Put(20, (uint)Width * 4);
        Put(76, 32);                 // pixel format: uncompressed, with alpha, A8R8G8B8
        Put(80, 0x41);
        Put(88, 32);
        Put(92, 0x00FF0000);
        Put(96, 0x0000FF00);
        Put(100, 0x000000FF);
        Put(104, 0xFF000000);
        Put(108, 0x1000);            // a texture
        Pixels.CopyTo(f, 128);
        return f;
    }

    public byte[] Tga()
    {
        var f = new byte[18 + Pixels.Length];
        f[2] = 2;
        BitConverter.GetBytes((ushort)Width).CopyTo(f, 12);
        BitConverter.GetBytes((ushort)Height).CopyTo(f, 14);
        f[16] = 32;
        f[17] = 0x28; // 8 alpha bits, top row first
        Pixels.CopyTo(f, 18);
        return f;
    }
}

/// <summary>Glyphs drawn by Windows (GDI): anti-aliased coverage, white (or dark) on transparent, with an optional dark outline and shadow.</summary>
internal sealed unsafe class Gdi : IDisposable
{
    public const uint FrPrivate = 0x10;
    private const uint GgoGray8 = 6, GgoGlyphIndex = 0x80, GdiError = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetrics
    {
        public uint BlackBoxX, BlackBoxY;
        public int OriginX, OriginY;
        public short CellIncX, CellIncY;
    }

    [DllImport("gdi32", CharSet = CharSet.Unicode)] public static extern int AddFontResourceExW(string file, uint flags, nint reserved);
    [DllImport("gdi32", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern int GetTextFaceW(nint dc, int count, char* face);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern uint GetGlyphIndicesW(nint dc, char* text, int count, ushort* indices, uint flags);
    [DllImport("gdi32")] private static extern uint GetFontData(nint dc, uint table, uint offset, byte* buffer, uint size);
    [DllImport("gdi32")] private static extern uint GetGlyphOutlineW(nint dc, uint glyph, uint format, GlyphMetrics* metrics, uint size, byte* buffer, int* matrix);

    private readonly nint _dc, _font;

    /// <summary>The face Windows actually picked (it substitutes one it can't find).</summary>
    public readonly string Face;

    /// <summary><paramref name="face"/> with lines <paramref name="lineHeight"/> pixels tall (GDI's cell height).</summary>
    public Gdi(string face, int lineHeight, bool bold)
    {
        _dc = CreateCompatibleDC(0);
        _font = CreateFontW(lineHeight, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 1 /* default charset */, 7 /* TrueType only */, 0, 4 /* anti-aliased */, 0, face);
        SelectObject(_dc, _font);
        var name = stackalloc char[64];
        Face = new string(name, 0, Math.Max(0, GetTextFaceW(_dc, 64, name) - 1));
    }

    /// <summary>The selected font's family names, in every language it has (Windows matches any of them).</summary>
    public List<string> FamilyNames()
    {
        const uint name = 0x656D616E; // "name", as GDI wants a table tag
        var size = GetFontData(_dc, name, 0, null, 0);
        if (size == GdiError) return [];
        var table = new byte[size];
        fixed (byte* buffer = table) GetFontData(_dc, name, 0, buffer, size);
        return Fonts.FamilyNames(table, 0);
    }

    /// <summary>
    /// The character's glyph (metrics against <paramref name="baseline"/>), or null if the face has none. With an outline also a
    /// shadow a pixel down and right, as the game's own outlined fonts have.
    /// </summary>
    public Glyph? Draw(char c, int outline, int baseline, bool darkInk)
    {
        ushort index;
        if (GetGlyphIndicesW(_dc, &c, 1, &index, 1 /* mark missing */) == GdiError || index == 0xFFFF) return null;
        var identity = stackalloc int[] { 0x10000, 0, 0, 0x10000 }; // MAT2: 1, 0, 0, 1 in 16.16
        GlyphMetrics m;
        var size = GetGlyphOutlineW(_dc, index, GgoGray8 | GgoGlyphIndex, &m, 0, null, identity);
        if (size == GdiError) return null;
        if (size == 0 || m.BlackBoxX == 0) return new Glyph(0, [], 0, 0, 0, 0, m.CellIncX); // a space
        var coverage = new byte[size];
        fixed (byte* buffer = coverage)
            if (GetGlyphOutlineW(_dc, index, GgoGray8 | GgoGlyphIndex, &m, size, buffer, identity) == GdiError) return null;
        int w = (int)m.BlackBoxX, h = (int)m.BlackBoxY, pitch = (w + 3) & ~3;
        var shadow = outline > 0 ? 1 : 0;
        int ow = w + 2 * outline + shadow, oh = h + 2 * outline + shadow;
        var fill = new float[ow * oh]; // 0..1
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            fill[(y + outline) * ow + x + outline] = Math.Min(64, (int)coverage[y * pitch + x]) / 64f;
        var pixels = new byte[ow * oh * 4];
        for (var y = 0; y < oh; y++)
        for (var x = 0; x < ow; x++)
        {
            var f = fill[y * ow + x];
            var o = 0f; // the outline: the most ink within its radius (a disc), and again a shadow's step up-left
            for (var s = 0; s <= shadow; s++)
            for (var dy = -outline; dy <= outline; dy++)
            for (var dx = -outline; dx <= outline; dx++)
                if (dx * dx + dy * dy <= outline * outline + 1 && x + dx - s >= 0 && x + dx - s < ow && y + dy - s >= 0 && y + dy - s < oh)
                    o = Math.Max(o, fill[(y + dy - s) * ow + x + dx - s]);
            var a = f + o * (1 - f); // ink over a black rim
            var at = (y * ow + x) * 4;
            var shade = darkInk ? (byte)0x20 : a > 0 ? (byte)(255 * f / a) : (byte)0;
            (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (shade, shade, shade, (byte)(255 * a));
        }
        return new Glyph(0, pixels, ow, oh, m.OriginX - outline, baseline - m.OriginY - outline, m.CellIncX);
    }

    public void Dispose()
    {
        DeleteObject(_font);
        DeleteDC(_dc);
    }
}
