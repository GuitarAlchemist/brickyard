using Delta.SceneModules;

var failures = 0;
void Check(bool ok, string message)
{
    Console.WriteLine($"MODULE TEST {(ok ? "ok" : "FAIL")} {message}");
    if (!ok) failures++;
}

try
{
    if (args.Length is not (2 or 3)) throw new ArgumentException("Expected paths to the real V1 and V1.1 module assemblies, and optionally V1.2's.");
    var offer1 = LoadedSceneModule.Probe(args[0]);
    var offer2 = LoadedSceneModule.Probe(args[1]);
    foreach (var (utc, expected) in new[] {
        ("2026-10-24T19:00:00Z", "Ottawa 15:00 EDT | Paris 21:00 CEST"),
        ("2026-10-25T00:30:00Z", "Ottawa 20:30 EDT | Paris 02:30 CEST"),
        ("2026-10-25T01:30:00Z", "Ottawa 21:30 EDT | Paris 02:30 CET"),
        ("2026-10-25T19:00:00Z", "Ottawa 15:00 EDT | Paris 20:00 CET"),
        ("2026-11-01T05:30:00Z", "Ottawa 01:30 EDT | Paris 06:30 CET"),
        ("2026-11-01T06:30:00Z", "Ottawa 01:30 EST | Paris 07:30 CET"),
        ("2026-03-08T12:00:00Z", "Ottawa 08:00 EDT | Paris 13:00 CET"),
        ("2026-03-29T01:30:00Z", "Ottawa 21:30 EDT | Paris 03:30 CEST") })
        Check(ModTimeZones.Format(DateTimeOffset.Parse(utc)).Clocks == expected, $"Ottawa and Paris apply their own DST rules at {utc}");
    Check(ModTimeZones.Format(DateTimeOffset.Parse("2026-10-25T00:30:00Z")).Dates == "Ottawa 2026-10-24 | Paris 2026-10-25" &&
        ModTimeZones.Format(DateTimeOffset.Parse("2026-10-25T19:00:00Z")).Dates is null,
        "different local days are explicit while same-day clocks stay compact");
    var preparing = new ModulePresentation(offer2.Version, null, "en", null) { Preparation = new Dictionary<string, string>
        { ["state"] = "preparing", ["next_en"] = "Isolated Vulkan planet preview", ["next_fr"] = "Apercu Vulkan isole de la planete", ["updated_utc"] = "2026-10-25T19:00:00Z" } };
    Check(preparing.PreparationLines.Contains("Gaia v1.2 preparing") && preparing.PreparationLines.Contains("Progress: unknown") &&
        preparing.PreparationLines.Contains("Next demo ETA: unknown") && preparing.PreparationLines.Contains("Last tracking update:") &&
        preparing.PreparationLines.Contains("Ottawa 15:00 EDT | Paris 20:00 CET"),
        "the preparation card names a next increment without inventing progress or an ETA");
    Check((preparing with { Language = "fr" }).PreparationLines.Contains("Gaia v1.2 en pr\u00e9paration") &&
        (preparing with { Language = "fr" }).PreparationLines.Contains("Avancement : inconnu"), "the same preparation data follows French selection");
    Check((preparing with { Version = new Version(1, 2, 0, 0), Preparation = new Dictionary<string, string>(preparing.Preparation!) { ["version"] = "1.2" } }).PreparationLines.Length == 0 &&
        (preparing with { Preparation = new Dictionary<string, string>(preparing.Preparation!) { ["version"] = "1.2" } }).PreparationLines.Length > 0,
        "the preparation card goes once the prepared version is the active one, and stays before");
    var fixedClock = preparing with { CurrentUtc = DateTimeOffset.Parse("2026-10-26T01:20:00Z") };
    Check(fixedClock.PreparationLines.Contains("Last tracking update:") &&
        fixedClock.PreparationLines.Contains("Ottawa 15:00 EDT | Paris 20:00 CET") &&
        fixedClock.PreparationLines.Contains("Now:") &&
        fixedClock.PreparationLines.Contains("Ottawa 21:20 EDT | Paris 02:20 CET") &&
        fixedClock.PreparationLines.Contains("Dates: Ottawa 2026-10-25 | Paris 2026-10-26"),
        "the fixed tracking event and the current local clocks are separate, including different calendar days");
    Check((fixedClock with { Language = "fr" }).PreparationLines.Contains("Derni\u00e8re mise \u00e0 jour du suivi :") &&
        (fixedClock with { Language = "fr" }).PreparationLines.Contains("Maintenant :"),
        "French labels distinguish the last tracking update from now");
    SceneModulePicking.SelfTest(Check);
    using var inputHost = new SceneModuleHost<TestSurface>(args[0], args[1], "en", (visual, presentation) => new TestSurface(presentation.Version, visual, []), () => { });
    Check(SceneModuleInteraction.TryClick(inputHost, new(0, 0, 100, 100), 50, 25, true) is null && !inputHost.IsValidated,
        "a simulated click cannot validate the operator's active image");
    Check(SceneModuleInteraction.TryClick(inputHost, new(0, 0, 100, 100), 50, 25, false) == "validation" && inputHost.IsValidated,
        "the input dispatcher validates the active image independently of an update offer");
    using var first = LoadedSceneModule.Load(offer1);
    using var second = LoadedSceneModule.Load(offer2);
    Check(first.Id == "gaia" && first.Version == new Version(1, 0, 0, 0), "V1 identity and version come from a loaded code assembly");
    Check(second.Id == "gaia" && second.Version == new Version(1, 1, 0, 0), "V1.1 identity and version come from a distinct loaded assembly");
    Check(first.ContextId != second.ContextId && first.Sha256 != second.Sha256, "versions have distinct code images and collectible load contexts");
    first.Update(new("fr"));
    var originalBoxes = first.Render().Boxes.Count;
    second.Update(new("fr"));
    Check(second.Render().Boxes.Count == originalBoxes + 1, "V1.1 executes changed geometry code, not just a changed version string");
    foreach (var (language, text) in new[] { ("fr", "Gaia — non raccordée"), ("en", "Gaia — disconnected"), ("fr", "Gaia — non raccordée") })
    {
        second.Update(new(language));
        Check(second.Render().Text.Any(t => t.Text == text), $"one loaded code module follows {language}");
    }
    var rejected = false;
    try { using var invalid = LoadedSceneModule.Load(offer2 with { Sha256 = new string('0', 64) }); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected && first.Render().Boxes.Count == originalBoxes, "changed candidate image is rejected and the old module remains usable");
    first.Dispose();
    var disposed = false;
    try { first.Render(); } catch (ObjectDisposedException) { disposed = true; }
    Check(disposed, "a retired module cannot execute again");
    // Keep the collection assertion outside the frame that executed the plugin: Debug JIT temporaries
    // may remain GC roots until that frame returns, even after an explicit Dispose/Unload.
    var retired = RetireCode(offer1);
    for (var i = 0; i < 3 && retired.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    Check(!retired.IsAlive, "the retired code load context is actually collectible");

    List<string> lifecycle = [];
    using var host = new SceneModuleHost<TestSurface>(args[0], args[1], "en",
        (visual, presentation) => new TestSurface(presentation.Version, visual, lifecycle), () => lifecycle.Add("gpu-idle"));
    var oldContext = host.ContextId;
    var oldSurface = host.Surface;
    Check(!host.IsValidated, "activation alone does not validate a code image");
    Check(host.RequestValidation(offer1.Sha256) && host.IsValidated && host.Version == offer1.Version && host.ContextId == oldContext,
        "explicit validation marks only the active code image without activating or replacing it");
    host.PollAvailable(1);
    host.Advance("en");
    Check(host.Available?.Version == new Version(1, 1, 0, 0) && host.Version == new Version(1, 0, 0, 0) && host.ContextId == oldContext,
        "a new version is available but is not loaded automatically");
    oldSurface = host.Surface;
    Check(host.RequestReplacement() && !host.RequestReplacement(), "a click queues the exact offer once");
    lifecycle.Clear();
    host.Advance("en");
    Check(host.Version == new Version(1, 1, 0, 0) && host.ContextId != oldContext, "the queued click swaps executable code at the next frame boundary");
    Check(!host.IsValidated, "activating another image does not inherit the previous image's validation");
    Check(!host.RequestValidation(offer1.Sha256) && !host.IsValidated, "an inactive image cannot be validated through a stale request");
    Check(new ModulePresentation(offer2.Version, null, "fr", null).ActiveLabel == "v1.1 active - Valider" &&
        (new ModulePresentation(offer2.Version, null, "fr", null) { Validated = true }).ActiveLabel == "v1.1 active - Valid\u00e9e" &&
        (new ModulePresentation(offer2.Version, null, "en", null) { Validated = true }).ActiveLabel == "v1.1 active - Validated",
        "validation and activation are explicitly distinct in French and English");
    Check(host.ActiveSlot == "green" && host.RetiredVersion == new Version(1, 0, 0, 0), "fixed blue/green slots change roles without claiming a second active module");
    Check(host.LastRequestTimestamp > 0 && host.LastSwapMilliseconds >= host.LastPreparationMilliseconds && host.LastPreparationMilliseconds >= 0,
        "click-to-active and preparation durations use monotonic timestamps");
    Check(oldSurface.Disposed && lifecycle.SequenceEqual(new[] { "allocate 1.1", "gpu-idle", "free 1.0" }),
        "new resources are staged, the GPU finishes, then old resources are freed");

    var candidatePath = Path.Combine(Path.GetTempPath(), $"gaia-candidate-test-{Guid.NewGuid():N}.dll");
    File.Copy(args[1], candidatePath);
    try
    {
        using var fenced = new SceneModuleHost<TestSurface>(args[0], candidatePath, "en",
            (visual, presentation) => new TestSurface(presentation.Version, visual, []), () => { });
        fenced.PollAvailable(1);
        fenced.Advance("en");
        var protectedSurface = fenced.Surface;
        var protectedContext = fenced.ContextId;
        Check(fenced.RequestReplacement(), "a valid offer is queued before the file changes");
        File.WriteAllBytes(candidatePath, new byte[16]);
        fenced.Advance("en");
        Check(fenced.Version == new Version(1, 0, 0, 0) && fenced.ContextId == protectedContext && ReferenceEquals(fenced.Surface, protectedSurface) && !protectedSurface.Disposed,
            "a candidate changed after the click keeps the original code and native resources alive");
    }
    finally { File.Delete(candidatePath); }

    using var allocationFailure = new SceneModuleHost<TestSurface>(args[0], args[1], "en",
        (visual, presentation) => presentation.Version.Minor == 1 ? throw new InvalidOperationException("simulated GPU allocation refusal") : new TestSurface(presentation.Version, visual, []), () => { });
    allocationFailure.PollAvailable(1);
    allocationFailure.Advance("en");
    var protectedAllocation = allocationFailure.Surface;
    allocationFailure.RequestReplacement();
    allocationFailure.Advance("en");
    Check(allocationFailure.Version == new Version(1, 0, 0, 0) && ReferenceEquals(protectedAllocation, allocationFailure.Surface) && !protectedAllocation.Disposed,
        "failed new-resource allocation never retires the old module");

    var failFence = false;
    TestSurface? staged = null;
    using var fenceFailure = new SceneModuleHost<TestSurface>(args[0], args[1], "en",
        (visual, presentation) => staged = new TestSurface(presentation.Version, visual, []), () => { if (failFence) throw new InvalidOperationException("simulated GPU fence failure"); });
    fenceFailure.PollAvailable(1);
    fenceFailure.Advance("en");
    var protectedFence = fenceFailure.Surface;
    fenceFailure.RequestReplacement();
    failFence = true;
    fenceFailure.Advance("en");
    failFence = false;
    Check(fenceFailure.Version == new Version(1, 0, 0, 0) && ReferenceEquals(protectedFence, fenceFailure.Surface) && !protectedFence.Disposed && staged is { Disposed: true },
        "a failed GPU fence disposes only the unused candidate resources");

    var root = Path.Combine(Path.GetTempPath(), $"gaia-revert-{Guid.NewGuid():N}");
    var initial = Path.Combine(root, "initial", "GaiaPresence.Module.dll");
    var available = Path.Combine(root, "available", "GaiaPresence.Module.dll");
    var retained = Path.Combine(root, "retained");
    Directory.CreateDirectory(Path.GetDirectoryName(initial)!);
    Directory.CreateDirectory(Path.GetDirectoryName(available)!);
    Directory.CreateDirectory(retained);
    var retained1 = Path.Combine(retained, offer1.Sha256 + ".dll");
    var retained2 = Path.Combine(retained, offer2.Sha256 + ".dll");
    var index = Path.Combine(retained, "versions.json");
    var preparationPath = Path.Combine(root, "preparation.json");
    File.WriteAllText(preparationPath, "{\"version\":\"1.2\",\"state\":\"preparing\",\"next_en\":\"Isolated Vulkan planet preview\",\"updated_utc\":\"2026-10-25T19:00:00Z\"}");
    File.Copy(args[0], initial); File.Copy(args[1], available);
    File.Copy(args[0], retained1); File.Copy(args[1], retained2);
    File.WriteAllText(index, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        { [offer1.Version.ToString()] = offer1.Sha256, [offer2.Version.ToString()] = offer2.Sha256 }));
    try
    {
        List<ModulePresentation> clockFrames = [];
        using var clockHost = new SceneModuleHost<TestSurface>(initial, available, "en",
            (visual, presentation) => { clockFrames.Add(presentation); return new TestSurface(presentation.Version, visual, []); }, () => { });
        var originalMetadata = File.ReadAllText(preparationPath);
        clockHost.PollAvailable(1);
        clockHost.RequestValidation(offer1.Sha256);
        var clockContext = clockHost.ContextId;
        clockHost.Advance("en", DateTimeOffset.Parse("2026-10-26T01:20:15Z"));
        var clockSurface = clockHost.Surface;
        var framesBeforeTick = clockFrames.Count;
        clockHost.Advance("en", DateTimeOffset.Parse("2026-10-26T01:20:59Z"));
        Check(ReferenceEquals(clockSurface, clockHost.Surface) && clockFrames.Count == framesBeforeTick,
            "the current clock does not allocate another render surface within the same minute");
        clockHost.Advance("en", DateTimeOffset.Parse("2026-10-26T01:21:00Z"));
        Check(clockSurface.Disposed && clockFrames.Count == framesBeforeTick + 1 &&
            clockFrames[^1].PreparationLines.Contains("Ottawa 21:21 EDT | Paris 02:21 CET") &&
            clockFrames[^1].PreparationLines.Contains("Ottawa 15:00 EDT | Paris 20:00 CET") &&
            clockHost.ContextId == clockContext && clockHost.IsValidated && File.ReadAllText(preparationPath) == originalMetadata,
            "a minute boundary updates only the clock surface, preserving code, validation and the recorded event");
        clockHost.Advance("en", DateTimeOffset.Parse("2026-10-26T03:59:59Z"));
        Check(clockFrames[^1].PreparationLines.Contains("Dates: Ottawa 2026-10-25 | Paris 2026-10-26"),
            "the current clock shows separate dates before Ottawa midnight");
        clockHost.Advance("en", DateTimeOffset.Parse("2026-10-26T04:00:00Z"));
        Check(clockFrames[^1].PreparationLines.Contains("Ottawa 00:00 EDT | Paris 05:00 CET") &&
            !clockFrames[^1].PreparationLines.Any(line => line.StartsWith("Dates:")),
            "both clock dates agree after Ottawa midnight without a fixed inter-city offset");
        clockHost.Advance("en", DateTimeOffset.Parse("2026-10-25T00:30:00Z"));
        Check(clockFrames[^1].PreparationLines.Contains("Ottawa 20:30 EDT | Paris 02:30 CEST"),
            "the current clock applies Paris daylight time before its fall transition");
        clockHost.Advance("en", DateTimeOffset.Parse("2026-10-25T01:30:00Z"));
        Check(clockFrames[^1].PreparationLines.Contains("Ottawa 21:30 EDT | Paris 02:30 CET"),
            "the current clock handles the repeated Paris hour while Ottawa is still on daylight time");
        using var roundtrip = new SceneModuleHost<TestSurface>(initial, available, "en",
            (visual, presentation) => new TestSurface(presentation.Version, visual, []), () => { });
        roundtrip.PollAvailable(1); roundtrip.RequestReplacement(); roundtrip.Advance("en");
        Check(roundtrip.Preparation?["version"] == "1.2" && roundtrip.Version == offer2.Version,
            "preparation metadata does not activate the future version or change the active image");
        var green = roundtrip.ContextId;
        roundtrip.PollAvailable(2);
        Check(roundtrip.Version == offer2.Version && roundtrip.Available?.Version == offer1.Version, "green offers the verified previous version without auto-reverting");
        Check(roundtrip.RequestReplacement(), "the previous-version action is explicitly clicked");
        Check(!roundtrip.IsValidated && !roundtrip.RequestValidation(offer2.Sha256), "validation refuses a pending replacement");
        roundtrip.Advance("fr");
        Check(roundtrip.Version == offer1.Version && roundtrip.ActiveSlot == "blue" && roundtrip.ContextId != green && roundtrip.Surface.Visual.Boxes.Count == originalBoxes,
            "revert reloads actual V1 code and geometry into blue on the same host");
        roundtrip.PollAvailable(3); roundtrip.RequestReplacement(); roundtrip.Advance("en");
        Check(roundtrip.Version == offer2.Version && roundtrip.ActiveSlot == "green", "the same host can reswap from V1 blue to V1.1 green");
        Check(roundtrip.RequestValidation(offer2.Sha256) && roundtrip.IsValidated && !roundtrip.RequestValidation(offer2.Sha256),
            "the active image is explicitly validated once without changing activation");
        roundtrip.PollAvailable(4); roundtrip.RequestReplacement(); roundtrip.Advance("fr");
        Check(!roundtrip.IsValidated && roundtrip.Version == offer1.Version, "rollback does not transfer validation to a different image");
        roundtrip.PollAvailable(5); roundtrip.RequestReplacement(); roundtrip.Advance("en");
        Check(roundtrip.IsValidated && roundtrip.Version == offer2.Version, "validation follows the exact code image across rollback and reswap");
        roundtrip.PollAvailable(6);
        var protectedRevert = roundtrip.Surface;
        var protectedCode = roundtrip.ContextId;
        roundtrip.RequestReplacement();
        File.WriteAllBytes(retained1, new byte[16]);
        roundtrip.Advance("en");
        Check(roundtrip.Version == offer2.Version && roundtrip.ContextId == protectedCode && ReferenceEquals(roundtrip.Surface, protectedRevert) && !protectedRevert.Disposed,
            "a corrupted retained DLL cannot replace the current green code or resources");
    }
    finally
    {
        foreach (var file in new[] { initial, available, retained1, retained2, index, preparationPath }) File.Delete(file);
        Directory.Delete(Path.GetDirectoryName(initial)!); Directory.Delete(Path.GetDirectoryName(available)!); Directory.Delete(retained); Directory.Delete(root);
    }

    if (args.Length == 3) Planet(args[1], args[2], offer2);
}
catch (Exception error)
{
    Check(false, $"{error.GetType().Name}: {error.Message}");
}

return failures == 0 ? 0 : 1;

// V1.1 -> V1.2 -> V1.1 -> V1.2 on one host through the resource port, as the scene does it but without a GPU: V1.2 gives
// the planet as triangles, which a host whose surfaces cannot draw them refuses.
void Planet(string v11, string v12, ModuleOffer offer2)
{
    var offer3 = LoadedSceneModule.Probe(v12);
    Check(offer3.Version == new Version(1, 2, 0, 0), "V1.2 is a real 1.2 image of the same module");
    var root = Path.Combine(Path.GetTempPath(), $"gaia-planet-{Guid.NewGuid():N}");
    var initial = Path.Combine(root, "initial", "GaiaPresence.Module.dll");
    var available = Path.Combine(root, "available", "GaiaPresence.Module.dll");
    var retained = Path.Combine(root, "retained");
    Directory.CreateDirectory(Path.GetDirectoryName(initial)!);
    Directory.CreateDirectory(Path.GetDirectoryName(available)!);
    Directory.CreateDirectory(retained);
    File.Copy(v11, initial); File.Copy(v12, available);
    File.Copy(v11, Path.Combine(retained, offer2.Sha256 + ".dll"));
    File.WriteAllText(Path.Combine(retained, "versions.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [offer2.Version.ToString()] = offer2.Sha256 }));
    try
    {
        using (var legacy = new SceneModuleHost<TestSurface>(initial, available, "en", (visual, presentation) => new TestSurface(presentation.Version, visual, []), () => { }))
        {
            legacy.PollAvailable(1);
            legacy.Advance("en");
            var kept = legacy.Surface;
            legacy.RequestReplacement();
            legacy.Advance("en");
            Check(legacy.Version == offer2.Version && ReferenceEquals(kept, legacy.Surface) && !kept.Disposed && legacy.Error == "candidate-rejected",
                "a host whose surfaces cannot draw triangles refuses V1.2 and keeps V1.1, rather than drawing it without its planet");
        }

        List<string> lifecycle = [];
        using var host = new SceneModuleHost<TestSurface>(initial, available, "en",
            (visual, triangles, presentation) => new TestSurface(presentation.Version, visual, lifecycle, triangles), () => lifecycle.Add("gpu-idle"));
        Check(host.Surface.Triangles is null, "V1.1 gives no triangles");
        host.PollAvailable(1);
        host.Advance("en");
        var v11Surface = host.Surface;
        Check(host.Available?.Version == offer3.Version && host.Version == offer2.Version, "V1.2 is offered, not loaded");
        lifecycle.Clear();
        host.RequestReplacement();
        host.Advance("en");
        var planet = host.Surface.Triangles;
        Check(host.Version == offer3.Version && planet?.Triangles.Count == 7680 && host.Surface.Visual.Text.Any(t => t.Text == "Gaia - disconnected"),
            "the click activates V1.2: its 7680 triangles reach the surface, and its placard still says disconnected");
        Check(v11Surface.Disposed && lifecycle.SequenceEqual(["allocate 1.2", "gpu-idle", "free 1.1"]),
            "V1.2's resources are staged, the GPU finishes, then V1.1's are freed");
        var mutable = false;
        try { ((IList<ModuleTriangle>)planet!.Triangles)[0] = default; }
        catch (NotSupportedException) { mutable = true; }
        Check(mutable, "the surface gets the host's read-only copy of the triangles, the one that was checked");
        host.Advance("fr");
        Check(host.Surface.Triangles?.Triangles.Count == 7680 && host.Surface.Visual.Text.Any(t => t.Text == "Gaia - non raccordée"),
            "a change of language rebuilds the surface with the planet");
        var planetCode = host.ContextId;
        host.PollAvailable(2);
        Check(host.Available?.Version == offer2.Version, "V1.2 offers the verified V1.1 back");
        host.RequestReplacement();
        host.Advance("en");
        Check(host.Version == offer2.Version && host.Surface.Triangles is null && host.ContextId != planetCode, "the explicit return reloads V1.1, without the planet");
        host.PollAvailable(3);
        host.RequestReplacement();
        host.Advance("en");
        Check(host.Version == offer3.Version && host.Surface.Triangles?.Triangles.Count == 7680, "the same host can go to V1.2 again");
    }
    finally { Directory.Delete(root, recursive: true); }
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static WeakReference RetireCode(ModuleOffer offer)
{
    using var module = LoadedSceneModule.Load(offer);
    module.Update(new("en"));
    _ = module.Render();
    return module.ContextReference;
}

sealed class TestSurface(Version version, ModuleVisual visual, List<string> trace, ModuleTriangleVisual? triangles = null) : IDisposable
{
    public bool Disposed { get; private set; }
    public ModuleVisual Visual { get; } = visual;
    public ModuleTriangleVisual? Triangles { get; } = triangles;
    private readonly string allocation = Allocate(version, trace);
    private static string Allocate(Version version, List<string> trace) { var name = version.ToString(2); trace.Add("allocate " + name); return name; }
    public void Dispose()
    {
        if (Disposed) throw new InvalidOperationException("Resource was disposed twice.");
        Disposed = true;
        trace.Add("free " + allocation);
    }
}
