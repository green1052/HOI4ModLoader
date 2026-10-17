namespace HOI4ModLoader;

/// <summary>
/// Sends the game's own commands, the same objects the UI makes, so other players' games run them as vanilla. Posting is
/// main thread only (<see cref="MainThread"/>); New, CanExecute, Free and ClearWay are safe on AI worker threads.
/// </summary>
public static unsafe class Commands
{
    /// <summary>CSession::PostCommand(session, command, force) (clausewitz session.cpp).</summary>
    [Signature] public const string PostSig = "48 89 5C 24 ? 57 48 81 EC 30 01 00 00 48 8B FA 48 8B D9 45 84 C0";

    /// <summary>A UI helper that sends a command: `mov rcx,[rip+SessionHolder]; mov rax,[rcx]; call [rax+88h]` gets the session.</summary>
    [Signature] public const string SessionHolderSig = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 48 8B FA 48 8B F1 48 8B 0D ? ? ? ? 48 8B 01 FF 90 88 00 00 00 48 8B D8 B9 48 00 00 00 E8";

    private const int TagOffset = 40, TargetOffset = 48; // CCommand subclasses: issuing country tag, then their argument
    private const int CanExecuteSlot = 9, GetSessionSlot = 17;

    private static delegate* unmanaged<nint, nint, byte, byte> _post;
    private static nint* _sessionHolder;

    /// <summary>
    /// A new <paramref name="className"/> command (e.g. "CSetResearchCommand") of <paramref name="size"/> bytes on the game's
    /// heap: CCommand base, country tag and target, as the game's constructors set them. Fill any other fields yourself.
    /// </summary>
    public static nint New(string className, int size, int tag, nint target)
    {
        var vtable = Sig.VTable(className);
        var cmd = Allocator.AllocZeroed(size);
        *(nint*)cmd = vtable;
        *(int*)(cmd + 12) = -1;
        *(int*)(cmd + 20) = -65536;
        *(int*)(cmd + TagOffset) = tag;
        *(nint*)(cmd + TargetOffset) = target;
        return cmd;
    }

    /// <summary>
    /// The command's own validity check, as the game runs it before executing. The reason argument (std::string*) is null.
    /// </summary>
    public static bool CanExecute(nint cmd) => ((delegate* unmanaged<nint, nint, byte>)(*(nint**)cmd)[CanExecuteSlot])(cmd, 0) != 0;

    /// <summary>Hands the command to the session, which owns and eventually frees it. False (command freed): no session.</summary>
    public static bool Post(nint cmd) => Post(cmd, false);

    /// <summary>
    /// <see cref="Post(nint)"/>; <paramref name="force"/> posts as the AI does (PostAICommand), so the session takes it even
    /// where it would drop a player's command. Use it for commands on an AI country's behalf (single player).
    /// </summary>
    public static bool Post(nint cmd, bool force)
    {
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment (no ??= for function pointers)
        if (_post == null) _post = (delegate* unmanaged<nint, nint, byte, byte>)Sig.Find(PostSig);
        var session = Session;
        if (session == 0) // loading or shutting down: PostCommand would dereference it
        {
            Free(cmd);
            return false;
        }
        _post(session, cmd, force ? (byte)1 : (byte)0);
        return true;
    }

    /// <summary>The game's CSession (clausewitz session.cpp), or 0 before the game application exists.</summary>
    internal static nint Session
    {
        get
        {
            if (_sessionHolder == null)
            {
                var holder = Sig.Find(SessionHolderSig) + 0x15; // mov rcx, [rip+disp32]
                _sessionHolder = (nint*)(holder + 7 + *(int*)(holder + 3));
            }
            var app = *_sessionHolder;
            return app == 0 ? 0 : ((delegate* unmanaged<nint, nint>)(*(nint**)app)[GetSessionSlot])(app);
        }
    }

    /// <summary>Frees a command that was never posted, through its deleting destructor.</summary>
    public static void Free(nint cmd) => ((delegate* unmanaged<nint, int, void>)(*(nint**)cmd)[0])(cmd, 1);

    /// <summary>Posts the command if the game would accept it; otherwise frees it.</summary>
    public static bool TryPost(nint cmd) => TryPost(cmd, false);

    /// <summary><see cref="TryPost(nint)"/>, posting with <paramref name="force"/> (see <see cref="Post(nint, bool)"/>).</summary>
    public static bool TryPost(nint cmd, bool force)
    {
        if (!CanExecute(cmd))
        {
            Free(cmd);
            return false;
        }
        return Post(cmd, force);
    }

    /// <summary>
    /// CMoveCommand ctor (mem[0x88], CArmy* unit, int province, bool clearQueue, int moveType, ...): the battle plan UI's plain
    /// move. A move out of combat becomes a retreat.
    /// </summary>
    [Signature] public const string MoveSig = "48 89 4C 24 ? 53 48 83 EC 60 48 8B D9 C6 41 08 00 C7 41 0C FF FF FF FF 33 C9 89 4B 10 C7 43 14 00 00 FF FF 66 89 4B 18 48 89 4B 1C 48 8D 05 ? ? ? ? 48 89 03 48 8D 4B 28";

    private const int MoveSize = 0x88;
    private static delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint> _move;

    /// <summary>
    /// Moves <paramref name="unit"/> to <paramref name="province"/>, clearing its queue, if the game accepts it and
    /// <see cref="ClearWay"/> holds. The game's own path goes straight through enemy divisions and attacks them.
    /// </summary>
    public static bool Move(nint unit, int province, bool force = false)
    {
        if (!ClearWay(unit, province)) return false;
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
        if (_move == null) _move = (delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint>)Sig.Find(MoveSig);
        var cmd = Allocator.AllocZeroed(MoveSize);
        _move(cmd, unit, province, 1, 2, 0, 0, 0, 0, 0);
        return TryPost(cmd, force);
    }

    private const int MaxWaySearch = 4000; // provinces looked at before giving up (and moving anyway)

    /// <summary>
    /// Whether a land path reaches <paramref name="target"/> without passing enemy divisions (the target itself may hold
    /// them). Also true when the search gives up, leaving long walks to the game.
    /// </summary>
    public static bool ClearWay(nint unit, int target)
    {
        var from = Division.Location(unit);
        var tag = Division.Commander(unit);
        var country = Game.Country(tag);
        if (from == 0 || country == 0 || Game.Id(from) == target) return true;
        var seen = new HashSet<int> { Game.Id(from) };
        var queue = new Queue<nint>();
        queue.Enqueue(from);
        while (queue.Count > 0 && seen.Count < MaxWaySearch)
            foreach (var id in Game.Neighbours(queue.Dequeue()))
            {
                if (!seen.Add(id)) continue;
                if (id == target) return true;
                var p = Game.Province(id);
                if (p == 0 || Province.Controller(p) == 0 || Hostile(country, p)) continue; // sea, wasteland, or enemy troops on the way
                queue.Enqueue(p);
            }
        return queue.Count > 0;
    }

    private static bool Hostile(nint country, nint province)
    {
        foreach (var u in Province.Units(province))
            if (u != 0 && Division.IsLand(u) && Game.AtWar(country, Division.Commander(u))) return true;
        return false;
    }

    /// <summary>COrderUnassignCommand ctor (mem[0x50], CArmy* unit, COrdersGroup* group, bool ungroup, bool alsoParent).</summary>
    [Signature] public const string UnassignSig = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 48 83 EC 30 41 0F B6 F1 49 8B F8 48 8B EA 48 8B D9 C6 41 08 00";

    private const int UnassignSize = 0x50;
    private static delegate* unmanaged<nint, nint, nint, byte, byte, nint> _unassign;

    /// <summary>Takes <paramref name="unit"/> off every order of <paramref name="group"/> and its parent; it stays in the army.</summary>
    public static bool Unassign(nint unit, nint group, bool force = false)
    {
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
        if (_unassign == null) _unassign = (delegate* unmanaged<nint, nint, nint, byte, byte, nint>)Sig.Find(UnassignSig);
        var cmd = Allocator.AllocZeroed(UnassignSize);
        _unassign(cmd, unit, group, 0, 1); // a refused one is freed whole by its destructor
        return TryPost(cmd, force);
    }

    /// <summary>COrderAssignCommand ctor (mem[0x50], CPdxArray&lt;CArmy*&gt;* units, COrdersGroup* group, int orderId, bool).</summary>
    [Signature] public const string AssignSig = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 4C 24 ? 56 57 41 54 41 56 41 57 48 83 EC 30 41 8B D9 49 8B E8 4C 8B FA 4C 8B F1 0F B6 BC 24 80 00 00 00 C6 41 08 00";

    private const int AssignSize = 0x50;
    private static delegate* unmanaged<nint, nint, nint, int, byte, nint> _assign;

    /// <summary>Puts <paramref name="unit"/> on order <paramref name="orderId"/> of <paramref name="group"/> or its parent.</summary>
    public static bool Assign(nint unit, nint group, int orderId, bool force = false)
    {
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
        if (_assign == null) _assign = (delegate* unmanaged<nint, nint, nint, int, byte, nint>)Sig.Find(AssignSig);
        var cmd = Allocator.AllocZeroed(AssignSize);
        Native.WithPdxArray([unit], units => _assign(cmd, units, group, orderId, 0)); // a refused one is freed whole by its destructor
        return TryPost(cmd, force);
    }

    /// <summary>The battle plan's play button: (group CRef, order id 0 = the whole group, execute, also its armies).</summary>
    [VTable] public const string ExecuteOrder = "COrderExecuteCommand";

    // Order commands (COrderExecuteCommand, COrderDeleteCommand...): group CRef, order id, then their argument
    // (execute: a byte, then also-armies; paradrop target: the province).
    public const int OrderCommandSize = 0x38, GroupOfOrderCommand = 0x28, OrderOfOrderCommand = 0x30, ArgOfOrderCommand = 0x34, AlsoArmiesOfOrderCommand = 0x35;

    /// <summary>A new order command for <paramref name="order"/> (0: the whole group) of the group with CRef <paramref name="group"/>.</summary>
    public static nint NewOrderCommand(string className, long group, int order, int argument)
    {
        var cmd = New(className, OrderCommandSize, 0, 0);
        *(long*)(cmd + GroupOfOrderCommand) = group;
        *(int*)(cmd + OrderOfOrderCommand) = order;
        *(int*)(cmd + ArgOfOrderCommand) = argument;
        return cmd;
    }
}
