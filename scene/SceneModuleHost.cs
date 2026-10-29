using Delta.SceneModules;
using System.Diagnostics;

internal static class ModTimeZones
{
    public static (string Clocks, string? Dates) Format(DateTimeOffset instant)
    {
        try
        {
            var torontoZone = Resolve("America/Toronto", "Eastern Standard Time");
            var parisZone = Resolve("Europe/Paris", "Romance Standard Time");
            var toronto = TimeZoneInfo.ConvertTime(instant, torontoZone);
            var paris = TimeZoneInfo.ConvertTime(instant, parisZone);
            var torontoName = torontoZone.IsDaylightSavingTime(instant) ? "EDT" : "EST";
            var parisName = parisZone.IsDaylightSavingTime(instant) ? "CEST" : "CET";
            var clocks = $"Ottawa {toronto:HH:mm} {torontoName} | Paris {paris:HH:mm} {parisName}";
            var dates = toronto.Date != paris.Date ? $"Ottawa {toronto:yyyy-MM-dd} | Paris {paris:yyyy-MM-dd}" : null;
            return (clocks, dates);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or System.Security.SecurityException)
        {
            return ("Ottawa ? | Paris ?", null);
        }
    }

    private static TimeZoneInfo Resolve(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        // The renderer uses invariant globalization; Windows then needs its rule-based ID.
        catch (TimeZoneNotFoundException) when (OperatingSystem.IsWindows()) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }
}

internal sealed record ModulePresentation(Version Version, Version? Available, string Language, string? Error, string ActiveSlot = "blue", Version? Retired = null)
{
    public bool Validated { get; init; }
    public IReadOnlyDictionary<string, string>? Preparation { get; init; }
    public DateTimeOffset CurrentUtc { get; init; } = DateTimeOffset.UtcNow;
    public string[] PreparationLines
    {
        get
        {
            if (Preparation is not { } data) return [];
            var fr = Language == "fr";
            var known = data.TryGetValue("state", out var state) && state == "preparing";
            var next = data.TryGetValue(fr ? "next_fr" : "next_en", out var name) ? name : (fr ? "inconnu" : "unknown");
            var updated = data.TryGetValue("updated_utc", out var stamp) && DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? ModTimeZones.Format(at) : (Clocks: fr ? "inconnue" : "unknown", Dates: (string?)null);
            string[] lines = [
                known ? (fr ? "Gaia v1.2 en pr\u00e9paration" : "Gaia v1.2 preparing") : (fr ? "Gaia v1.2 : \u00e9tat inconnu" : "Gaia v1.2: state unknown"),
                known ? (fr ? "Constat\u00e9 : prototype CPU" : "Verified: CPU prototype") : (fr ? "Donn\u00e9es indisponibles" : "Data unavailable"),
                (fr ? "Prochain : " : "Next: ") + next,
                fr ? "Avancement : inconnu" : "Progress: unknown",
                fr ? "ETA prochaine d\u00e9mo : inconnue" : "Next demo ETA: unknown",
                fr ? "Derni\u00e8re mise \u00e0 jour du suivi :" : "Last tracking update:",
                updated.Clocks
            ];
            if (updated.Dates is not null) lines = [.. lines, "Dates: " + updated.Dates];
            var current = ModTimeZones.Format(CurrentUtc);
            lines = [.. lines, fr ? "Maintenant :" : "Now:", current.Clocks];
            return current.Dates is null ? lines : [.. lines, "Dates: " + current.Dates];
        }
    }
    public string ActiveLabel => $"v{Version.ToString(2)} active - {(Language == "fr" ? (Validated ? "Valid\u00e9e" : "Valider") : (Validated ? "Validated" : "Validate"))}";
}

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
    private HashSet<string>? validatedImages;
    private Dictionary<string, string>? preparationData;
    private string? preparationSource;
    private int preparationRevision;

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

    public bool IsValidated => validatedImages?.Contains(active.Sha256) == true;
    public IReadOnlyDictionary<string, string>? Preparation => preparationData;
    public bool RequestValidation(string expectedCodeHash)
    {
        CheckThread();
        if (requested is not null || expectedCodeHash != active.Sha256 || IsValidated) return false;
        (validatedImages ??= new(StringComparer.Ordinal)).Add(active.Sha256);
        Console.WriteLine($"MODULE gaia validation recorded: v{active.Version.ToString(2)}; sha256 {active.Sha256}; pid {Environment.ProcessId}; utc {DateTimeOffset.UtcNow:O}");
        return true;
    }

    public void PollAvailable(double now)
    {
        CheckThread();
        if (now < nextPoll) return;
        nextPoll = now + 1;
        PollPreparation();
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

    public void Advance(string language) => Advance(language, DateTimeOffset.UtcNow);

    // Use one sampled instant for the minute cache key and the displayed clocks.
    public void Advance(string language, DateTimeOffset currentUtc)
    {
        CheckThread();
        if (requested is { } offer)
        {
            requested = null;
            if (!Replace(offer, language)) return;
        }
        active.Update(new(language));
        var nextKey = Key(language, currentUtc);
        if (nextKey == key) return;
        var next = createSurface(active.Render(), new(active.Version, Available?.Version, language, Error, ActiveSlot, RetiredVersion) { Validated = IsValidated, Preparation = preparationData, CurrentUtc = currentUtc });
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
            nextSurface = createSurface(next.Render(), new(next.Version, null, language, null, nextSlot, active.Version) { Validated = validatedImages?.Contains(next.Sha256) == true, Preparation = preparationData });
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

    private string Key(string language) => Key(language, DateTimeOffset.UtcNow);

    private string Key(string language, DateTimeOffset currentUtc) =>
        $"{language}|{Available?.Sha256}|{Error}|{IsValidated}|{preparationRevision}|ottawa-paris-clock-v2|{(preparationData is null ? 0 : currentUtc.UtcTicks / TimeSpan.TicksPerMinute)}";

    private void PollPreparation()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(availablePath))!)!;
        var path = Path.Combine(root, "preparation.json");
        try
        {
            if (!File.Exists(path))
            {
                if (preparationData is not null) { preparationData = null; preparationSource = null; preparationRevision++; }
                return;
            }
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Preparation data exceeds its budget.");
            var json = File.ReadAllText(path);
            if (json == preparationSource) return;
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data is null || data.Count > 12 || !data.TryGetValue("version", out var version) || version != "1.2" ||
                !data.TryGetValue("state", out var state) || state != "preparing") throw new InvalidDataException("Unknown preparation schema.");
            foreach (var pair in data)
                if (pair.Key.Length > 32 || pair.Value is null || pair.Value.Length > 128 || pair.Value.Any(char.IsControl))
                    throw new InvalidDataException("Invalid preparation text.");
            preparationData = data;
            preparationSource = json;
            preparationRevision++;
            Console.WriteLine($"MOD gaia preparation metadata updated; version 1.2; not an activation offer; pid {Environment.ProcessId}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (preparationSource == "unavailable") return;
            preparationData = new() { ["state"] = "unknown" };
            preparationSource = "unavailable";
            preparationRevision++;
            Console.Error.WriteLine($"MOD gaia preparation unavailable ({e.GetType().Name}); activation unchanged");
        }
    }

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
