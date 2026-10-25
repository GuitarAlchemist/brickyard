using Delta.SceneModules;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;

internal sealed record ModuleOffer(string Path, Version Version, string Sha256);

internal sealed class LoadedSceneModule : IDisposable
{
    private CodeContext? context;
    private ISceneModule? module;

    private LoadedSceneModule(CodeContext context, ISceneModule module, ModuleOffer offer)
    {
        this.context = context;
        this.module = module;
        Id = module.Id;
        Version = offer.Version;
        Sha256 = offer.Sha256;
        ContextId = context.Name!;
        ContextReference = new WeakReference(context);
    }

    public string Id { get; }
    public Version Version { get; }
    public string Sha256 { get; }
    public string ContextId { get; }
    public WeakReference ContextReference { get; }

    // Availability inspection reads PE metadata, not executable module code.
    public static ModuleOffer Probe(string path)
    {
        var bytes = Image(path);
        using var reader = new PEReader(new MemoryStream(bytes));
        if (!reader.HasMetadata) throw new InvalidDataException("The candidate is not a managed module.");
        var metadata = reader.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        if (metadata.GetString(assembly.Name) != "GaiaPresence.Module") throw new InvalidDataException("Unexpected module assembly identity.");
        return new(Path.GetFullPath(path), assembly.Version, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static LoadedSceneModule Load(ModuleOffer offer)
    {
        var bytes = Image(offer.Path);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != offer.Sha256) throw new InvalidDataException("The candidate changed after its availability was checked.");
        var context = new CodeContext($"gaia-{offer.Version}-{Guid.NewGuid():N}");
        ISceneModule? module = null;
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(bytes));
            if (assembly.GetName().Name != "GaiaPresence.Module" || assembly.GetName().Version != offer.Version)
                throw new InvalidDataException("The loaded code does not match the advertised version.");
            var type = assembly.GetType("Delta.GaiaPresence.GaiaModule", throwOnError: true)!;
            module = Activator.CreateInstance(type) as ISceneModule ?? throw new InvalidDataException("The module does not implement the shared scene contract.");
            if (module.Id != "gaia") throw new InvalidDataException("Unexpected scene module identity.");
            module.Initialize();
            return new(context, module, offer);
        }
        catch
        {
            try { module?.Dispose(); }
            finally { context.Unload(); }
            throw;
        }
    }

    public void Update(ModuleState state)
    {
        ObjectDisposedException.ThrowIf(module is null, this);
        module.Update(state);
    }

    public ModuleVisual Render()
    {
        ObjectDisposedException.ThrowIf(module is null, this);
        return module.Render();
    }

    public void Dispose()
    {
        var retired = context;
        if (retired is null) return;
        try { module?.Dispose(); }
        finally
        {
            module = null;
            context = null;
            retired.Unload();
        }
    }

    private static byte[] Image(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("The module image is absent or outside the size limit.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != info.Length) throw new InvalidDataException("The candidate changed while reading.");
        return bytes;
    }

    private sealed class CodeContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var shared = typeof(ISceneModule).Assembly;
            return name.Name == shared.GetName().Name ? shared : null;
        }
    }
}
