using System.Reflection;
using HOI4ModLoader;

namespace Signatures.Tests;

public class SignatureTests
{
    static readonly string GameDir = typeof(SignatureTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "HOI4Dir").Value!;

    static readonly Lazy<Image> Game = new(() => Image.Load(Path.Combine(GameDir, "hoi4.exe")));

    /// <summary>Every `[Signature]` / `[VTable]` const in this folder's assemblies (loader + every plugin), as "assembly|type|field".</summary>
    public static TheoryData<string> All()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        {
            Type?[] types;
            try { types = Assembly.LoadFrom(file).GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; } // plugin classes reference types that only resolve in-game
            catch (BadImageFormatException) { continue; }
            foreach (var field in types.OfType<Type>().SelectMany(t => t.GetFields()))
                if (field.IsDefined(typeof(SignatureAttribute)) || field.IsDefined(typeof(VTableAttribute)))
                    data.Add($"{field.DeclaringType!.Assembly.GetName().Name}|{field.DeclaringType.FullName}|{field.Name}");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(All))]
    public void MatchesInstalledGame(string id)
    {
        var parts = id.Split('|');
        var field = Assembly.Load(parts[0]).GetType(parts[1])!.GetField(parts[2])!;
        var value = (string)field.GetRawConstantValue()!;
        if (field.GetCustomAttribute<SignatureAttribute>() is { } sig)
            Game.Value.FindAll(value, sig.Count, sig.Anchor); // throws with the match count on failure
        else
            Game.Value.VTable(value);
    }

    [Fact]
    public void FoundSignatures() => Assert.True(All().Count >= 10, $"only {All().Count} signatures discovered");
}
