using Delta.SceneModules;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

var failures = 0;
void Check(bool ok, string label) { Console.WriteLine($"SCREEN TEST {(ok ? "ok" : "FAIL")} {label}"); if (!ok) failures++; }
void Refuses(Action action, string label) { try { action(); Check(false, label); } catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or ObjectDisposedException) { Check(true, label); } }
try
{
    if (args.Length != 2) throw new ArgumentException("Expected Pong module and Gaia module paths");
    using var screen = new ScreenModuleSession(args[0]);
    Check(screen.Id == "arcade-pong" && screen.Version == new Version(0, 1, 0, 0), "identity and version belong to actual Pong code, not the Gaia slot");
    var first = screen.Frame;
    Check(first.Width == 160 && first.Height == 96 && first.Stride == 640 && first.Rgba.Length == 61440, "pixel port is fixed and bounded at 160 x 96 RGBA8");
    var original = SHA256.HashData(first.Rgba.Span);
    screen.Advance(0.01);
    Check(screen.Frame.Revision == first.Revision, "render is paced below thirty frames per second");
    screen.Advance(0.03);
    Check(screen.Frame.Revision == first.Revision + 1, "one due render publishes one revision");
    for (var i = 0; i < 180; i++) screen.Advance(1.0 / 60);
    Check(!original.SequenceEqual(SHA256.HashData(screen.Frame.Rgba.Span)), "elapsed monotonic time produces different game pixels");
    MemoryMarshal.TryGetArray(first.Rgba, out var firstBuffer);
    MemoryMarshal.TryGetArray(screen.Frame.Rgba, out var nextBuffer);
    Check(ReferenceEquals(firstBuffer.Array, nextBuffer.Array), "the module reuses its CPU frame buffer");
    screen.Paused = true;
    screen.Advance(0.1);
    var paused = SHA256.HashData(screen.Frame.Rgba.Span);
    for (var i = 0; i < 60; i++) screen.Advance(0.1);
    Check(paused.SequenceEqual(SHA256.HashData(screen.Frame.Rgba.Span)), "pause preserves pixels without a catch-up queue");
    foreach (var invalid in new[] { -1.0, double.NaN, double.PositiveInfinity }) Refuses(() => screen.Advance(invalid), "invalid elapsed time is rejected before mutation");
    Check(paused.SequenceEqual(SHA256.HashData(screen.Frame.Rgba.Span)), "rejected time leaves the last frame intact");
    screen.Reset();
    Check(original.SequenceEqual(SHA256.HashData(screen.Frame.Rgba.Span)), "reset deterministically returns the original image");
    var revisionBeforeCadence = screen.Frame.Revision;
    for (var i = 0; i < 600; i++) screen.Advance(0.01);
    Check(screen.Frame.Revision - revisionBeforeCadence <= 181, "one hundred caller ticks per second still publishes at most thirty game frames per second");
    for (var i = 0; i < 1000; i++) screen.Advance(1.0 / 60);
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 10000; i++) screen.Advance(1.0 / 60);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Check(allocated == 0, $"the warmed screen port allocates {allocated} managed bytes across ten thousand updates");
    Refuses(() => (first with { Width = 320 }).Validate(), "oversized frame rejected");
    Refuses(() => (first with { Stride = 639 }).Validate(), "incorrect row stride rejected");
    Refuses(() => (first with { Rgba = new byte[4] }).Validate(), "short pixel buffer rejected");
    Refuses(() => { using var wrong = new ScreenModuleSession(args[1]); }, "Gaia assembly is not accepted as an Arcade screen module");
    screen.Dispose(); screen.Dispose();
    Refuses(() => screen.Advance(0.1), "disposed code cannot update again");
    var retired = Retire(args[0]);
    for (var i = 0; i < 3 && retired.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    Check(!retired.IsAlive, "the retired Arcade code context is actually collected");
}
catch (Exception e) { Check(false, e.ToString()); }
return failures == 0 ? 0 : 1;

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static WeakReference Retire(string path) { using var module = new ScreenModuleSession(path); module.Advance(0.1); return module.CodeContextReference; }
