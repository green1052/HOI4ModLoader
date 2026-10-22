using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HOI4ModLoader;

namespace DiscordPresence;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CCountryTag::GetTag's tail: `mov rcx,[gamestate]; movsxd rax,[rbx]; shl rax,5; add rax,[rcx+TagNames]`. The gamestate's
    /// CPdxArray of std::string tag names ("---", "GER", "ENG"...), indexed by the tag int.
    /// </summary>
    [Signature] public const string TagNames = "48 8B 0D ? ? ? ? 48 63 03 48 C1 E0 05 48 03 81 ? ? ? ?";
}

/// <summary>
/// Discord Rich Presence: the country played, the game date and multiplayer, shown in the player's Discord status. Talks
/// to the Discord app over its local IPC pipe (\\.\pipe\discord-ipc-N), the same way Discord's own SDKs do; no game state
/// is touched. The pipe work runs on its own thread, which ends (and Discord clears the status) a minute after the plugin
/// stops updating it, e.g. when it is turned off or reloaded.
/// </summary>
[Plugin("com.green1052.discordpresence", "Discord Rich Presence", "1.0.0", GameVersion = "1.19.3")]
public unsafe class DiscordPresence : Plugin
{
    private const string Image = "https://cdn.cloudflare.steamstatic.com/steam/apps/394360/header.jpg";
    private const int Handshake = 0, Frame = 1;

    private static readonly int[] MonthDays = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    private const int NameOfCountry = 0x10; // std::string: the English name for its current ideology ("German Reich")

    private string _appId = "";
    private long _started;
    private int _tagNames;
    private Dictionary<string, string> _english = [], _local = []; // the game's country name localisation, key -> text
    private volatile string? _wanted;   // the activity the main thread wants shown (JSON)
    private long _lastTick;             // when it last said so (TickCount64)
    private Thread? _worker;
    private string? _lastProblem; // logged once until it changes

    public override void Load()
    {
        _appId = Config.Bind("Discord", "ApplicationId", "",
            "The Discord application whose name Discord shows as the game (Discord Developer Portal -> New Application -> Application ID).").Trim();
        if (_appId == "")
        {
            Log.Info("no ApplicationId set: nothing shown (see the plugin's settings)");
            return;
        }
        _tagNames = *(int*)(Sig.Find(Signatures.TagNames) + 17);
        _started = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUnixTimeSeconds();
        _english = CountryNames("english");
        _local = Loc.Language == "english" ? _english : CountryNames(Loc.Language);
        MainThread.Every(TimeSpan.FromSeconds(15), Tick);
    }

    private void Tick()
    {
        _wanted = Activity();
        Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
        if (_worker is { IsAlive: true }) return;
        _worker = new Thread(Run) { IsBackground = true, Name = "Discord Rich Presence" };
        _worker.Start();
    }

    /// <summary>What the status shows now, as Discord's activity object.</summary>
    private string Activity()
    {
        string details, state;
        var country = Game.HasStarted ? Game.Country(Game.ControlledTag) : 0;
        if (country == 0)
            (details, state) = (Loc.Get("com.green1052.discordpresence.Menu", "In the main menu"), "");
        else
        {
            details = CountryName(country, Game.ControlledTag);
            state = Date(Game.Hour) + (Game.IsMultiplayer ? " · " + Loc.Get("com.green1052.discordpresence.Multiplayer", "Multiplayer") : "");
        }
        return JsonSerializer.Serialize(new
        {
            details,
            state = state == "" ? null : state,
            timestamps = new { start = _started },
            assets = new { large_image = Image, large_text = "Hearts of Iron IV" },
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    /// <summary>"1936.1.1": the clock counts hours in 365-day years from year -5000.</summary>
    private static string Date(int hour)
    {
        var day = hour / 24 % 365;
        var month = 0;
        while (day >= MonthDays[month]) day -= MonthDays[month++];
        return $"{Game.Year}.{month + 1}.{day + 1}";
    }

    /// <summary>
    /// The name the game shows, in its language: the country holds its English name for its ideology ("German Reich"); the
    /// localisation key with that English text ("GER_fascism") gives the translation ("독일국"). Mods' names stay English.
    /// </summary>
    private string CountryName(nint country, int tag)
    {
        var tags = Game.State + _tagNames; // CPdxArray of 32-byte std::strings
        var text = (uint)tag < (uint)Native.PdxArray(tags).Length ? Native.Utf8(Native.MsvcString(*(nint*)tags + 32 * tag)) : "";
        var english = Native.Utf8(Native.MsvcString(country + NameOfCountry));
        foreach (var (key, value) in _english)
            if (value == english && (key == text || key.StartsWith(text + "_")) && _local.TryGetValue(key, out var local)) return local;
        return english != "" ? english : _local.GetValueOrDefault(text, text);
    }

    /// <summary>Country names (`GER_fascism: "German Reich"`) from the game's own localisation files.</summary>
    private static Dictionary<string, string> CountryNames(string language)
    {
        var names = new Dictionary<string, string>();
        foreach (var file in new[] { "countries", "countries_cosmetic" }.Select(f => Path.Combine(Loader.GameDir, "localisation", language, $"{f}_l_{language}.yml")))
            if (File.Exists(file))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"(?m)^\s*([A-Z][A-Z0-9]{2}(?:_[a-z_]+)?):\d*\s*""([^""]*)"""))
                    names.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return names;
    }

    // ---- Discord IPC: frames of {int32 opcode, int32 length, UTF-8 JSON} ----

    private void Run()
    {
        NamedPipeClientStream? pipe = null;
        string? shown = null;
        var retryAt = 0L;
        try
        {
            while (Environment.TickCount64 - Interlocked.Read(ref _lastTick) < 60_000)
            {
                var want = _wanted;
                if (want != shown && Environment.TickCount64 >= retryAt)
                {
                    try
                    {
                        pipe ??= Connect();
                        Send(pipe, Frame, $$"""{"cmd":"SET_ACTIVITY","args":{"pid":{{Environment.ProcessId}},"activity":{{want}}},"nonce":"{{Guid.NewGuid()}}"}""");
                        if (Receive(pipe) is var reply && reply.Contains("\"evt\":\"ERROR\"")) Problem($"Discord refused the status: {reply}");
                        shown = want;
                    }
                    catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
                    {
                        Problem(e.Message);
                        pipe?.Dispose(); // Discord isn't running or went away: try again later
                        pipe = null;
                        retryAt = Environment.TickCount64 + 30_000;
                    }
                }
                Thread.Sleep(1000);
            }
        }
        catch (Exception e) { Log.Error($"discord: {e.Message}"); }
        finally { pipe?.Dispose(); } // closing the pipe clears the status
    }

    private void Problem(string message)
    {
        if (message == _lastProblem) return;
        _lastProblem = message;
        Log.Info(message);
    }

    private NamedPipeClientStream Connect()
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut);
            try
            {
                pipe.Connect(100);
                Send(pipe, Handshake, $$"""{"v":1,"client_id":"{{_appId}}"}""");
                Receive(pipe); // READY
                return pipe;
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                pipe.Dispose();
            }
        }
        throw new IOException("Discord isn't running");
    }

    private static void Send(Stream pipe, int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0), opcode);
        BitConverter.TryWriteBytes(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        pipe.Write(frame);
        pipe.Flush();
    }

    private static string Receive(Stream pipe)
    {
        var header = new byte[8];
        pipe.ReadExactly(header);
        var opcode = BitConverter.ToInt32(header, 0);
        var body = new byte[BitConverter.ToInt32(header, 4)];
        pipe.ReadExactly(body);
        var text = Encoding.UTF8.GetString(body);
        if (opcode == 2) throw new IOException($"Discord closed the connection: {text}"); // CLOSE (e.g. an unknown application id)
        return text;
    }
}
