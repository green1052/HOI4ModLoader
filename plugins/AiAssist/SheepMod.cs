using System.Runtime.InteropServices;

namespace AiAssist;

/// <summary>
/// Detects Sheep Mod (the "Discord mod") by its defines file in the game's virtual file system (PhysFS, exported by hoi4.exe).
/// Mods mount after plugins load, so this is asked on the first AI update, not in Load.
/// </summary>
internal static unsafe class SheepMod
{
    private const string ExistsExport = "?PHYSFS_exists@@YAHPEBD@Z";
    private const string DefinesFile = "common/defines/lsm_defines.lua";

    private static readonly Lazy<bool> Found = new(() =>
    {
        var exists = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), ExistsExport);
        var path = Marshal.StringToCoTaskMemUTF8(DefinesFile);
        try { return exists(path) != 0; }
        finally { Marshal.FreeCoTaskMem(path); }
    });

    /// <summary>Whether Sheep Mod is in the running game.</summary>
    public static bool Active => Found.Value;
}
