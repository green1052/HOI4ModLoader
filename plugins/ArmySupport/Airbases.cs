using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>A string only <see cref="SendSelectedWings"/> references, to find it if the pattern stops matching.</summary>
    public const string SendAnchor = "order_airwing_effect";

    /// <summary>
    /// UI: send the selected air wings to an airbase (CAirBase* dest); posts one vanilla transfer command per wing.
    /// Right-clicking an airbase, a carrier or a naval mission all end here.
    /// </summary>
    [Signature(Anchor = SendAnchor)] public const string SendSelectedWings = "48 89 4C 24 ? 55 53 56 57 41 54 41 56 41 57 48 8D 6C 24 ? 48 81 EC B0 00 00 00 45 33 E4 41 8B DC 89 5D";

    /// <summary>UI: the selected air wings (CPdxArray* out, int role filter). The send above gets its list here.</summary>
    [Signature] public const string SelectedWings = "40 55 41 56 48 83 EC 38 4C 8B F1 8B EA 48 8B 0D ? ? ? ? 48 85 C9 0F 84 ? ? ? ? 48 81 C1 38 05 00 00";

    /// <summary>CAirBase::GetNumPlanes(base): planes stationed there, every country's (the "used" number of the capacity tooltip).</summary>
    [Signature] public const string PlanesAtBase = "48 89 74 24 ? 48 89 7C 24 ? 41 56 48 83 EC 20 48 8B 71 48 33 FF 48 63 41 54";
}

/// <summary>
/// Shift+right-click an airbase with air wings selected to send only the wings that fit (capacity minus planes there and
/// on their way). Only the UI's selection list is trimmed, so the game posts its own commands and multiplayer works.
/// </summary>
public unsafe partial class ArmySupport
{
    // Layouts for HOI4 1.19.3.
    private const int Handle = 0x08, Incoming = 0x18, Capacity = 0x78;           // CAirBase: CRef id, CPdxArray<CAirWing*> flying in, planes
    private const int Pool = 0x38, Destination = 0x48, Planes = 0x7C, BaseOfPool = 0x18; // CAirWing (+ its pool's airbase CRef id)

    [Function(CallingConventions.Microsoft)] public delegate void SendFn(nint dest);
    [Function(CallingConventions.Microsoft)] public delegate void SelectedFn(nint wings, int filter);

    private IHook<SendFn>? _send;
    private IHook<SelectedFn>? _selected;
    private delegate* unmanaged<nint, int> _planesAt;
    private nint _dest; // the airbase while a Shift+send runs

    private void LoadSending()
    {
        var send = Sig.Find(Signatures.SendSelectedWings, Signatures.SendAnchor);
        var selected = Sig.Find(Signatures.SelectedWings);
        _planesAt = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.PlanesAtBase);

        _send = Patches.Hook<SendFn>(send, dest =>
        {
            _dest = Gui.ShiftHeld ? dest : 0;
            try { _send!.OriginalFunction(dest); }
            finally { _dest = 0; }
        });
        _selected = Patches.Hook<SelectedFn>(selected, (wings, filter) =>
        {
            _selected!.OriginalFunction(wings, filter);
            if (_dest != 0) Log.Guard("trim selection", () => KeepWhatFits(wings, _dest));
        });
    }

    /// <summary>Drops wings from the selection list (in place; the caller owns it) until the rest fit at <paramref name="dest"/>.</summary>
    private void KeepWhatFits(nint wings, nint dest)
    {
        var room = *(int*)(dest + Capacity) - _planesAt(dest);
        foreach (var wing in Native.PdxArray(dest + Incoming)) room -= *(int*)(wing + Planes);

        var id = *(ulong*)(dest + Handle);
        var data = Native.PdxArray<nint>(wings);
        var kept = 0;
        foreach (var wing in data)
        {
            // Needs no room: already there (vanilla cancels the transfer), flying there (counted above), or no pool (vanilla treats it as there).
            var pool = *(nint*)(wing + Pool);
            var counted = pool == 0 || *(ulong*)(pool + BaseOfPool) == id || *(ulong*)(wing + Destination) == id;
            if (!counted)
            {
                if (*(int*)(wing + Planes) > room) continue;
                room -= *(int*)(wing + Planes);
            }
            data[kept++] = wing;
        }
        if (kept < data.Length) Log.Info($"sending {kept} of {data.Length} air wings: the rest don't fit");
        Native.SetPdxArraySize(wings, kept);
    }
}
