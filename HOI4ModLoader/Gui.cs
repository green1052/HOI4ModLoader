using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader;

/// <summary>
/// Reading and writing the game's GUI widgets (elements defined in interface\*.gui). Widget text is the game's byte
/// encoding (Windows-1252 plus § colour codes, or whatever a language mod maps in), so it is handled as bytes.
/// </summary>
public static unsafe class Gui
{
    /// <summary>CInstantTextBox::SetText(box, std::string*): moves the string in (freeing the old text) and re-lays it out.</summary>
    [Signature] public const string SetTextSig = "48 89 5C 24 ? 48 89 74 24 ? 48 89 54 24 ? 57 48 83 EC 20 48 8B F2";

    /// <summary>CContainerWindow::HasInstantTextBox(window, std::string* name) -> bool.</summary>
    [Signature] public const string HasTextBoxSig = "48 83 EC 28 48 8B C2 4C 8D 81 E0 11 00 00 48 8D 91 F8 11 00 00 48 8B C8 E8 ? ? ? ? 48 85 C0 0F 95 C0 48 83 C4 28 C3";

    private const int FindTextBoxSlot = 15; // CContainerWindow vtable: instantTextBox by name (std::string*, bool logIfMissing); never null
    private const int ShowSlot = 15, Flags = 117, HiddenFlag = 0x10; // GUI object: Show(), flags byte
    private const int TextOffset = 0x110; // CInstantTextBox: std::string text

    private static delegate* unmanaged<nint, nint, void> _setText;
    private static delegate* unmanaged<nint, nint, byte> _hasTextBox;

    /// <summary>The instantTextBox named <paramref name="name"/> in <paramref name="window"/>, or 0 if it has none.</summary>
    public static nint FindTextBox(nint window, string name)
    {
        if (_hasTextBox == null) _hasTextBox = (delegate* unmanaged<nint, nint, byte>)Sig.Find(HasTextBoxSig);
        var str = NewString(Encoding.ASCII.GetBytes(name));
        try
        {
            return _hasTextBox(window, str) == 0 ? 0 : ((delegate* unmanaged<nint, nint, byte, nint>)(*(nint**)window)[FindTextBoxSlot])(window, str, 0);
        }
        finally { FreeString(str); }
    }

    /// <summary>The text box's current text (the game's bytes; valid until its text changes).</summary>
    public static ReadOnlySpan<byte> Text(nint textBox) => Native.MsvcString(textBox + TextOffset);

    public static void SetText(nint textBox, string text) => SetText(textBox, Encoding.UTF8.GetBytes(text));

    public static void SetText(nint textBox, ReadOnlySpan<byte> text)
    {
        if (_setText == null) _setText = (delegate* unmanaged<nint, nint, void>)Sig.Find(SetTextSig);
        var str = NewString(text);
        try { _setText(textBox, str); }
        finally { NativeMemory.Free((void*)str); } // SetText already took (and freed) any heap buffer
    }

    public static bool IsHidden(nint widget) => (*(byte*)(widget + Flags) & HiddenFlag) != 0;

    /// <summary>Shows a widget the code hid (what the game does: clear the hidden flag, then Show()).</summary>
    public static void Show(nint widget)
    {
        *(byte*)(widget + Flags) &= unchecked((byte)~HiddenFlag);
        ((delegate* unmanaged<nint, void>)(*(nint**)widget)[ShowSlot])(widget);
    }

    public static bool ShiftHeld => (GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
    public static bool CtrlHeld => (GetAsyncKeyState(0x11) & 0x8000) != 0;  // VK_CONTROL

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static nint NewString(ReadOnlySpan<byte> value)
    {
        var s = (nint)NativeMemory.AllocZeroed(32);
        *(long*)(s + 24) = 15; // empty, inline buffer
        Native.SetMsvcString(s, value);
        return s;
    }

    private static void FreeString(nint s)
    {
        if (*(long*)(s + 24) >= 16) NativeMemory.Free(*(void**)s);
        NativeMemory.Free((void*)s);
    }
}
