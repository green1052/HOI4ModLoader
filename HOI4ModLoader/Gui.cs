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

    /// <summary>The instantTextBox named <paramref name="name"/> in <paramref name="window"/>, or 0 if it has none.</summary>
    public static nint FindTextBox(nint window, string name)
    {
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment (no ??= for function pointers)
        if (_hasTextBox == null) _hasTextBox = (delegate* unmanaged<nint, nint, byte>)Sig.Find(HasTextBoxSig);
        var str = NewString(Encoding.ASCII.GetBytes(name));
        try { return _hasTextBox(window, str) == 0 ? 0 : Find(window, FindTextBoxSlot, str); }
        finally { FreeString(str); }
    }

    /// <summary>
    /// A child of <paramref name="window"/> by name through one of CContainerWindow's lookups (vtable slot: 13 button,
    /// 15 instantTextBox, 21 checkbox, 55 window). Like the game's own calls, only for children the .gui defines.
    /// </summary>
    public static nint Find(nint window, int slot, string name)
    {
        var str = NewString(Encoding.ASCII.GetBytes(name));
        try { return Find(window, slot, str); }
        finally { FreeString(str); }
    }

    private static nint Find(nint window, int slot, nint name) => ((delegate* unmanaged<nint, nint, byte, nint>)(*(nint**)window)[slot])(window, name, 0);

    /// <summary>The text box's current text (the game's bytes; valid until its text changes).</summary>
    public static ReadOnlySpan<byte> Text(nint textBox) => Native.MsvcString(textBox + TextOffset);

    public static void SetText(nint textBox, string text) => SetText(textBox, Encoding.UTF8.GetBytes(text));

    public static void SetText(nint textBox, ReadOnlySpan<byte> text)
    {
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
    /// Puts "N. " (<paramref name="position"/>) in front of the text box's text, replacing an earlier "N. ", or takes it off
    /// for position 0. Idempotent; sets the text only when it changes.
    /// </summary>
    public static void Number(nint box, int position)
    {
        var text = Text(box);
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit((char)text[digits])) digits++;
        var rest = digits > 0 && text[digits..].StartsWith(". "u8) ? text[(digits + 2)..] : text;
        byte[] shown = position > 0 ? [.. Encoding.ASCII.GetBytes($"{position}. "), .. rest] : [.. rest];
        if (!text.SequenceEqual(shown)) SetText(box, shown);
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

    public static bool ShiftHeld => (GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
    public static bool CtrlHeld => (GetAsyncKeyState(0x11) & 0x8000) != 0;  // VK_CONTROL
    public static bool AltHeld => (GetAsyncKeyState(0x12) & 0x8000) != 0;   // VK_MENU

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

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
