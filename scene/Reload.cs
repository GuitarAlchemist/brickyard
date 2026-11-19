using System.Diagnostics;

// F5: a full reload of the scene (the user, 2026-11-18: « il manque de pouvoir faire un reset complet de la scene
// (Rechargement complet) »). The window closes as it does when closed by hand, and a small helper (this same exe, run
// with --reload-after) waits for it to be gone, then brings it back:
// - under dotnet watch (DOTNET_WATCH=1), which waits for a file to change once its app has exited, by touching the
//   project's Program.cs (its date only), so that watch builds and starts it again;
// - otherwise by starting the exe again with the same arguments.
internal static class Reload
{
    internal static bool Asked { get; set; }

    // The helper's arguments: wait for this process, then touch the project's Program.cs or start the exe again.
    internal static string[] HelperArgs(int pid, bool watched, string? project, IEnumerable<string> args) =>
        watched && project is not null
            ? ["--reload-after", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--touch", Path.Combine(project, "Program.cs")]
            : ["--reload-after", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--then", .. args];

    // The folder of the exe's project, when it runs from its bin folder (dotnet watch's way): the nearest folder up that
    // holds a .csproj and a Program.cs.
    internal static string? Project(string from)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            if (dir.GetFiles("*.csproj").Length > 0 && File.Exists(Path.Combine(dir.FullName, "Program.cs"))) return dir.FullName;
        }

        return null;
    }

    // Called as the window has closed for F5: starts the helper, which outlives this process.
    internal static void Start(string[] args, Action<string> log)
    {
        var watched = Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1";
        var project = Project(AppContext.BaseDirectory);
        var helper = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in HelperArgs(Environment.ProcessId, watched, project, args)) helper.ArgumentList.Add(a);
        log(watched && project is not null ? $"full reload: dotnet watch starts it again ({project})" : "full reload: starting again");
        try
        {
            Process.Start(helper)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"full reload failed: {e.Message}");
        }
    }

    // The helper: --reload-after PID (--touch FILE | --then ARGS...). Returns its exit code.
    internal static int Helper(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out var pid)) return 2;
        try
        {
            using var gone = Process.GetProcessById(pid);
            gone.WaitForExit(60_000);
        }
        catch (ArgumentException)
        {
            // gone already
        }

        Thread.Sleep(500);
        if (args[2] == "--touch" && args.Length > 3)
        {
            File.SetLastWriteTime(args[3], DateTime.Now);
            return 0;
        }

        if (args[2] == "--then")
        {
            var again = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var a in args.Skip(3)) again.ArgumentList.Add(a);
            Process.Start(again)?.Dispose();
            return 0;
        }

        return 2;
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var watched = HelperArgs(42, true, @"C:\p\scene", ["--listen", "8805"]);
        check(watched.SequenceEqual(["--reload-after", "42", "--touch", @"C:\p\scene\Program.cs"]), "under dotnet watch the reload touches the project's Program.cs");
        var alone = HelperArgs(42, false, null, ["--listen", "8805"]);
        check(alone.SequenceEqual(["--reload-after", "42", "--then", "--listen", "8805"]), "alone it starts the exe again with its arguments");
        check(HelperArgs(42, true, null, []).Contains("--then"), "under watch with no project found, it starts the exe again");
    }
}
