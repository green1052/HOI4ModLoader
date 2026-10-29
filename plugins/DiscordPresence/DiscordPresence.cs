using System.IO.Pipes;
using System.Text;
using System.Text.Json;
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
[Plugin("com.green1052.discordpresence", "Discord Rich Presence", "1.0.0", GameVersion = "1.19.4.1")]
public unsafe class DiscordPresence : Plugin
{
    private const string Image = "https://cdn.cloudflare.steamstatic.com/steam/apps/394360/header.jpg";
    private const int Handshake = 0, Frame = 1;

    private static readonly int[] MonthDays = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    private const int NameOfCountry = 0x10; // std::string: the name the game shows, for its current ideology ("독일국")
    private const int HumanControllersOfGameState = 0x388; // CPdxArray<sbyte> by tag: players on each country (gamestate.cpp)

    private string _appId = "";
    private long _since;      // when this game (or the main menu) began: Discord counts the time from it
    private int _generation = -1;
    private int _tagNames;
    private volatile string? _wanted;   // the activity the main thread wants shown (JSON)
    private long _lastTick;             // when it last said so (TickCount64)
    private Thread? _worker;
    private string? _lastProblem; // logged once until it changes

    public override void Load()
    {
        _appId = Config.Bind("Discord", "ApplicationId", "1562707563143761940",
            "The Discord application whose name Discord shows as the game: the project's by default, or your own (Discord Developer Portal -> New Application -> Application ID). Empty: off.").Trim();
        if (_appId == "")
        {
            Log.Info("no ApplicationId set: nothing shown (see the plugin's settings)");
            return;
        }
        _tagNames = *(int*)(Sig.Find(Signatures.TagNames) + 17);
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
        if (Game.Generation != _generation) // a save loaded, a new game, another country, back to the menu
            (_generation, _since) = (Game.Generation, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var country = Game.HasStarted ? Game.Country(Game.ControlledTag) : 0;
        if (country == 0)
            (details, state) = (Loc.Get("com.green1052.discordpresence.Menu", "In the main menu"), "");
        else
        {
            details = CountryName(country, Game.ControlledTag);
            state = Date(Game.Hour) + (Game.IsMultiplayer ? " · " + Loc.Format("com.green1052.discordpresence.Players", "Multiplayer, {0} players", Players()) : "");
        }
        return JsonSerializer.Serialize(new
        {
            details,
            state = state == "" ? null : state,
            timestamps = new { start = _since },
            assets = new { large_image = Image, large_text = "Hearts of Iron IV" },
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    /// <summary>Players with a country: lockstep game state, the same for the host and every client. Observers aren't counted.</summary>
    private static int Players()
    {
        var players = 0;
        foreach (var count in Native.PdxArray<sbyte>(Game.State + HumanControllersOfGameState)) players += Math.Max((int)count, 0);
        return players;
    }

    /// <summary>"1936.1.1": the clock counts hours in 365-day years from year -5000.</summary>
    private static string Date(int hour)
    {
        var day = hour / 24 % 365;
        var month = 0;
        while (day >= MonthDays[month]) day -= MonthDays[month++];
        return $"{Game.Year}.{month + 1}.{day + 1}";
    }

    /// <summary>The country's name as the game shows it (in its language, for its ideology), else its tag.</summary>
    private string CountryName(nint country, int tag)
    {
        var name = Native.Utf8(Native.MsvcString(country + NameOfCountry));
        if (name != "") return name;
        var tags = Game.State + _tagNames; // CPdxArray of 32-byte std::strings
        return (uint)tag < (uint)Native.PdxArray(tags).Length ? Native.Utf8(Native.MsvcString(*(nint*)tags + 32 * tag)) : "";
    }

    // ---- Discord IPC: frames of {int32 opcode, int32 length, UTF-8 JSON} ----

    private void Run()
    {
        NamedPipeClientStream? pipe = null;
        string? shown = null;
        long retryAt = 0, sentAt = 0;
        try
        {
            while (Environment.TickCount64 - Interlocked.Read(ref _lastTick) < 60_000)
            {
                var want = _wanted;
                // Resent every minute too: a write is the only way a Discord restart is noticed (the menu's text never changes).
                if ((want != shown || Environment.TickCount64 - sentAt > 60_000) && Environment.TickCount64 >= retryAt)
                {
                    try
                    {
                        pipe ??= Connect();
                        Send(pipe, Frame, $$"""{"cmd":"SET_ACTIVITY","args":{"pid":{{Environment.ProcessId}},"activity":{{want}}},"nonce":"{{Guid.NewGuid()}}"}""");
                        if (Receive(pipe) is var reply && reply.Contains("\"evt\":\"ERROR\"")) Problem($"Discord refused the status: {reply}");
                        if (shown == null) Problem("showing the status in Discord"); // logged once per connection
                        (shown, sentAt) = (want, Environment.TickCount64);
                    }
                    catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
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
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) // access denied: Discord runs as administrator
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
