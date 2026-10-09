using System.Security.Cryptography;
using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>
/// Mixes the loaded <see cref="PluginAttribute.AffectsGameState"/> plugins into the game checksum, so a multiplayer lobby
/// shows a checksum mismatch unless every player runs the same ones (instead of desyncing mid-game).
/// </summary>
public static class Checksum
{
    /// <summary>CChecksum::Compute(this, files, ...) (clausewitz checksum.cpp), called once at startup from gameapplication.</summary>
    [Signature] public const string ComputeSig = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 41 56 41 57 48 83 EC 50 49 8B D8";

    const int DigestOffset = 0x108; // CChecksum: std::string, 32 hex chars of an MD5

    [Function(CallingConventions.Microsoft)]
    public delegate nint ComputeFn(nint self, nint a2, nint a3);

    static IHook<ComputeFn>? _hook;

    internal static void Install(IReadOnlyList<PluginAttribute> plugins, Logger log)
    {
        if (plugins.Count == 0) return;
        var ids = string.Join(",", plugins.Select(p => $"{p.Id}@{p.Version}").Order(StringComparer.Ordinal));
        var salt = MD5.HashData(Encoding.UTF8.GetBytes(ids));
        _hook = Patches.Hook<ComputeFn>(Sig.Find(ComputeSig), (self, a2, a3) =>
        {
            var result = _hook!.OriginalFunction(self, a2, a3);
            var text = Native.MsvcString(self + DigestOffset);
            if (text.Length != 32) return result;
            var digest = Convert.FromHexString(Native.Utf8(text));
            for (int i = 0; i < 16; i++) digest[i] ^= salt[i];
            Encoding.ASCII.GetBytes(Convert.ToHexStringLower(digest), text);
            log.Info($"checksum includes {ids}: {Native.Utf8(text)}");
            return result;
        });
    }
}
