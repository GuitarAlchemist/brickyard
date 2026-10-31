using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// A web page's canvas shown in the hall. Edge, already on the machine, runs the page in its own profile, in an app window
// moved off the screen so that its sound and voice play as in any browser; the scene drives it over the DevTools
// protocol, on a port of 127.0.0.1 that Edge picks, and nothing else listens. A script in the page sends the canvas's
// pixels each animation frame; the scene sends keys and clicks back. Edge runs only between Start and Dispose.
internal sealed class WebScreen : IDisposable
{
    private readonly Process browser;
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task reading;
    private readonly object gate = new();
    private readonly byte[] received; // the latest canvas, written by the reading task
    private readonly SemaphoreSlim sending = new(1, 1);
    private int nextId;
    private ulong revision;

    public int Width { get; }
    public int Height { get; }
    public ulong Frames { get; private set; }
    public string? Failure { get; private set; }
    public bool Running => Failure is null && !browser.HasExited;

    private WebScreen(Process browser, int width, int height)
    {
        this.browser = browser;
        Width = width;
        Height = height;
        received = new byte[width * height * 4];
        reading = Task.CompletedTask;
    }

    private WebScreen(Process browser, int width, int height, Uri page) : this(browser, width, height)
    {
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.ConnectAsync(page, stop.Token).GetAwaiter().GetResult();
        reading = Task.Run(Read);
    }

    // Opens url in Edge and starts sending selector's canvas, width by height pixels.
    public static WebScreen Start(string url, string profile, string selector, int width, int height, TimeSpan wait)
    {
        var edge = Edge() ?? throw new FileNotFoundException("Microsoft Edge is not installed.");
        Directory.CreateDirectory(profile);
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        File.Delete(portFile);
        var start = new ProcessStartInfo(edge) { UseShellExecute = false };
        foreach (var argument in new[]
        {
            $"--user-data-dir={profile}", "--remote-debugging-port=0", "--remote-allow-origins=http://127.0.0.1",
            "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-extensions",
            "--window-position=-32000,-32000", $"--window-size={width * 2},{height * 2}",
            "--autoplay-policy=no-user-gesture-required",
            // Off the screen the window is occluded; Edge would otherwise stop drawing and timing the page.
            "--disable-backgrounding-occluded-windows", "--disable-renderer-backgrounding", "--disable-background-timer-throttling",
            "--disable-features=CalculateNativeWinOcclusion",
            $"--app={url}",
        })
        {
            start.ArgumentList.Add(argument);
        }

        var browser = Process.Start(start) ?? throw new InvalidOperationException("Edge did not start.");
        Bind(browser);
        try
        {
            var deadline = Stopwatch.StartNew();
            string[] lines = [];
            while (deadline.Elapsed < wait && !(File.Exists(portFile) && (lines = ReadShared(portFile)).Length >= 2))
            {
                Thread.Sleep(50);
            }

            if (lines.Length < 2 || !int.TryParse(lines[0], out var port))
            {
                throw new TimeoutException("Edge did not open its DevTools port.");
            }

            Uri? page = null;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            while (page is null && deadline.Elapsed < wait)
            {
                using var targets = JsonDocument.Parse(http.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult());
                foreach (var target in targets.RootElement.EnumerateArray())
                {
                    if (target.GetProperty("type").GetString() == "page" && target.TryGetProperty("webSocketDebuggerUrl", out var debugger))
                    {
                        page = new Uri(debugger.GetString()!);
                        break;
                    }
                }

                if (page is null)
                {
                    Thread.Sleep(50);
                }
            }

            var screen = new WebScreen(browser, width, height, page ?? throw new TimeoutException("Edge opened no page."));
            screen.Send("Runtime.enable", new { }); // binding calls arrive only with the domain on
            screen.Send("Runtime.addBinding", new { name = "deltaFrame" });
            var script = Capture(selector, width, height);
            screen.Send("Page.enable", new { });
            screen.Send("Page.addScriptToEvaluateOnNewDocument", new { source = script });
            // The page may still be loading into a document the script never saw: put it in again, it runs once a
            // document, until the canvas arrives.
            while (screen.Frames == 0 && deadline.Elapsed < wait)
            {
                screen.Send("Runtime.evaluate", new { expression = script });
                Thread.Sleep(250);
            }

            if (screen.Frames == 0)
            {
                screen.Dispose();
                throw new TimeoutException("The page sent no canvas.");
            }
            return screen;
        }
        catch
        {
            Kill(browser);
            throw;
        }
    }

    // Copies the latest canvas into pixels (RGBA, top-left origin) when it is newer than seen; returns its revision.
    public ulong Copy(Span<byte> pixels, ulong seen)
    {
        lock (gate)
        {
            if (revision != seen)
            {
                received.CopyTo(pixels);
            }

            return revision;
        }
    }

    // A key by its DOM code, as KeyW, Digit1, ArrowUp or Space: the physical key, whatever the layout.
    public void Key(string code, bool down)
    {
        var (key, virtualKey) = code switch
        {
            ['K', 'e', 'y', var letter] => (char.ToLowerInvariant(letter).ToString(), (int)letter),
            ['D', 'i', 'g', 'i', 't', var digit] => (digit.ToString(), (int)digit),
            "ArrowLeft" => (code, 37), "ArrowUp" => (code, 38), "ArrowRight" => (code, 39), "ArrowDown" => (code, 40),
            "Space" => (" ", 32), "Enter" => ("Enter", 13), "Escape" => ("Escape", 27),
            _ => (code, 0),
        };
        Post("Input.dispatchKeyEvent", new { type = down ? "keyDown" : "keyUp", code, key, windowsVirtualKeyCode = virtualKey });
    }

    // A click at x, y in the page's CSS pixels.
    public void Click(double x, double y)
    {
        Post("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        Post("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
    }

    // Clicks the middle of the element selector names, as a person would, so that the page may start its sound.
    public void ClickOn(string selector) =>
        Post("Runtime.evaluate", new { expression = $"(() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); if (e) {{ const r = e.getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2]; }} }})()", returnByValue = true }, result =>
        {
            if (result.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var point) && point.ValueKind == JsonValueKind.Array)
            {
                Click(point[0].GetDouble(), point[1].GetDouble());
            }
        });

    // An expression's value as JSON text, for diagnostics.
    public string Evaluate(string expression)
    {
        var done = new TaskCompletionSource<string>();
        Post("Runtime.evaluate", new { expression, returnByValue = true }, result => done.TrySetResult(result.GetRawText()));
        return done.Task.Wait(TimeSpan.FromSeconds(5)) ? done.Task.Result : "(no answer)";
    }

    public void Dispose()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                Send("Browser.close", new { });
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or TimeoutException or InvalidOperationException)
        {
        }

        stop.Cancel();
        if (!browser.WaitForExit(2000))
        {
            Kill(browser);
        }

        try
        {
            reading.Wait(1000);
        }
        catch (AggregateException)
        {
        }

        socket.Dispose();
        browser.Dispose();
        stop.Dispose();
    }

    // The page side: each animation frame, the canvas's pixels go to the binding, as width, height and base64 RGBA.
    internal static string Capture(string selector, int width, int height) => $$"""
        (() => {
          if (window.__deltaScreen) return;
          window.__deltaScreen = true;
          const chunk = 0x8000;
          const tick = () => {
            requestAnimationFrame(tick);
            const canvas = document.querySelector({{JsonSerializer.Serialize(selector)}});
            if (!canvas || !window.deltaFrame || canvas.width !== {{width}} || canvas.height !== {{height}}) return;
            const data = canvas.getContext("2d").getImageData(0, 0, {{width}}, {{height}}).data;
            let text = "";
            for (let i = 0; i < data.length; i += chunk) text += String.fromCharCode.apply(null, data.subarray(i, i + chunk));
            window.deltaFrame(btoa(text));
          };
          requestAnimationFrame(tick);
        })()
        """;

    private async Task Read()
    {
        var buffer = new byte[1 << 20];
        var used = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (used == buffer.Length)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                var part = await socket.ReceiveAsync(buffer.AsMemory(used), stop.Token);
                if (part.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                used += part.Count;
                if (!part.EndOfMessage)
                {
                    continue;
                }

                Handle(buffer.AsSpan(0, used));
                used = 0;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or FormatException)
        {
            if (!stop.IsCancellationRequested)
            {
                Failure = e.GetType().Name;
            }
        }
    }

    private readonly Dictionary<int, Action<JsonElement>> pending = [];

    private void Handle(ReadOnlySpan<byte> message)
    {
        using var document = JsonDocument.Parse(message.ToArray());
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var id))
        {
            Action<JsonElement>? then;
            lock (pending)
            {
                pending.Remove(id.GetInt32(), out then);
            }

            if (then is not null && root.TryGetProperty("result", out var result))
            {
                then(result);
            }

            return;
        }

        if (root.GetProperty("method").GetString() == "Runtime.bindingCalled" &&
            root.GetProperty("params") is var details && details.GetProperty("name").GetString() == "deltaFrame")
        {
            var payload = details.GetProperty("payload").GetString()!;
            lock (gate)
            {
                if (Convert.TryFromBase64String(payload, received, out var written) && written == received.Length)
                {
                    revision++;
                    Frames++;
                }
            }
        }
    }

    private void Send(string method, object parameters)
    {
        var done = new TaskCompletionSource();
        Post(method, parameters, _ => done.TrySetResult());
        if (!done.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException($"Edge did not answer {method}.");
        }
    }

    private void Post(string method, object parameters, Action<JsonElement>? then = null)
    {
        var id = Interlocked.Increment(ref nextId);
        if (then is not null)
        {
            lock (pending)
            {
                pending[id] = then;
            }
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        _ = Task.Run(async () =>
        {
            await sending.WaitAsync(stop.Token);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stop.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
            }
            finally
            {
                sending.Release();
            }
        });
    }

    private static string[] ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static string? Edge()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var path = Path.Combine(Environment.GetFolderPath(root), "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    // A job that ends Edge with this process, however it ends: a crash or a kill never leaves the game playing.
    private static readonly nint Job = KillOnClose();

    private static void Bind(Process process)
    {
        if (Job != 0 && !AssignProcessToJobObject(Job, process.Handle))
        {
            Console.Error.WriteLine($"WEBSCREEN Edge is not bound to the scene (error {Marshal.GetLastWin32Error()}); it is closed on leaving only");
        }
    }

    private static nint KillOnClose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var job = CreateJobObjectW(0, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (job == 0 || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>())) // JobObjectExtendedLimitInformation
        {
            return 0;
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, int length);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
}
