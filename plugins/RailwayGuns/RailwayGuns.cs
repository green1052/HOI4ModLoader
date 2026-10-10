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

    /// <summary>The army's railway gun box, assign button: posts an unassign for guns assigned elsewhere, then the assign.</summary>
    [Signature] public const string BoxUnassign = "4C 8B C6 48 8D 54 24 20 48 8B 0D ? ? ? ? E8 ? ? ? ? 90 48 8B 54 24 20 48 85 D2 74 ? 44 89 7C 24 2C 48 8B 4C 24 30 48 8B 01 FF 50 10 48 83 C3 08 48 3B DF 0F 85 ? ? ? ? 4C 8B C5 48 8D 54 24 38 48 8B 0D ? ? ? ? E8";

    /// <summary>UnitController::DistributeRailwayGuns(controller, -1, 0): *controller is the orders group whose guns it places.</summary>
    [Signature] public const string Distribute = "48 89 5C 24 10 48 89 4C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 00 DD FF FF B8 00 24 00 00 E8";

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
/// Railway guns can be assigned to a field marshal: right-click his card with railway guns selected (or use his card's
/// railway gun box, which AirWingFieldMarshal's card patch shows). His guns take positions along all of his armies'
/// divisions. Army groups owning guns changes the game, so in multiplayer the right-click spreads them over his armies
/// instead, with the vanilla assign command, and other players need nothing. Either way guns assigned elsewhere are
/// unassigned first, as the army's own railway gun box does; guns already on the target are left out (one of them would
/// make the game reject the whole batch).
///
/// Movement (single player, it changes the game's distribution): guns keep off the front line and stay on their own rail
/// network. Candidate provinces next to the enemy (enemy-controlled or with hostile units) get a large penalty, so guns sit
/// just behind the line instead of being overrun, and guns never ship out by sea (where submarines sink them).
/// </summary>
[Plugin("com.green1052.railwayguns", "Railway Guns", "1.0.0", GameVersion = "1.19.3")]
public unsafe class RailwayGuns : Plugin
{
    private const int ViewOfCard = 0x10;
    private const int AssignedTo = 0x440, GunsOfGroup = 0x18; // CRailwayGun -> its orders group's railway gun container (group + 0x18), or 0
    private const int RecordSize = 48, HostileUnits = 12, ControllerAtWar = 25; // province records of the distribution

    [Function(CallingConventions.Microsoft)] public delegate void CardFn(nint card);
    [Function(CallingConventions.Microsoft)] public delegate byte PostFn(nint view, nint guns, nint group);
    [Function(CallingConventions.Microsoft)] public delegate void DistributeFn(nint controller, int a2, int a3);
    [Function(CallingConventions.Microsoft)] public delegate nint ScoreFn(nint ctx, nint score, int province);

    private IHook<CardFn>? _cardRightClick;
    private IHook<PostFn>? _postAssign;
    private IHook<DistributeFn>? _distribute;
    private IHook<ScoreFn>? _score;
    private delegate* unmanaged<nint, nint, nint, byte> _postUnassign;
    private long _frontPenalty;

    public override void Load()
    {
        var fieldMarshal = Config.Bind("FieldMarshal", "FieldMarshal", true,
            "Railway guns can be assigned to field marshals: right-click his card with railway guns selected. Single player: changes the game.");
        var spread = Config.Bind("Multiplayer", "SpreadOverArmies", true,
            "In multiplayer, where a field marshal can't hold railway guns: right-clicking his card with railway guns selected assigns them to his armies, spread over them.");
        _frontPenalty = Math.Max(0, Config.Bind("Movement", "FrontPenalty", 500,
            "Score taken off provinces on the front line (next to enemy-controlled provinces or hostile units); 0: off. Vanilla bonuses for being near the fighting are about 100-200. Single player.")) * Native.FixedPoint;
        var noSea = Config.Bind("Movement", "NoSeaTransfer", true, "Never ship railway guns by sea; they only move along their own rail network. Single player.");

        if (fieldMarshal && (!Multiplayer || spread)) LoadFieldMarshal();
        if (Multiplayer) return; // the rest changes the game

        if (noSea)
        {
            var setle = Sig.Find(Signatures.ConvoysOk) + 6;
            Patches.Bytes(setle, "0F 9E 44 24 3C", "C6 44 24 3C 00"); // setle [rsp+3Ch] -> mov byte [rsp+3Ch],0: never enough convoys
        }
        if (_frontPenalty > 0)
            _score = Patches.Hook<ScoreFn>(Sig.Find(Signatures.ScoreProvince), (ctx, score, province) =>
            {
                var result = _score!.OriginalFunction(ctx, score, province);
                if (IsFront(*(nint*)ctx, province)) *(long*)score -= _frontPenalty;
                return result;
            });
    }

    private void LoadFieldMarshal()
    {
        var at = Sig.Find(Signatures.CardAssign);
        var gunSelected = (delegate* unmanaged<nint, byte>)(at + 9 + *(int*)(at + 5));
        var postAssign = at + 57 + *(int*)(at + 53);
        var unassign = Sig.Find(Signatures.BoxUnassign);
        _postUnassign = (delegate* unmanaged<nint, nint, nint, byte>)(unassign + 20 + *(int*)(unassign + 16));

        // A marshal's card ignores a right-click with railway guns selected; as an army card's it gets to the assign below.
        _cardRightClick = Patches.Hook<CardFn>(Sig.Find(ArmyCard.RightClickSig), card =>
            ArmyCard.AsArmyCard(card, gunSelected(*(nint*)(card + ViewOfCard)) != 0, () => _cardRightClick!.OriginalFunction(card)));
        // Only a marshal's assign is ours; an army's goes through as the game made it.
        _postAssign = Patches.Hook<PostFn>(postAssign, (view, guns, group) =>
            group != 0 && new OrdersGroup(group).IsArmyGroup ? Assign(view, guns, group) : _postAssign!.OriginalFunction(view, guns, group));
        if (Multiplayer) return;

        // An army group has no divisions of its own, so its guns would find no positions: let it see its armies' ones.
        _distribute = Patches.Hook<DistributeFn>(Sig.Find(Signatures.Distribute), (controller, a2, a3) =>
        {
            var group = new OrdersGroup(*(nint*)controller);
            if (group.Ptr != 0 && group.IsArmyGroup) group.WithArmiesMembers(() => _distribute!.OriginalFunction(controller, a2, a3));
            else _distribute!.OriginalFunction(controller, a2, a3);
        });
    }

    private byte Assign(nint view, nint guns, nint group)
    {
        var all = Native.PdxArray(guns).ToArray();
        // Each target gets a share, in selection order.
        nint[] targets = Multiplayer ? [.. new OrdersGroup(group).Armies] : [group];
        var shares = ArmyCard.Spread(all, targets.Length);
        byte posted = 0;
        for (var t = 0; t < shares.Length; t++)
        {
            var target = targets[t];
            var share = shares[t].Where(gun => Owner(gun) != target).ToArray();
            if (share.Length == 0) continue;
            foreach (var gun in share)
            {
                var from = Owner(gun);
                if (from != 0) Native.WithPdxArray([gun], array => _postUnassign(view, array, from));
            }
            posted |= Native.WithPdxArray(share, array => _postAssign!.OriginalFunction(view, array, target));
        }
        Log.Info($"{all.Length} railway guns selected for a field marshal, assigned to {shares.Length} group(s)");
        return posted;
    }

    /// <summary>The orders group the gun is assigned to, or 0.</summary>
    private static nint Owner(nint gun)
    {
        var container = *(nint*)(gun + AssignedTo);
        return container == 0 ? 0 : container - GunsOfGroup;
    }

    /// <summary>The province or a neighbor has hostile units or an enemy controller, per the distribution's own records.</summary>
    private static bool IsFront(nint records, int province)
    {
        if (Enemy(records, province)) return true;
        var p = Game.Province(province);
        return p != 0 && Game.Neighbours(p).Any(n => Enemy(records, n));
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
