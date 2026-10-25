using System.Numerics;

namespace Delta.SceneModules;

// Shared only with trusted local modules. Native graphics/audio/network handles never cross this boundary.
public sealed record ModuleState(string Language);
public sealed record ModuleBox(Vector3 Centre, Vector3 Size, Vector3 Color, uint Material = 0);
public sealed record ModuleText(string Text, Vector3 Centre, float Width, Vector3 Color);
public sealed record ModuleVisual(IReadOnlyList<ModuleBox> Boxes, IReadOnlyList<ModuleText> Text);

public interface ISceneModule : IDisposable
{
    string Id { get; }
    void Initialize();
    void Update(ModuleState state);
    ModuleVisual Render();
}
