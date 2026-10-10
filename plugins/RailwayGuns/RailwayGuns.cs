using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace RailwayGuns;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// Army card right-click, railway gun branch: `mov rcx,[r13+10h]; call IsRailwayGunSelected; test al,al; jz; cmp byte
    /// [r13+CardIsArmyGroup],0; jnz skip; ...; call GetSelectedRailwayGuns; ...; call PostAssignRailwayGuns`.
    /// </summary>
    [Signature] public const string CardAssign = "49 8B 4D 10 E8 ? ? ? ? 84 C0 74 ? 41 80 BD ? ? ? ? 00 0F 85 ? ? ? ? 49 8B 55 10 48 8D 4D ? E8 ? ? ? ? 90 4D 8B C6 48 8D 55 ? 49 8B 4D 10 E8";

    /// <summary>Army card right-click (card); the same function AirWingFieldMarshal hooks.</summary>
    [Signature] public const string CardRightClick = "48 8B C4 48 89 58 ? 48 89 70 ? 48 89 78 ? 55 41 54 41 55 41 56 41 57 48 8D 68 ? 48 81 EC B0 00 00 00 0F 29 70 ? 4C 8B E9 48 83 C1 30";

    /// <summary>The army's railway gun box, assign button: posts an unassign for guns assigned elsewhere, then the assign.</summary>
    [Signature] public const string BoxUnassign = "4C 8B C6 48 8D 54 24 20 48 8B 0D ? ? ? ? E8 ? ? ? ? 90 48 8B 54 24 20 48 85 D2 74 ? 44 89 7C 24 2C 48 8B 4C 24 30 48 8B 01 FF 50 10 48 83 C3 08 48 3B DF 0F 85 ? ? ? ? 4C 8B C5 48 8D 54 24 38 48 8B 0D ? ? ? ? E8";

    /// <summary>
    /// Railway gun distribution, per gun: `mov r13d,eax; cmp eax,r14d; setle [rsp+3Ch]` = "enough convoys to ship it",
    /// which lets a gun target provinces outside its own rail network by sea.
    /// </summary>
    [Signature] public const string ConvoysOk = "44 8B E8 41 3B C6 0F 9E 44 24 3C 48 8B 7D B8";

    /// <summary>
    /// Railway gun distribution: score of a candidate province (ctx**, long* out, int province) -> out. *ctx is the
    /// CPdxArray of 48-byte province records the distribution built: +0 id, +12 hostile units, +25 controller at war with us.
    /// </summary>
    [Signature] public const string ScoreProvince = "48 89 5C 24 ? 48 89 54 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 80 00 00 00 48 8B 19";
}

/// <summary>
/// Right-click a field marshal's card with railway guns selected to assign them to his armies, spread over them in
/// selection order, with the vanilla assign command (so multiplayer stays in sync and other players need nothing). Guns
/// assigned elsewhere are unassigned first, as the army's own railway gun box does (a card right-click alone would fail).
/// </summary>
[Plugin("com.green1052.railwayguns", "Railway Gun Field Marshal", "1.0.0", GameVersion = "1.19.3")]
public unsafe class RailwayGunFieldMarshal : Plugin
{
    private const int ViewOfCard = 0x10;
    private const int AssignedTo = 0x440, GunsOfGroup = 0x18; // CRailwayGun -> its orders group's railway gun container (group + 0x18)

    [Function(CallingConventions.Microsoft)] public delegate void CardFn(nint card);
    [Function(CallingConventions.Microsoft)] public delegate byte PostFn(nint view, nint guns, nint group);

    private IHook<CardFn>? _cardRightClick;
    private IHook<PostFn>? _postAssign;
    private delegate* unmanaged<nint, nint, nint, byte> _postUnassign;

    public override void Load()
    {
        var at = Sig.Find(Signatures.CardAssign);
        var gunSelected = (delegate* unmanaged<nint, byte>)(at + 9 + *(int*)(at + 5));
        var isArmyGroup = *(int*)(at + 16);
        var postAssign = at + 57 + *(int*)(at + 53);
        var unassign = Sig.Find(Signatures.BoxUnassign);
        _postUnassign = (delegate* unmanaged<nint, nint, nint, byte>)(unassign + 20 + *(int*)(unassign + 16));

        // A marshal's card ignores a right-click with railway guns selected; as an army card's it gets to the assign below.
        _cardRightClick = Patches.Hook<CardFn>(Sig.Find(Signatures.CardRightClick), card =>
        {
            var flag = (byte*)(card + isArmyGroup);
            var was = *flag;
            if (gunSelected(*(nint*)(card + ViewOfCard)) != 0) *flag = 0;
            try { _cardRightClick!.OriginalFunction(card); }
            finally { *flag = was; }
        });
        _postAssign = Patches.Hook<PostFn>(postAssign, Assign);
    }

    private byte Assign(nint view, nint guns, nint group)
    {
        if (group == 0) return _postAssign!.OriginalFunction(view, guns, group);
        var all = Native.PdxArray(guns).ToArray();
        var og = new OrdersGroup(group);
        var targets = og.IsArmyGroup ? og.Armies.ToArray() : [group];
        if (targets.Length == 0) return 0;

        byte posted = 0;
        for (var t = 0; t < targets.Length && t < all.Length; t++)
        {
            // ponytail: round-robin in selection order, not weighted by army size or front.
            var share = all.Where((_, i) => i % targets.Length == t).ToArray();
            foreach (var gun in share)
            {
                var from = *(nint*)(gun + AssignedTo);
                if (from != 0 && from - GunsOfGroup != targets[t]) WithArray([gun], view1 => _postUnassign(view, view1, from - GunsOfGroup));
            }
            WithArray(share, view1 => posted |= _postAssign!.OriginalFunction(view, view1, targets[t]));
        }
        if (og.IsArmyGroup) Log.Info($"{all.Length} railway guns assigned across {Math.Min(targets.Length, all.Length)} of the marshal's armies");
        return posted;
    }

    /// <summary>Runs <paramref name="action"/> with a CPdxArray view of <paramref name="items"/> (the commands read only data +0 and size +0xC).</summary>
    private static void WithArray(nint[] items, Action<nint> action)
    {
        var view = stackalloc nint[2];
        fixed (nint* data = items)
        {
            view[0] = (nint)data;
            ((int*)view)[2] = ((int*)view)[3] = items.Length;
            action((nint)view);
        }
    }
}

/// <summary>
/// Railway guns keep off the front line and stay on their own rail network: candidate provinces next to the enemy
/// (enemy-controlled or with hostile units) get a large penalty, so guns sit just behind the line instead of being overrun,
/// and guns never ship out by sea (where submarines sink them). Changes the game's distribution, so single player only.
/// </summary>
[Plugin("com.green1052.railwaygunmovement", "Railway Gun Movement", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe class RailwayGunMovement : Plugin
{
    private const int RecordSize = 48, HostileUnits = 12, ControllerAtWar = 25; // province records of the distribution
    private const int ProvincesOfState = 8;                                     // gamestate: province container (vtable slot 0 = by id)
    private const int AdjacencyOfProvince = 0xB8, Adjacencies = 0x70, AdjacencySize = 0x30, NeighbourId = 8;
    private const long Fixed = 100_000;

    [Function(CallingConventions.Microsoft)] public delegate nint ScoreFn(nint ctx, nint score, int province);

    private IHook<ScoreFn>? _score;
    private long _frontPenalty;

    public override void Load()
    {
        _frontPenalty = Config.Bind("Movement", "FrontPenalty", 500,
            "Score taken off provinces on the front line (next to enemy-controlled provinces or hostile units). Vanilla bonuses for being near the fighting are about 100-200.") * Fixed;
        if (Config.Bind("Movement", "NoSeaTransfer", true, "Never ship railway guns by sea; they only move along their own rail network."))
        {
            var setle = Sig.Find(Signatures.ConvoysOk) + 6;
            Patches.Bytes(setle, "0F 9E 44 24 3C", "C6 44 24 3C 00"); // setle [rsp+3Ch] -> mov byte [rsp+3Ch],0: never enough convoys
        }
        _score = Patches.Hook<ScoreFn>(Sig.Find(Signatures.ScoreProvince), (ctx, score, province) =>
        {
            var result = _score!.OriginalFunction(ctx, score, province);
            if (IsFront(*(nint*)ctx, province)) *(long*)score -= _frontPenalty;
            return result;
        });
    }

    /// <summary>The province or a neighbour has hostile units or an enemy controller, per the distribution's own records.</summary>
    private static bool IsFront(nint records, int province)
    {
        if (Enemy(records, province)) return true;
        var provinces = Game.State + ProvincesOfState;
        var p = ((delegate* unmanaged<nint, int, nint>)(**(nint**)provinces))(provinces, province);
        var adjacency = p == 0 ? 0 : *(nint*)(p + AdjacencyOfProvince);
        if (adjacency == 0) return false;
        var data = *(nint*)(adjacency + Adjacencies);
        for (int i = 0, n = *(int*)(adjacency + Adjacencies + 0xC); i < n; i++)
            if (Enemy(records, *(int*)(data + i * AdjacencySize + NeighbourId))) return true;
        return false;
    }

    // ponytail: linear scan of the records per lookup; a dictionary per distribution run if it ever shows in a profile.
    private static bool Enemy(nint records, int province)
    {
        var data = *(nint*)records;
        for (int i = 0, n = *(int*)(records + 0xC); i < n; i++)
        {
            var r = data + i * RecordSize;
            if (*(int*)r == province) return *(byte*)(r + ControllerAtWar) != 0 || *(int*)(r + HostileUnits) > 0;
        }
        return false; // not near our divisions: not front
    }
}
