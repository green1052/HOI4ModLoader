using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader;

/// <summary>
/// Reading and writing the game's GUI widgets (elements defined in interface\*.gui). Widget text is the game's byte
/// encoding (Windows-1252 plus § color codes, or whatever a language mod maps in), so it is handled as bytes.
/// </summary>
public static unsafe partial class Gui
{
    /// <summary>CInstantTextBox::SetText(box, std::string*): moves the string in (freeing the old text) and re-lays it out.</summary>
    [Signature] public const string SetTextSig = "48 89 5C 24 ? 48 89 74 24 ? 48 89 54 24 ? 57 48 83 EC 20 48 8B F2";

    /// <summary>CContainerWindow::HasInstantTextBox(window, std::string* name) -> bool.</summary>
    [Signature] public const string HasTextBoxSig = "48 83 EC 28 48 8B C2 4C 8D 81 E0 11 00 00 48 8D 91 F8 11 00 00 48 8B C8 E8 ? ? ? ? 48 85 C0 0F 95 C0 48 83 C4 28 C3";

    private const int FindTextBoxSlot = 15; // CContainerWindow vtable: instantTextBox by name (std::string*, bool logIfMissing); never null
    private const int ShowSlot = 15, HideSlot = 16, Flags = 117, HiddenFlag = 0x10; // GUI object: Show(), Hide(), flags byte
    private const int TextOffset = 0x110; // CInstantTextBox: std::string text
    private const int SetTooltipHandlerSlot = 69; // GUI object: SetTooltipHandler(CTooltipHandler*), used when the .gui has no tooltip

    private static delegate* unmanaged<nint, nint, void> _setText;
    private static delegate* unmanaged<nint, nint, byte> _hasTextBox;
    private static nint _tooltipVTable;                       // CTooltipHandler vtable: { BuildTooltip, destructor }
    private static readonly Dictionary<nint, nint> TooltipHandlers = [];      // widget -> its handler { vtable, widget }
    private static readonly Dictionary<nint, Func<string>> TooltipTexts = []; // widget -> its text

    /// <summary>The instantTextBox named <paramref name="name"/> in <paramref name="window"/>, or 0 if it has none (or no window).</summary>
    public static nint FindTextBox(nint window, string name)
    {
        if (window == 0) return 0; // a native null read can't be caught: it ends the game
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment (no ??= for function pointers)
        if (_hasTextBox == null) _hasTextBox = (delegate* unmanaged<nint, nint, byte>)Sig.Find(HasTextBoxSig);
        var str = Name(name);
        return _hasTextBox(window, str) == 0 ? 0 : Find(window, FindTextBoxSlot, str);
    }

    // Names looked up as game strings, made once and kept: per-frame lookups (queues, trade, construction) made and freed one each time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, nint> Names = new();

    private static nint Name(string name) => Names.GetOrAdd(name, n => NewString(Encoding.ASCII.GetBytes(n)));

    /// <summary>
    /// A child of <paramref name="window"/> by name through one of CContainerWindow's lookups (vtable slot: 13 button,
    /// 15 instantTextBox, 21 checkbox, 55 window). Like the game's own calls, only for children the .gui defines. 0 for no window.
    /// </summary>
    public static nint Find(nint window, int slot, string name)
    {
        return window == 0 ? 0 : Find(window, slot, Name(name));
    }

    private static nint Find(nint window, int slot, nint name) => ((delegate* unmanaged<nint, nint, byte, nint>)(*(nint**)window)[slot])(window, name, 0);

    /// <summary>The text box's current text (the game's bytes; valid until its text changes).</summary>
    public static ReadOnlySpan<byte> Text(nint textBox) => Native.MsvcString(textBox + TextOffset);

    public static void SetText(nint textBox, string text) => SetText(textBox, Encoding.UTF8.GetBytes(text));

    public static void SetText(nint textBox, ReadOnlySpan<byte> text)
    {
        if (textBox == 0) return; // FindTextBox found none
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment (no ??= for function pointers)
        if (_setText == null) _setText = (delegate* unmanaged<nint, nint, void>)Sig.Find(SetTextSig);
        var str = NewString(text);
        try { _setText(textBox, str); }
        finally { Allocator.Free(str); } // SetText already took (and freed) any heap buffer
    }

    public static bool IsHidden(nint widget) => (*(byte*)(widget + Flags) & HiddenFlag) != 0;

    /// <summary>Shows a widget the code hid (what the game does: clear the hidden flag, then Show()).</summary>
    public static void Show(nint widget)
    {
        *(byte*)(widget + Flags) &= unchecked((byte)~HiddenFlag);
        ((delegate* unmanaged<nint, void>)(*(nint**)widget)[ShowSlot])(widget);
    }

    /// <summary>Hides a widget (what the game does: set the hidden flag, then Hide()).</summary>
    public static void Hide(nint widget)
    {
        *(byte*)(widget + Flags) |= HiddenFlag;
        ((delegate* unmanaged<nint, void>)(*(nint**)widget)[HideSlot])(widget);
    }

    /// <summary>
    /// Puts <paramref name="position"/> in front of the text box's text as a numbered badge (an inline text icon, see
    /// <see cref="WriteNumberIcons"/>; "[N] " past the last one), replacing an earlier one, or takes it off for position 0.
    /// Idempotent; sets the text only when it changes.
    /// </summary>
    public static void Number(nint box, int position)
    {
        var text = Text(box);
        var shown = Numbered(text, position);
        if (!text.SequenceEqual(shown)) SetText(box, shown);
    }

    /// <summary><paramref name="text"/> with its badge (if any) replaced by <paramref name="position"/>'s (none for 0).</summary>
    internal static byte[] Numbered(ReadOnlySpan<byte> text, int position)
    {
        var rest = text;
        var open = text.StartsWith(NumberIcon) ? NumberIcon.Length : text.StartsWith("["u8) ? 1 : 0;
        if (open > 0)
        {
            var end = open;
            while (end < text.Length && char.IsAsciiDigit((char)text[end])) end++;
            var close = open == 1 ? "] "u8 : " "u8;
            if (end > open && text[end..].StartsWith(close)) rest = text[(end + close.Length)..];
        }
        return position <= 0 ? [.. rest]
            : position <= NumberIcons ? [.. NumberIcon, .. Encoding.ASCII.GetBytes($"{position} "), .. rest]
            : [.. Encoding.ASCII.GetBytes($"[{position}] "), .. rest];
    }

    // "£hoimod_numbers|": the game's inline sprite syntax (£name|frame, as in vanilla's £resources_strip|3), £ in its encoding.
    private static readonly byte[] NumberIcon = [0xA3, .. "hoimod_numbers|"u8];
    private const int NumberIcons = 30, NumberIconSize = 18, NumberIconWidth = 24; // 24 wide: two digits get a pill

    /// <summary>
    /// interface/hoimod_numbers.gfx and its texture under <paramref name="dir"/> (the game files we mount): frame N is N in
    /// dark digits on a gold disc, like a circled number; the game's fonts have no ① .. ㉚.
    /// </summary>
    internal static void WriteNumberIcons(string dir)
    {
        // 3x5 digits, rows top to bottom, drawn at twice their size.
        string[] glyphs = ["111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
            "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111"];
        const int s = NumberIconSize, w = NumberIconWidth, width = w * NumberIcons;
        const double radius = s / 2.0 - 0.5;
        var pixels = new byte[width * s * 4]; // BGRA, top row first
        for (var frame = 0; frame < NumberIcons; frame++)
        {
            var digits = (frame + 1).ToString();
            var text = digits.Length * 8 - 2; // 6 px a digit, 2 between
            var left = frame * w + (w - text) / 2;
            var stretch = Math.Max(0, (text + 8 - s) / 2.0); // two digits: a pill, its round ends clear of the digits
            const int top = (s - 10) / 2;
            for (var y = 0; y < s; y++)
            for (var x = 0; x < w; x++)
            {
                var inside = 0; // 4x4 samples: an anti-aliased disc (or pill)
                var rim = 0;
                for (var sy = 0; sy < 4; sy++)
                for (var sx = 0; sx < 4; sx++)
                {
                    var dx = Math.Max(0, Math.Abs(x + (sx + 0.5) / 4 - w / 2.0) - stretch);
                    var d = Math.Sqrt(dx * dx + Math.Pow(y + (sy + 0.5) / 4 - s / 2.0, 2));
                    if (d <= radius) inside++;
                    if (d > radius - 1.5) rim++;
                }
                var px = frame * w + x;
                var glyph = (px - left) is var gx && gx >= 0 && gx % 8 < 6 && gx / 8 < digits.Length && y - top is >= 0 and < 10
                    && glyphs[digits[gx / 8] - '0'][(y - top) / 2 * 3 + gx % 8 / 2] == '1';
                (byte b, byte g, byte r) = glyph ? ((byte)0x1A, (byte)0x1A, (byte)0x1A) : rim > 8 ? ((byte)0x10, (byte)0x6A, (byte)0x9A) : ((byte)0x44, (byte)0xC7, (byte)0xF2);
                var at = (y * width + px) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (b, g, r, (byte)(inside * 255 / 16));
            }
        }
        // Uncompressed 32-bit TGA, origin top left (descriptor 0x28: 8 alpha bits, top-down rows).
        var tga = new byte[18 + pixels.Length];
        tga[2] = 2;
        BitConverter.GetBytes((ushort)width).CopyTo(tga, 12);
        BitConverter.GetBytes((ushort)s).CopyTo(tga, 14);
        tga[16] = 32;
        tga[17] = 0x28;
        pixels.CopyTo(tga, 18);
        Directory.CreateDirectory(Path.Combine(dir, "gfx", "interface"));
        File.WriteAllBytes(Path.Combine(dir, "gfx", "interface", "hoimod_numbers.tga"), tga);
        Directory.CreateDirectory(Path.Combine(dir, "interface"));
        File.WriteAllText(Path.Combine(dir, "interface", "hoimod_numbers.gfx"),
            $"spriteTypes = {{\n\tspriteType = {{\n\t\tname = \"GFX_hoimod_numbers\"\n\t\ttexturefile = \"gfx/interface/hoimod_numbers.tga\"\n\t\tnoOfFrames = {NumberIcons}\n\t}}\n}}\n");
    }

    /// <summary>Puts <paramref name="prefix"/> in front of the text box's text when <paramref name="on"/>, else takes it off. Idempotent.</summary>
    public static void SetPrefix(nint box, ReadOnlySpan<byte> prefix, bool on)
    {
        var text = Text(box);
        var has = text.StartsWith(prefix);
        if (on && !has) SetText(box, [.. prefix, .. text]);
        else if (!on && has) SetText(box, [.. text[prefix.Length..]]);
    }

    /// <summary>Text the GUI shows as written: it expands $KEY$, [..] and § codes otherwise.</summary>
    public static string Literal(string text) => string.Concat(text.Where(c => c is not ('$' or '[' or ']' or '§')));

    /// <summary>
    /// Gives <paramref name="widget"/> a tooltip (for widgets whose .gui has none): <paramref name="text"/> makes it each time
    /// it shows, every frame while hovered, so it stays current. Shown as written (<see cref="Literal"/>). UI thread only.
    /// </summary>
    public static void SetTooltip(nint widget, Func<string> text)
    {
        if (widget == 0) return;
        if (_tooltipVTable == 0)
        {
            var vtable = (nint*)NativeMemory.Alloc(2, (nuint)sizeof(nint)); // the GUI never frees tooltip handlers
            vtable[0] = (nint)(delegate* unmanaged<nint, nint, nint, void>)&BuildTooltip;
            vtable[1] = (nint)(delegate* unmanaged<nint, int, void>)&DestroyTooltip;
            _tooltipVTable = (nint)vtable;
        }
        TooltipTexts[widget] = text;
        if (!TooltipHandlers.TryGetValue(widget, out var handler))
        {
            var created = (nint*)NativeMemory.Alloc(2, (nuint)sizeof(nint));
            created[0] = _tooltipVTable;
            created[1] = widget;
            TooltipHandlers[widget] = handler = (nint)created;
        }
        ((delegate* unmanaged<nint, nint, void>)(*(nint**)widget)[SetTooltipHandlerSlot])(widget, handler);
    }

    /// <summary>CTooltipHandler::Build(handler, widget, out): the text goes into out's first std::string.</summary>
    [UnmanagedCallersOnly]
    private static void BuildTooltip(nint handler, nint widget, nint tooltip)
    {
        try
        {
            if (TooltipTexts.TryGetValue(((nint*)handler)[1], out var text))
                Native.SetMsvcString(tooltip, Encoding.UTF8.GetBytes(Literal(text())));
        }
        catch { /* never throw into the game */ }
    }

    [UnmanagedCallersOnly]
    private static void DestroyTooltip(nint handler, int flags) { }

    public static bool ShiftHeld => KeyHeld(0x10); // VK_SHIFT
    public static bool CtrlHeld => KeyHeld(0x11);  // VK_CONTROL
    public static bool AltHeld => KeyHeld(0x12);   // VK_MENU

    /// <summary>Whether the key (a virtual-key code, e.g. 'S') is down right now, wherever the focus is.</summary>
    public static bool KeyHeld(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>
    /// The game window has the focus: key state is global, so a hotkey must check this or it fires while the player types
    /// in another program (and AltGr is Ctrl+Alt on many layouts).
    /// </summary>
    public static bool GameFocused => GetForegroundWindow() is var window && window != 0
        && GetWindowThreadProcessId(window, out var process) != 0 && process == (uint)Environment.ProcessId;

    /// <summary>
    /// Ctrl+Alt+<paramref name="key"/> was just pressed with the game focused: once per press, for a timer polling it
    /// (<paramref name="down"/>: the key's state at the last poll; a key held across a modifier change does not fire).
    /// </summary>
    public static bool HotkeyPressed(int key, ref bool down)
    {
        var now = KeyHeld(key);
        var hit = now && !down && CtrlHeld && AltHeld && GameFocused;
        down = now;
        return hit;
    }

    /// <summary>
    /// UI_AreAirWingsSelected: `mov rcx,[rip+ui]; test rcx,rcx; jz; add rcx,538h`: the player's selection list (CSelectionList)
    /// inside the UI object. List +0x30 first node {item, ?, next at +0x10}, +0x40 count; an item's kind at +8 (0 = a land
    /// division, the CArmy itself; 1 ship, 2 air wing).
    /// </summary>
    [Signature] public const string AirWingsSelectedSig = "48 83 EC 28 48 8B 0D ? ? ? ? 48 85 C9 74 ? 48 81 C1 38 05 00 00";

    private const int FirstNode = 0x30, NextOfNode = 0x10;
    private static nint* _selectionUi;
    private static int _selectionOffset;

    /// <summary>The selected items (divisions, ships, air wings), unchecked: compare them with live objects before reading them. UI thread.</summary>
    public static List<nint> Selection()
    {
        if (_selectionUi == null)
        {
            var at = Sig.Find(AirWingsSelectedSig);
            _selectionOffset = *(int*)(at + 19);
            _selectionUi = (nint*)(at + 11 + *(int*)(at + 7));
        }
        var items = new List<nint>();
        var ui = *_selectionUi;
        if (ui == 0) return items;
        var node = *(nint*)(ui + _selectionOffset + FirstNode);
        for (var n = 0; node != 0 && n < 5000; n++, node = *(nint*)(node + NextOfNode)) items.Add(*(nint*)node);
        return items;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    private static nint NewString(ReadOnlySpan<byte> value)
    {
        var s = Allocator.AllocZeroed(32);
        *(long*)(s + 24) = 15; // empty, inline buffer
        Native.SetMsvcString(s, value);
        return s;
    }

    private static void FreeString(nint s)
    {
        Native.FreeMsvcString(s);
        Allocator.Free(s);
    }
}
