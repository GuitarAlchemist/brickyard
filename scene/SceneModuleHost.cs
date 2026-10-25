using Delta.SceneModules;
using System.Diagnostics;

internal sealed record ModulePresentation(Version Version, Version? Available, string Language, string? Error, string ActiveSlot = "blue", Version? Retired = null);

// One trusted local code module, owned by the render thread. A click queues an exact, hashed offer; only Advance
// may install it at a frame boundary. The resource port is the Vulkan boundary, exercised without a GPU in tests.
internal sealed class SceneModuleHost<TSurface> : IDisposable where TSurface : class, IDisposable
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly string availablePath;
    private readonly Func<ModuleVisual, ModulePresentation, TSurface> createSurface;
    private readonly Action waitForGpu;
    private LoadedSceneModule active;
    private TSurface surface;
    private ModuleOffer? requested;
    private string key = "";
    private double nextPoll;
    private bool disposed;
    private long requestedAt;

    public SceneModuleHost(string initialPath, string availablePath, string language,
        Func<ModuleVisual, ModulePresentation, TSurface> createSurface, Action waitForGpu)
    {
        this.availablePath = availablePath;
        this.createSurface = createSurface;
        this.waitForGpu = waitForGpu;
        active = LoadedSceneModule.Load(LoadedSceneModule.Probe(initialPath));
        try
        {
            active.Update(new(language));
            surface = createSurface(active.Render(), new(active.Version, null, language, null));
            CreatedSurfaces = 1;
            key = Key(language);
        }
        catch { active.Dispose(); throw; }
    }

    public Version Version => active.Version;
    public string CodeHash => active.Sha256;
    public string ContextId => active.ContextId;
    public TSurface Surface => surface;
    public ModuleOffer? Available { get; private set; }
    public string? Error { get; private set; }
    public int CreatedSurfaces { get; private set; }
    public int DisposedSurfaces { get; private set; }
    public WeakReference? LastRetiredCode { get; private set; }
    public string ActiveSlot { get; private set; } = "blue";
    public Version? RetiredVersion { get; private set; }
    public long LastRequestTimestamp { get; private set; }
    public double LastSwapMilliseconds { get; private set; }
    public double LastPreparationMilliseconds { get; private set; }

    public void PollAvailable(double now)
    {
        CheckThread();
        if (now < nextPoll) return;
        nextPoll = now + 1;
        try
        {
            var newest = File.Exists(availablePath) ? LoadedSceneModule.Probe(availablePath) : null;
            ModuleOffer? previous = null;
            if (!(newest?.Version > active.Version) && RetiredVersion is { } retired)
            {
                var retained = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(availablePath))!)!, "retained");
                var index = Path.Combine(retained, "versions.json");
                if (File.Exists(index))
                {
                    if (new FileInfo(index).Length > 4096) throw new InvalidDataException("Retained module index is too large.");
                    Dictionary<string, string>? versions;
                    try { versions = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(index)); }
                    catch (System.Text.Json.JsonException e) { throw new InvalidDataException("Invalid retained module index.", e); }
                    if (versions is not null && versions.Count <= 16 && versions.TryGetValue(retired.ToString(), out var hash))
                    {
                        if (string.IsNullOrWhiteSpace(hash)) throw new InvalidDataException("Missing retained module hash.");
                        hash = hash.ToUpperInvariant();
                        var validHash = hash.Length == 64;
                        foreach (var c in hash) if (!(c is >= '0' and <= '9' or >= 'A' and <= 'F')) validHash = false;
                        if (!validHash) throw new InvalidDataException("Invalid retained module hash.");
                        previous = LoadedSceneModule.Probe(Path.Combine(retained, hash + ".dll"));
                        if (previous.Version != retired || previous.Sha256 != hash) throw new InvalidDataException("Retained image/version mismatch.");
                    }
                }
            }
            var offer = newest?.Version > active.Version ? newest : previous;
            if (offer is not null && Available?.Sha256 != offer.Sha256)
            {
                Error = null;
                Console.WriteLine($"MODULE local offer v{offer.Version.ToString(2)} {(offer.Version < active.Version ? "revert" : "candidate")} available; active v{active.Version.ToString(2)}; pid {Environment.ProcessId}; sha256 {offer.Sha256}");
            }
            Available = offer is not null && offer.Version != active.Version ? offer : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException)
        {
            Available = null;
            Error = "candidate-invalid-image";
        }
    }

    public bool RequestReplacement()
    {
        CheckThread();
        if (Available is null || requested is not null) return false;
        requested = Available;
        requestedAt = Stopwatch.GetTimestamp();
        return true;
    }

    public void Advance(string language)
    {
        CheckThread();
        if (requested is { } offer)
        {
            requested = null;
            if (!Replace(offer, language)) return;
        }
        active.Update(new(language));
        var nextKey = Key(language);
        if (nextKey == key) return;
        var next = createSurface(active.Render(), new(active.Version, Available?.Version, language, Error, ActiveSlot, RetiredVersion));
        CreatedSurfaces++;
        try { waitForGpu(); }
        catch { next.Dispose(); DisposedSurfaces++; throw; }
        var previous = surface;
        surface = next;
        key = nextKey;
        previous.Dispose();
        DisposedSurfaces++;
    }

    private bool Replace(ModuleOffer offer, string language)
    {
        LoadedSceneModule? next = null;
        TSurface? nextSurface = null;
        var stageStarted = Stopwatch.GetTimestamp();
        var prepared = stageStarted;
        var nextSlot = ActiveSlot == "blue" ? "green" : "blue";
        try
        {
            if (offer.Version == active.Version) throw new InvalidDataException("The requested code is already active.");
            if (offer.Version < active.Version)
            {
                if (offer.Version != RetiredVersion) throw new InvalidDataException("The requested code is not the previous version.");
                var retained = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(availablePath))!)!, "retained");
                var index = Path.Combine(retained, "versions.json");
                if (!File.Exists(index) || new FileInfo(index).Length > 4096) throw new InvalidDataException("Missing or invalid retained module index.");
                var versions = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(index));
                if (versions is null || !versions.TryGetValue(offer.Version.ToString(), out var hash) || !string.Equals(hash, offer.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(offer.Path, Path.Combine(retained, offer.Sha256 + ".dll"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The return action does not match the verified retained image.");
            }
            next = LoadedSceneModule.Load(offer);
            next.Update(new(language));
            nextSurface = createSurface(next.Render(), new(next.Version, null, language, null, nextSlot, active.Version));
            CreatedSurfaces++;
            prepared = Stopwatch.GetTimestamp();
            waitForGpu(); // old command buffers can still name the old native buffers until this completes
        }
        catch (Exception error)
        {
            try { if (nextSurface is not null) { nextSurface.Dispose(); DisposedSurfaces++; } }
            finally { next?.Dispose(); }
            Error = "candidate-rejected";
            Console.Error.WriteLine($"MODULE gaia replacement refused ({error.GetType().Name}); keeping {active.Version.ToString(2)}");
            return false;
        }

        var previousModule = active;
        var previousSurface = surface;
        active = next!;
        surface = nextSurface!;
        ActiveSlot = nextSlot;
        RetiredVersion = previousModule.Version;
        Available = null;
        Error = null;
        key = Key(language);
        LastRetiredCode = previousModule.ContextReference;
        try { previousSurface.Dispose(); DisposedSurfaces++; }
        finally { previousModule.Dispose(); }
        LastRequestTimestamp = requestedAt;
        LastSwapMilliseconds = Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds;
        LastPreparationMilliseconds = Stopwatch.GetElapsedTime(stageStarted, prepared).TotalMilliseconds;
        Console.WriteLine($"MODULE gaia code replaced: {previousModule.Version.ToString(2)} -> {active.Version.ToString(2)}; active slot {ActiveSlot}; pid {Environment.ProcessId}; sha256 {active.Sha256}; surfaces {CreatedSurfaces} created/{DisposedSurfaces} disposed; old code unload requested; click-to-active {LastSwapMilliseconds:F2} ms, preparation {LastPreparationMilliseconds:F2} ms (monotonic)");
        return true;
    }

    private string Key(string language) => $"{language}|{Available?.Sha256}|{Error}";

    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Scene modules belong to the render thread.");
    }

    public void Dispose()
    {
        if (disposed) return;
        CheckThread();
        waitForGpu();
        try { surface.Dispose(); DisposedSurfaces++; }
        finally { active.Dispose(); disposed = true; }
    }
}
