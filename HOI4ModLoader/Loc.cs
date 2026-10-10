using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader;

/// <summary>
/// Text in the player's language, from the game's own localisation (localisation\&lt;language&gt;\*_l_&lt;language&gt;.yml in
/// any enabled mod; the HOI4ModLoader UI mod ships English and Korean for the loader and its plugins). A key the game
/// doesn't know gives the fallback, so everything still reads in English without the mod.
/// </summary>
public static unsafe class Loc
{
    /// <summary>Localize(std::string* out, const char* key, args, flags) (clausewitzlib localize.cpp): the key's text, or the key.</summary>
    [Signature] public const string LocalizeSig = "48 89 6C 24 18 48 89 74 24 20 57 41 56 41 57 48 83 EC 50 45 33 FF 41 8B E9 44 38 3D ? ? ? ? 4D 8B F0 48 8B F2";

    private static delegate* unmanaged<nint, byte*, nint, int, nint> _localize;

    /// <summary>The text of <paramref name="key"/> in the game's language, or <paramref name="fallback"/> if no loaded file has it.</summary>
    public static string Get(string key, string fallback)
    {
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment (no ??= for function pointers)
        if (_localize == null) _localize = (delegate* unmanaged<nint, byte*, nint, int, nint>)Sig.Find(LocalizeSig);
        var text = (nint)NativeMemory.AllocZeroed(32); // an empty std::string for the result
        try
        {
            var name = Encoding.UTF8.GetBytes(key + "\0");
            fixed (byte* p = name) _localize(text, p, 0, 0);
            var result = Encoding.UTF8.GetString(Native.MsvcString(text));
            return result.Length == 0 || result == key ? fallback : result;
        }
        finally
        {
            if (*(long*)(text + 0x18) >= 16) Allocator.Free(*(nint*)text); // the game's heap buffer
            NativeMemory.Free((void*)text);
        }
    }

    /// <summary><see cref="Get"/> with {0}, {1}... filled in (string.Format).</summary>
    public static string Format(string key, string fallback, params object[] args) => string.Format(Get(key, fallback), args);
}
