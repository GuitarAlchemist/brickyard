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

    private readonly byte[] image;

    private LoadedSceneModule(CodeContext context, ISceneModule module, ModuleOffer offer, byte[] image)
    {
        this.context = context;
        this.module = module;
        this.image = image;
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
    // The bytes the code was loaded from, whose hash is Sha256: what a validation keeps, whatever the disk now holds.
    public ReadOnlySpan<byte> LoadedImage => image;

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
            return new(context, module, offer, bytes);
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

    // A module's optional triangles (V1.2's planet), null for a module without them. They are copied before they are
    // checked, so that what is checked is what is drawn even if the module's list changes afterwards.
    public ModuleTriangleVisual? RenderTriangles()
    {
        ObjectDisposedException.ThrowIf(module is null, this);
        if (module is not ISceneTriangleModule capable) return null;
        var visual = capable.RenderTriangles() ?? throw new InvalidDataException("Module triangles are missing.");
        var source = visual.Triangles ?? throw new InvalidDataException("Module triangles are missing.");
        var count = source.Count;
        if (count > ModuleTriangleValidation.MaxTriangles) throw new InvalidDataException("Module triangles exceed their budget.");
        var copy = new ModuleTriangle[count];
        for (var i = 0; i < count; i++) copy[i] = source[i];
        var image = visual.Image;
        ModuleTriangleImage? imageCopy = null;
        if (image is not null)
        {
            var uvs = image.Uvs ?? throw new InvalidDataException("Module triangle image coordinates are missing.");
            var uvCount = uvs.Count;
            if (uvCount > count) throw new InvalidDataException("More image coordinates than triangles.");
            var uvCopy = new ModuleTriangleUv[uvCount];
            for (var i = 0; i < uvCount; i++) uvCopy[i] = uvs[i];
            imageCopy = new ModuleTriangleImage(image.Name, Array.AsReadOnly(uvCopy));
        }
        var snapshot = new ModuleTriangleVisual(Array.AsReadOnly(copy)) { Image = imageCopy };
        ModuleTriangleValidation.Validate(snapshot);
        return snapshot;
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
