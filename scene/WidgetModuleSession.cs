using Delta.SceneModules;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;

// One trusted local widget module, named by an explicit option, owned by the render thread. While the scene runs its
// file is watched: when a build writes a new image there and it holds still for a poll, the image is loaded into a new
// collectible context beside the old one and replaces it only once it has initialized, the hot reload of a mod being
// developed. A refused image keeps the running code and is not tried again until the file changes. The image is read
// into memory, so a build can always overwrite the file. This is trusted local code, not a sandbox. Any contract of
// module-contracts: idOf reads a module's identity and start initializes it; tag starts its log lines.
internal class ModuleSession<TModule> : IDisposable where TModule : class, IDisposable
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly string path, assemblyName, typeName, id, tag;
    private readonly Func<TModule, string> idOf;
    private readonly Action<TModule> start;
    private CodeContext? context;
    private TModule? module;
    private (long Length, DateTime Written) seen, stable;
    private string? refused;
    private double nextPoll;

    public ModuleSession(string path, string assemblyName, string typeName, string id, Func<TModule, string> idOf, Action<TModule> start, string tag)
    {
        this.path = Path.GetFullPath(path);
        this.assemblyName = assemblyName;
        this.typeName = typeName;
        this.id = id;
        (this.idOf, this.start, this.tag) = (idOf, start, tag);
        seen = stable = Stamp();
        (context, module, Version, CodeHash) = Load(Image());
        CodeContextReference = new WeakReference(context);
    }

    public TModule Module => module ?? throw new ObjectDisposedException(GetType().Name);
    public Version Version { get; private set; }
    public string CodeHash { get; private set; }
    public int Reloads { get; private set; }
    public string? LastError { get; private set; }

    // The previous code's context once replaced, to show that it was collected.
    public WeakReference CodeContextReference { get; private set; }

    // Looks at the file at most once a second; true when new code replaced the running one.
    public bool Poll(double now)
    {
        Check();
        if (now < nextPoll) return false;
        nextPoll = now + 1;
        var stamp = Stamp();
        var settled = stamp == seen;
        seen = stamp;
        if (!settled || stamp == stable || stamp.Length == 0) return false;
        stable = stamp;

        byte[] bytes;
        try { bytes = Image(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return Refuse(e); }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (hash == CodeHash || hash == refused) return false;

        try
        {
            var (nextContext, nextModule, version, _) = Load(bytes);
            var previous = (context, module);
            (context, module, Version, CodeHash) = (nextContext, nextModule, version, hash);
            CodeContextReference = new WeakReference(previous.context);
            try { previous.module?.Dispose(); }
            finally { previous.context?.Unload(); }
            Reloads++;
            LastError = null;
            refused = null;
            Console.WriteLine($"{tag} {id} reloaded: v{Version} sha256 {CodeHash}; reload {Reloads}; pid {Environment.ProcessId}");
            return true;
        }
        catch (Exception e)
        {
            refused = hash;
            return Refuse(e);
        }
    }

    private bool Refuse(Exception e)
    {
        LastError = $"{e.GetType().Name}: {e.Message}";
        Console.Error.WriteLine($"{tag} {id} new image refused ({LastError}); keeping v{Version}");
        return false;
    }

    private (CodeContext, TModule, Version, string) Load(byte[] bytes)
    {
        Version version;
        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            if (!pe.HasMetadata) throw new InvalidDataException("Not a managed module.");
            var metadata = pe.GetMetadataReader();
            var assembly = metadata.GetAssemblyDefinition();
            if (metadata.GetString(assembly.Name) != assemblyName) throw new InvalidDataException("Unexpected module assembly identity.");
            version = assembly.Version;
        }

        var code = new CodeContext($"{id}-{version}-{Guid.NewGuid():N}");
        TModule? loaded = null;
        try
        {
            var assembly = code.LoadFromStream(new MemoryStream(bytes));
            loaded = Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!) as TModule
                ?? throw new InvalidDataException("The module does not implement the expected contract.");
            if (idOf(loaded) != id) throw new InvalidDataException("Unexpected module identity.");
            start(loaded);
            return (code, loaded, version, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch
        {
            try { loaded?.Dispose(); }
            finally { code.Unload(); }
            throw;
        }
    }

    private byte[] Image()
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("The module image is absent or outside the size limit.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != info.Length) throw new InvalidDataException("The image changed while reading.");
        return bytes;
    }

    private (long Length, DateTime Written) Stamp()
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (0, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (0, default); }
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(module is null, this);
        if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Modules belong to the render thread.");
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

    private sealed class CodeContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var shared = typeof(IWidgetModule).Assembly; // module-contracts, one for every contract
            return name.Name == shared.GetName().Name ? shared : null;
        }
    }
}

// A widget module (module-contracts/Widgets.cs), as the agents' office is.
internal sealed class WidgetModuleSession<TModule>(string path, string assemblyName, string typeName, string id)
    : ModuleSession<TModule>(path, assemblyName, typeName, id, m => m.Id, m => m.Initialize(), "WIDGETS") where TModule : class, IWidgetModule;
