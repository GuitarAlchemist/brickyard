using Delta.SceneModules;

var failures = 0;
void Check(bool ok, string message)
{
    Console.WriteLine($"MODULE TEST {(ok ? "ok" : "FAIL")} {message}");
    if (!ok) failures++;
}

try
{
    if (args.Length != 2) throw new ArgumentException("Expected paths to the real V1 and V1.1 module assemblies.");
    var offer1 = LoadedSceneModule.Probe(args[0]);
    var offer2 = LoadedSceneModule.Probe(args[1]);
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
    var retired = first.ContextReference;
    first.Dispose();
    var disposed = false;
    try { first.Render(); } catch (ObjectDisposedException) { disposed = true; }
    Check(disposed, "a retired module cannot execute again");
    for (var i = 0; i < 3 && retired.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    Check(!retired.IsAlive, "the retired code load context is actually collectible");

    List<string> lifecycle = [];
    using var host = new SceneModuleHost<TestSurface>(args[0], args[1], "en",
        (visual, presentation) => new TestSurface(presentation.Version, visual, lifecycle), () => lifecycle.Add("gpu-idle"));
    var oldContext = host.ContextId;
    var oldSurface = host.Surface;
    host.PollAvailable(1);
    host.Advance("en");
    Check(host.Available?.Version == new Version(1, 1, 0, 0) && host.Version == new Version(1, 0, 0, 0) && host.ContextId == oldContext,
        "a new version is available but is not loaded automatically");
    oldSurface = host.Surface;
    Check(host.RequestReplacement() && !host.RequestReplacement(), "a click queues the exact offer once");
    lifecycle.Clear();
    host.Advance("en");
    Check(host.Version == new Version(1, 1, 0, 0) && host.ContextId != oldContext, "the queued click swaps executable code at the next frame boundary");
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
}
catch (Exception error)
{
    Check(false, $"{error.GetType().Name}: {error.Message}");
}

return failures == 0 ? 0 : 1;

sealed class TestSurface(Version version, ModuleVisual visual, List<string> trace) : IDisposable
{
    public bool Disposed { get; private set; }
    public ModuleVisual Visual { get; } = visual;
    private readonly string allocation = Allocate(version, trace);
    private static string Allocate(Version version, List<string> trace) { var name = version.ToString(2); trace.Add("allocate " + name); return name; }
    public void Dispose()
    {
        if (Disposed) throw new InvalidOperationException("Resource was disposed twice.");
        Disposed = true;
        trace.Add("free " + allocation);
    }
}
