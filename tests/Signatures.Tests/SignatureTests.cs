using System.Reflection;
using HOI4ModLoader;

namespace Signatures.Tests;

public class SignatureTests
{
    static readonly string GameDir = typeof(SignatureTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "HOI4Dir").Value!;

    static readonly Lazy<Image> Game = new(() => Image.Load(Path.Combine(GameDir, "hoi4.exe")));

    /// <summary>Every `[Signature] const string` in this folder's assemblies (loader + every plugin).</summary>
    public static TheoryData<string, string> All()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        {
            Type?[] types;
            try { types = Assembly.LoadFrom(file).GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; } // mod classes reference Reloaded assemblies that only exist in-game
            catch (BadImageFormatException) { continue; }
            foreach (var field in types.OfType<Type>().SelectMany(t => t.GetFields()))
                if (field.GetCustomAttribute<SignatureAttribute>() != null)
                    data.Add(field.DeclaringType!.Assembly.GetName().Name!, $"{field.DeclaringType.FullName}.{field.Name}");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(All))]
    public void MatchesInstalledGame(string assembly, string name)
    {
        var type = Assembly.Load(assembly).GetType(name[..name.LastIndexOf('.')])!;
        var field = type.GetField(name[(name.LastIndexOf('.') + 1)..])!;
        var sig = field.GetCustomAttribute<SignatureAttribute>()!;
        Game.Value.FindAll((string)field.GetRawConstantValue()!, sig.Count, sig.Anchor); // throws with the match count on failure
    }

    [Fact]
    public void FoundSignatures() => Assert.True(All().Count >= 10, $"only {All().Count} signatures discovered");
}
