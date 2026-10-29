using Delta.SceneModules;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;

// One explicitly selected trusted local Arcade module; no timers, threads, IO commands or Gaia impersonation.
internal sealed class ScreenModuleSession : IDisposable
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private ScreenCodeContext? context;
    private IScreenModule? module;
    private double pending;
    public ScreenModuleSession(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("Invalid screen module size.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != info.Length) throw new InvalidDataException("Screen module changed while reading.");
        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            if (!pe.HasMetadata) throw new InvalidDataException("Not a managed screen module.");
            var metadata = pe.GetMetadataReader();
            var assembly = metadata.GetAssemblyDefinition();
            if (metadata.GetString(assembly.Name) != "ArcadePong.Module") throw new InvalidDataException("Unexpected screen assembly identity.");
            Version = assembly.Version;
        }
        CodeHash = Convert.ToHexString(SHA256.HashData(bytes));
        context = new ScreenCodeContext();
        CodeContextReference = new WeakReference(context);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(bytes));
            module = Activator.CreateInstance(assembly.GetType("Delta.ArcadePong.PongScreenModule", true)!) as IScreenModule
                ?? throw new InvalidDataException("Missing screen interface.");
            if (module.Id != "arcade-pong") throw new InvalidDataException("Unexpected screen ID.");
            module.Initialize();
            Frame = module.Render(); Frame.Validate();
        }
        catch { Dispose(); throw; }
    }
    public string Id => "arcade-pong";
    public Version Version { get; }
    public string CodeHash { get; }
    public WeakReference CodeContextReference { get; }
    public bool Paused { get; set; }
    public ScreenFrame Frame { get; private set; }
    public void Advance(double elapsedSeconds)
    {
        Check();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        module!.Update(new(elapsedSeconds, Paused));
        pending += Math.Min(elapsedSeconds, 0.25);
        var due = (int)Math.Floor((pending + 1e-10) / (1.0 / 30));
        if (due == 0) return;
        pending = Math.Max(0, pending - due * (1.0 / 30));
        var next = module.Render(); next.Validate(); Frame = next;
    }
    public void Reset() { Check(); module!.Reset(); Paused = false; pending = 0; var next = module.Render(); next.Validate(); Frame = next; }
    private void Check() { ObjectDisposedException.ThrowIf(module is null, this); if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Screen is owned by the render thread."); }
    public void Dispose()
    {
        var retired = context;
        if (retired is null) return;
        try { module?.Dispose(); }
        finally { module = null; Frame = default; context = null; retired.Unload(); }
    }
    private sealed class ScreenCodeContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name == typeof(IScreenModule).Assembly.GetName().Name ? typeof(IScreenModule).Assembly : null;
    }
}
