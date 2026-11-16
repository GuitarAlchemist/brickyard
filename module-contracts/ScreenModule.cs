namespace Delta.SceneModules;

// Borrowed RGBA8 pixels, top-left origin. The render thread copies before the next Render call.
// This screen port is independent of Gaia's boxes/text contract and exposes no native handles.
public readonly record struct ScreenFrame(int Width, int Height, int Stride, ReadOnlyMemory<byte> Rgba, ulong Revision)
{
    public const int PixelWidth = 160, PixelHeight = 96, ByteCount = PixelWidth * PixelHeight * 4;
    public void Validate()
    {
        if (Width != PixelWidth || Height != PixelHeight || Stride != PixelWidth * 4 || Rgba.Length != ByteCount || Revision == 0)
            throw new InvalidDataException("Screen frame is outside the fixed RGBA8 budget.");
    }
}

public readonly record struct ScreenModuleState(double ElapsedSeconds, bool Paused);
public interface IScreenModule : IDisposable
{
    string Id { get; }
    void Initialize();
    void Update(ScreenModuleState state);
    ScreenFrame Render();
    void Reset();
}
