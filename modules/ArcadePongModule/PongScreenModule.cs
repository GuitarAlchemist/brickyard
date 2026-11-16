using Delta.SceneModules;

namespace Delta.ArcadePong;

public sealed class PongScreenModule : IScreenModule
{
    private readonly PongSimulation game = new();
    private readonly PongRasterizer raster = new();
    private PongFrame drawn;
    private ulong revision;
    private bool ready, disposed, paused;
    public string Id => "arcade-pong";
    public void Initialize() { ObjectDisposedException.ThrowIf(disposed, this); if (ready) throw new InvalidOperationException("Already initialized"); ready = true; }
    private void Check() { ObjectDisposedException.ThrowIf(disposed, this); if (!ready) throw new InvalidOperationException("Not initialized"); }
    public void Update(ScreenModuleState state)
    {
        Check();
        if (!double.IsFinite(state.ElapsedSeconds) || state.ElapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(state));
        if (paused != state.Paused) { paused = state.Paused; game.SetPaused(paused); }
        game.Advance(state.ElapsedSeconds);
    }
    public ScreenFrame Render()
    {
        Check();
        var frame = game.Frame;
        if (revision == 0 || frame != drawn) { raster.Draw(frame); drawn = frame; if (++revision == 0) revision = 1; }
        return new(PongRasterizer.Width, PongRasterizer.Height, PongRasterizer.Width * 4, raster.Pixels, revision);
    }
    public void Reset() { Check(); game.Reset(); paused = false; }
    public void Dispose() { disposed = true; }
}
