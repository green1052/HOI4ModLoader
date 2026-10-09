using System.Text;
using System.Text.Json;

namespace HOI4ModLoader;

public static class Game
{
    /// <summary>launcher-settings.json "rawVersion", e.g. "1.19.3.0".</summary>
    public static readonly string Version = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "launcher-settings.json"))).RootElement.GetProperty("rawVersion").GetString()!;
}

/// <summary>Readers for engine containers.</summary>
public static unsafe class Native
{
    /// <summary>CPdxArray&lt;T*&gt; at <paramref name="at"/>: { T** data; int capacity; int size; }.</summary>
    public static ReadOnlySpan<nint> PdxArray(nint at) => new((void*)*(nint*)at, *(int*)(at + 0xC));

    /// <summary>Characters of an MSVC std::string at <paramref name="at"/> (inline buffer when capacity &lt; 16).</summary>
    public static Span<byte> MsvcString(nint at)
    {
        var size = *(long*)(at + 0x10);
        var data = *(long*)(at + 0x18) < 16 ? (byte*)at : *(byte**)at;
        return new Span<byte>(data, (int)size);
    }

    public static string Utf8(Span<byte> bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>COrdersGroup (hoi4/source/ordersgroup.cpp): an army or an army group. Offsets for HOI4 1.19.3.</summary>
public readonly unsafe struct OrdersGroup(nint ptr)
{
    public const int IsArmyGroupOffset = 0x39;
    public const int OrdersOffset = 0x50;          // CPdxArray<COrder*>: front lines etc.
    public const int AttachedWingsOffset = 0x70;   // CPdxArray<CAirWing*>
    public const int ParentOffset = 0x1B8;         // army -> its army group
    public const int ArmiesOffset = 0x230;         // CPdxArray<COrdersGroup*>: armies of an army group

    public nint Ptr { get; } = ptr;
    public bool IsArmyGroup => *(byte*)(Ptr + IsArmyGroupOffset) != 0;
    public nint Orders => Ptr + OrdersOffset;
    public int AttachedWingCount => *(int*)(Ptr + AttachedWingsOffset + 0xC);
    public ReadOnlySpan<nint> Armies => Native.PdxArray(Ptr + ArmiesOffset);
}
