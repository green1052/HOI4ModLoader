using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader;

/// <summary>Reading and writing the game's GUI widgets (elements defined in interface\*.gui).</summary>
public static unsafe class Gui
{
    /// <summary>CInstantTextBox::SetText(box, std::string*): takes the string's contents (and frees its heap buffer).</summary>
    [Signature] public const string SetTextSig = "48 89 5C 24 ? 48 89 74 24 ? 48 89 54 24 ? 57 48 83 EC 20 48 8B F2";

    const int FindChildSlot = 15; // CWindow vtable: child by name (std::string*, bool)

    static delegate* unmanaged<nint, nint, void> _setText;

    /// <summary>The child element named <paramref name="name"/> of <paramref name="window"/>, or 0.</summary>
    public static nint FindChild(nint window, string name)
    {
        var str = NewString(name);
        try { return ((delegate* unmanaged<nint, nint, byte, nint>)(*(nint**)window)[FindChildSlot])(window, str, 0); }
        finally { FreeString(str); }
    }

    public static void SetText(nint textBox, string text)
    {
        if (_setText == null) _setText = (delegate* unmanaged<nint, nint, void>)Sig.Find(SetTextSig);
        var str = NewString(text);
        try { _setText(textBox, str); }
        finally { NativeMemory.Free((void*)str); } // SetText already took (and freed) any heap buffer
    }

    /// <summary>
    /// An MSVC std::string { char buf[16] | char* ptr; size; capacity }. Heap buffers come from the CRT heap,
    /// the same process heap the game's statically linked CRT frees from.
    /// </summary>
    static nint NewString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var s = (nint)NativeMemory.AllocZeroed(32);
        var data = bytes.Length < 16 ? s : (nint)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);
        if (data != s) *(nint*)s = data;
        bytes.CopyTo(new Span<byte>((void*)data, bytes.Length));
        *(long*)(s + 16) = bytes.Length;
        *(long*)(s + 24) = Math.Max(15, bytes.Length);
        return s;
    }

    static void FreeString(nint s)
    {
        if (*(long*)(s + 24) >= 16) NativeMemory.Free(*(void**)s);
        NativeMemory.Free((void*)s);
    }
}
