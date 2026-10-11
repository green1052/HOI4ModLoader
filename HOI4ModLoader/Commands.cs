namespace HOI4ModLoader;

/// <summary>
/// Sends the game's own commands (CSetResearchCommand, CAddConstructionCommand...), the same objects the UI makes when the
/// player clicks: no new command types, so other players' games run them as vanilla. Main thread only (<see cref="MainThread"/>).
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
    /// A new <paramref name="className"/> command (e.g. "CSetResearchCommand") of <paramref name="size"/> bytes, laid out like
    /// the game's constructors do: CCommand base, then the country tag and the target. Fill any further fields yourself.
    /// The memory is from the game's own heap (<see cref="Allocator"/>), which frees commands once they ran.
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
    /// The command's own validity check, the same one the game runs before executing it. Some take a std::string* for the
    /// failure reason (written when non-null); pass null like the game does.
    /// </summary>
    public static bool CanExecute(nint cmd) => ((delegate* unmanaged<nint, nint, byte>)(*(nint**)cmd)[CanExecuteSlot])(cmd, 0) != 0;

    /// <summary>Hands the command to the session, which owns and eventually frees it. False (command freed): no session.</summary>
    public static bool Post(nint cmd) => Post(cmd, false);

    /// <summary>
    /// <see cref="Post(nint)"/>; <paramref name="force"/> as the game's own AI posts (PostAICommand): the session takes it even
    /// where it would drop a player's command. Use it for commands made on an AI country's behalf (single player).
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

    /// <summary>Releases a command that was never posted, as the game does (its deleting destructor frees what it owns too).</summary>
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
    /// CMoveCommand ctor (mem[0x88], CArmy* unit, int province, bool clearQueue, int moveType, ...): a plain move, the one the
    /// battle plan UI sends; the game turns a move out of combat into a retreat.
    /// </summary>
    [Signature] public const string MoveSig = "48 89 4C 24 ? 53 48 83 EC 60 48 8B D9 C6 41 08 00 C7 41 0C FF FF FF FF 33 C9 89 4B 10 C7 43 14 00 00 FF FF 66 89 4B 18 48 89 4B 1C 48 8D 05 ? ? ? ? 48 89 03 48 8D 4B 28";

    private const int MoveSize = 0x88;
    private static delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint> _move;

    /// <summary>Moves <paramref name="unit"/> to <paramref name="province"/> (clearing its queue, as the AI moves units) if the game accepts it.</summary>
    public static bool Move(nint unit, int province, bool force = false)
    {
        // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
        if (_move == null) _move = (delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint>)Sig.Find(MoveSig);
        var cmd = Allocator.AllocZeroed(MoveSize);
        _move(cmd, unit, province, 1, 2, 0, 0, 0, 0, 0);
        return TryPost(cmd, force);
    }
}
