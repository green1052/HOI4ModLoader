using HOI4ModLoader;

namespace NoTelemetry;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// Telemetry: submit a finished game event (builder). Every gameplay telemetry event (about 150 builders) ends here; it
    /// is the only path to PDX::SDK::Api::QueueAndSendTelemetry. The caller destroys the builder afterwards.
    /// </summary>
    [Signature] public const string SubmitEvents = "48 89 5C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 40 0F 29 74 24 30 48 8B F9 33 DB 89 9C 24 88 00 00 00 E8 ? ? ? ? 48 8B 90 58 03 00 00 4C 8B 62 50";
}

/// <summary>
/// Stops the game's telemetry: gameplay events are dropped instead of being sent to Paradox, whatever the in-game
/// telemetry option says. Nothing else changes (account login, multiplayer, mods, crash reports), so it works in
/// multiplayer too.
/// </summary>
[Plugin("com.green1052.notelemetry", "No Telemetry", "1.0.0", GameVersion = "1.19.3")]
public class NoTelemetry : Plugin
{
    public override void Load()
    {
        // ponytail: the PDX SDK's own start_game/exit_game events still go out; hook PDX::SDK::Api's constructor and call
        // Config::SetAutoTelemetryEnabled(false) if they matter.
        Patches.Bytes(Sig.Find(Signatures.SubmitEvents), "48 89 5C 24 08", "C3 90 90 90 90"); // return at once
    }
}
