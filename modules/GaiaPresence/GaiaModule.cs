using Delta.SceneModules;
using SN = System.Numerics;

namespace Delta.GaiaPresence;

// This code lives in a separate assembly, never referenced by the renderer project. V1.1 adds an accent collar,
// so the demonstration changes executable geometry code as well as the assembly version shown by the host.
public sealed class GaiaModule : ISceneModule
{
    private readonly Dictionary<string, ModuleVisual> frames = [];
    private string language = "en";
    private bool initialized;
    public string Id => "gaia";

    public void Initialize()
    {
        if (initialized) throw new InvalidOperationException("Gaia module is already initialized.");
        foreach (var code in new[] { "en", "fr" }) frames.Add(code, Build(code));
        initialized = true;
    }

    public void Update(ModuleState state)
    {
        if (!initialized) throw new InvalidOperationException("Gaia module is not initialized.");
        language = state.Language == "fr" ? "fr" : "en";
    }

    public ModuleVisual Render() => initialized ? frames[language] : throw new InvalidOperationException("Gaia module is not initialized.");

    public void Dispose()
    {
        frames.Clear();
        initialized = false;
    }

    private static ModuleVisual Build(string code)
    {
        const float x = 0.88f, z = 0.16f;
        SN.Vector3 violet = new(0.48f, 0.23f, 0.78f), dark = new(0.055f, 0.045f, 0.08f), pale = new(0.82f, 0.72f, 0.98f);
        List<ModuleBox> boxes =
        [
            new(new(x, 0.035f, z), new(0.48f, 0.07f, 0.34f), dark),
            new(new(x, 0.09f, z), new(0.36f, 0.04f, 0.25f), violet, 1),
            new(new(x, 0.37f, z), new(0.12f, 0.54f, 0.12f), dark),
            new(new(x, 0.70f, z), new(0.30f, 0.40f, 0.17f), violet, 1),
            new(new(x, 0.70f, z + 0.09f), new(0.25f, 0.34f, 0.015f), dark),
            new(new(x, 0.56f, z + 0.105f), new(0.04f, 0.014f, 0.008f), new(0.40f)),
            new(new(x, 0.27f, z + 0.16f), new(0.86f, 0.12f, 0.025f), dark),
        ];
#if GAIA_V11
        boxes.Add(new(new(x, 0.48f, z), new(0.20f, 0.025f, 0.20f), pale, 1));
#endif
        return new(boxes,
        [
            new("G", new(x, 0.72f, z + 0.103f), 0.19f, pale),
            new(code == "fr" ? "Gaia — non raccordée" : "Gaia — disconnected", new(x, 0.27f, z + 0.177f), 0.78f, pale),
        ]);
    }
}
