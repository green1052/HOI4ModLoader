using System.Runtime.InteropServices;

namespace HOI4ModLoader;

/// <summary>
/// Game commands (CCommand subclasses), the way the UI issues player actions. Going through commands keeps
/// multiplayer in sync: the session stamps and distributes them. Send them from the main thread (<see cref="MainThread"/>).
/// </summary>
public static unsafe class Commands
{
    /// <summary>CSession::PostCommand(session, command, force) (clausewitz session.cpp).</summary>
    [Signature] public const string PostSig = "48 89 5C 24 ? 57 48 81 EC 30 01 00 00 48 8B FA 48 8B D9 45 84 C0";

    /// <summary>A UI helper that sends a command: `mov rcx,[rip+SessionHolder]; mov rax,[rcx]; call [rax+88h]` gets the session.</summary>
    [Signature] public const string SessionHolderSig = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 48 8B FA 48 8B F1 48 8B 0D ? ? ? ? 48 8B 01 FF 90 88 00 00 00 48 8B D8 B9 48 00 00 00 E8";

    const int TagOffset = 40, TargetOffset = 48; // CCommand subclasses: issuing country tag, then their argument
    const int CanExecuteSlot = 9, GetSessionSlot = 17;

    static delegate* unmanaged<nint, nint, byte, byte> _post;
    static nint* _sessionHolder;
    static readonly Dictionary<string, nint> VTables = new();

    /// <summary>
    /// A new <paramref name="className"/> command (e.g. "CSetResearchCommand") of <paramref name="size"/> bytes, laid out like
    /// the game's constructors do: CCommand base, then the country tag and the target. Fill any further fields yourself.
    /// The memory is from the CRT heap, the same process heap hoi4.exe's statically linked CRT frees commands from.
    /// </summary>
    public static nint New(string className, int size, int tag, nint target)
    {
        if (!VTables.TryGetValue(className, out var vtable)) VTables[className] = vtable = Sig.VTable(className);
        var cmd = (nint)NativeMemory.AllocZeroed((nuint)size);
        *(nint*)cmd = vtable;
        *(int*)(cmd + 12) = -1;
        *(int*)(cmd + 20) = -65536;
        *(int*)(cmd + TagOffset) = tag;
        *(nint*)(cmd + TargetOffset) = target;
        return cmd;
    }

    /// <summary>The command's own validity check, the same one the game runs before executing it.</summary>
    public static bool CanExecute(nint cmd) => ((delegate* unmanaged<nint, byte>)(*(nint**)cmd)[CanExecuteSlot])(cmd) != 0;

    /// <summary>Hands the command to the session, which owns and eventually frees it.</summary>
    public static void Post(nint cmd)
    {
        if (_post == null)
        {
            var holder = Sig.Find(SessionHolderSig) + 0x15; // mov rcx, [rip+disp32]
            _sessionHolder = (nint*)(holder + 7 + *(int*)(holder + 3));
            _post = (delegate* unmanaged<nint, nint, byte, byte>)Sig.Find(PostSig);
        }
        var app = *_sessionHolder;
        _post(((delegate* unmanaged<nint, nint>)(*(nint**)app)[GetSessionSlot])(app), cmd, 0);
    }

    /// <summary>Releases a command that was never posted.</summary>
    public static void Free(nint cmd) => NativeMemory.Free((void*)cmd);

    /// <summary>Posts the command if the game would accept it; otherwise frees it.</summary>
    public static bool TryPost(nint cmd)
    {
        if (!CanExecute(cmd))
        {
            Free(cmd);
            return false;
        }
        Post(cmd);
        return true;
    }
}
