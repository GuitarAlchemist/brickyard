using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Runtime.Interop;

// A web page's game run without a browser: the page's own script, fetched from its site each time and never kept,
// runs in Jint, a JavaScript interpreter in C#, on a thread of its own. The script sees only what this class gives it,
// the little of a browser such a game uses: canvases drawn with fillRect and drawImage, keys and clicks, timers and
// animation frames, a few Web Audio nodes (ScriptAudio), speech (Windows' voices) and a small local storage. It has no
// network, no files and no .NET of its own. Each animation frame the main canvas's pixels are kept for the screen.
internal sealed class ScriptScreen : IDisposable
{
    private const int MaxStorage = 64 * 1024;
    private readonly Thread loop;
    private readonly object gate = new();
    private readonly byte[] received;
    private readonly Queue<Func<Engine, JsValue>> inputs = new();
    private readonly List<Canvas> canvases = [];
    private readonly Dictionary<string, uint> colors = [];
    private readonly HashSet<string> down = []; // the keys held, by code, whoever pressed them
    private readonly string? storagePath;
    private readonly Dictionary<string, string> storage;
    private ScriptAudio? audio; // opened by the script's first sound or voice, on its thread
    private ScriptSpeech? speech;
    private readonly bool stepped;
    private readonly bool muted; // a robot's game in the hall: its sound mixed and dropped, its voice timed, not spoken
    private Engine? engine;
    private double steppedTime;
    private long pulled; // stepped: the samples mixed so far
    private readonly List<float> sound = [];
    private volatile bool stopping;
    private ulong revision;
    private int screenCanvas = -1;
    private int errors;

    public int Width { get; }
    public int Height { get; }
    public ulong Frames { get; private set; }
    public string? Failure { get; private set; }
    public bool Running => Failure is null && (stepped || loop.IsAlive);

    private sealed class Canvas(int width, int height)
    {
        public int Width = width, Height = height;
        public bool Opaque;
        public byte[] Pixels = new byte[Math.Max(0, width * height * 4)];
    }

    private ScriptScreen(int width, int height, string? storagePath, bool stepped, bool muted)
    {
        this.stepped = stepped;
        this.muted = muted;
        Width = width;
        Height = height;
        received = new byte[width * height * 4];
        this.storagePath = storagePath;
        storage = storagePath is null ? [] : Load(storagePath);
        loop = new Thread(Loop) { IsBackground = true, Name = "arcade script" };
    }

    // Fetches url, runs its inline script with the canvas selector names (#id), width by height, as the screen. A pilot,
    // a script of ours run after the page's in its scope, may play it by keys; stepped, Step drives its frames; muted, it
    // runs on its own thread without a sound.
    public static ScriptScreen Start(string url, string? storagePath, string selector, int width, int height, TimeSpan wait, string? pilot = null, bool stepped = false, bool muted = false)
    {
        using var http = new HttpClient { Timeout = wait };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeltaArcade/1.0");
        return Run(http.GetStringAsync(url).GetAwaiter().GetResult(), storagePath, selector, width, height, wait, pilot, stepped, muted);
    }

    // The same with the page in hand; without a storage path, its local storage lasts as long as it does.
    public static ScriptScreen Run(string page, string? storagePath, string selector, int width, int height, TimeSpan wait, string? pilot = null, bool stepped = false, bool muted = false)
    {
        var scripts = Regex.Matches(page, @"<script(?:\s[^>]*)?>([\s\S]*?)</script>", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).Where(s => s.Trim().Length > 0).ToArray();
        if (scripts.Length == 0)
        {
            throw new InvalidDataException("The page has no inline script.");
        }

        var screen = new ScriptScreen(width, height, storagePath, stepped, muted && !stepped);
        var script = string.Join("\n;\n", scripts);
        if (screen.muted)
        {
            screen.audio = new ScriptAudio(offline: true); // its clock follows the loop's, its samples are dropped
        }

        if (stepped)
        {
            screen.audio = new ScriptAudio(offline: true); // its clock starts with the page's
            if (!screen.Setup(page, script, selector.TrimStart('#'), pilot) || !screen.Tick(0))
            {
                screen.Dispose();
                throw new InvalidDataException(screen.Failure ?? "The page drew nothing.");
            }

            return screen;
        }

        screen.loop.Start((page, script, selector.TrimStart('#'), pilot));
        var clock = Stopwatch.StartNew();
        while (screen.Frames == 0 && screen.Failure is null && clock.Elapsed < wait)
        {
            Thread.Sleep(20);
        }

        if (screen.Frames == 0)
        {
            var why = screen.Failure ?? "The page drew nothing.";
            screen.Dispose();
            throw new InvalidDataException(why);
        }

        return screen;
    }

    // A page of our own, offline: a sprite drawn over an opaque canvas, a key, a click, a timer and the storage.
    internal static void SelfTest(Action<bool, string> check)
    {
        const string page = """
            <canvas id="cv" width="4" height="2"></canvas><div id="start"></div>
            <script>
            const ctx = document.getElementById("cv").getContext("2d", { alpha: false });
            const s = document.createElement("canvas"); s.width = 2; s.height = 1;
            const c = s.getContext("2d"); c.fillStyle = "#ff0000"; c.fillRect(1, 0, 1, 1);
            let key = "", started = false, late = false;
            window.addEventListener("keydown", e => { key = e.code; e.preventDefault(); });
            document.getElementById("start").addEventListener("click", () => { started = true; });
            setTimeout(() => { late = true; }, 30);
            localStorage.setItem("k", "v");
            function frame() {
              requestAnimationFrame(frame);
              ctx.fillStyle = "#102030"; ctx.fillRect(0, 0, 4, 2);
              ctx.drawImage(s, 0, 0);
              if (key === "KeyW") { ctx.fillStyle = "#00ff00"; ctx.fillRect(3, 1, 1, 1); }
              if (started && late && localStorage.getItem("k") === "v") { ctx.fillStyle = "rgb(0, 0, 255)"; ctx.fillRect(0, 1, 1, 1); }
            }
            requestAnimationFrame(frame);
            </script>
            """;
        using var screen = Run(page, null, "#cv", 4, 2, TimeSpan.FromSeconds(10));
        screen.ClickOn("#start");
        screen.Key("KeyW", true);
        var pixels = new byte[4 * 2 * 4];
        uint At(int x, int y) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(pixels.AsSpan(((y * 4) + x) * 4));
        var clock = Stopwatch.StartNew();
        do
        {
            Thread.Sleep(20);
            screen.Copy(pixels, ulong.MaxValue);
        }
        while ((At(0, 1) != 0x0000FFFF || At(3, 1) != 0x00FF00FF) && clock.Elapsed < TimeSpan.FromSeconds(5));

        check(At(0, 0) == 0x102030FF && At(1, 0) == 0xFF0000FF, "a page's sprite is drawn over its canvas, its transparent pixels left out");
        check(At(3, 1) == 0x00FF00FF, "a page hears a key by its code");
        check(At(0, 1) == 0x0000FFFF, "a page hears a click, runs its timers and keeps its storage");
        check(screen.Running && screen.Frames > 1, "a page's animation frames run");
        var refused = "";
        try
        {
            using var wrong = Run(page.Replace("width=\"4\"", "width=\"5\""), null, "#cv", 4, 2, TimeSpan.FromSeconds(10));
        }
        catch (InvalidDataException e)
        {
            refused = e.Message;
        }

        check(refused.Contains("4 by 2", StringComparison.Ordinal), "a page without a canvas of the screen's size is refused");
    }

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
        var key = code switch
        {
            ['K', 'e', 'y', var letter] => char.ToLowerInvariant(letter).ToString(),
            ['D', 'i', 'g', 'i', 't', var digit] => digit.ToString(),
            "Space" => " ",
            _ => code,
        };
        Post(engine => engine.Invoke("__deltaKey", down ? "keydown" : "keyup", code, key));
    }

    // Whether the key of this DOM code is held, by the scene or by the pilot: a film moves the robot's hands with it.
    public bool Holding(string code)
    {
        lock (down)
        {
            return down.Contains(code);
        }
    }

    // A click on the element selector names (#id), as a person's: it may start the page's sound.
    public void ClickOn(string selector) => Post(engine => engine.Invoke("__deltaClick", selector.TrimStart('#')));

    public void Dispose()
    {
        stopping = true;
        if (loop.IsAlive && loop != Thread.CurrentThread)
        {
            loop.Join(2000);
        }

        audio?.Dispose();
        speech?.Dispose();
        Save();
    }

    private void Post(Func<Engine, JsValue> input)
    {
        lock (inputs)
        {
            if (inputs.Count < 256)
            {
                inputs.Enqueue(input);
            }
        }
    }

    // Stepped: one animation frame of the page, ms after the last, on the caller's thread; the page's time is then the
    // steps' sum, whatever the time it takes, as a recording needs. False once the game has failed.
    public bool Step(double ms)
    {
        if (!stepped || Failure is not null || engine is null)
        {
            return false;
        }

        steppedTime += ms;
        if (!Tick(steppedTime))
        {
            return false;
        }

        // The sound of the step, mixed up to the page's new time.
        var due = (long)Math.Round(steppedTime * ScriptAudio.SampleRate / 1000.0) - pulled;
        if (due > 0)
        {
            sound.AddRange(audio!.Pull((int)due));
            pulled += due;
        }

        return true;
    }

    // Stepped: the sound mixed since the last call, at 44.1 kHz mono.
    public float[] TakeSound()
    {
        var taken = sound.ToArray();
        sound.Clear();
        return taken;
    }

    private void Loop(object? start)
    {
        var (page, script, canvasId, pilot) = ((string, string, string, string?))start!;
        if (!Setup(page, script, canvasId, pilot))
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var next = 0.0;
        while (!stopping && Tick(clock.Elapsed.TotalMilliseconds))
        {
            if (muted)
            {
                // The sound's clock kept with the page's, its samples mixed and dropped.
                var due = (long)Math.Round(clock.Elapsed.TotalMilliseconds * ScriptAudio.SampleRate / 1000.0) - pulled;
                if (due > 0)
                {
                    audio!.Pull((int)due);
                    pulled += due;
                }
            }

            // Sixty frames a second, as a screen's animation frames.
            next += 1000.0 / 60.0;
            var late = clock.Elapsed.TotalMilliseconds - next;
            if (late > 100)
            {
                next = clock.Elapsed.TotalMilliseconds;
            }

            while (!stopping && clock.Elapsed.TotalMilliseconds < next)
            {
                Thread.Sleep(next - clock.Elapsed.TotalMilliseconds > 2 ? 1 : 0);
            }
        }
    }

    // The engine, the shim, the page's elements and its script, then the pilot's, if any, in the same global scope.
    private bool Setup(string page, string script, string canvasId, string? pilot)
    {
        try
        {
            var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(2)).LimitMemory(512L << 20).LimitRecursion(512));
            Host(engine);
            engine.Execute(Shim, "delta-shim.js");
            foreach (Match element in Regex.Matches(page, @"<(\w+)\s[^>]*\bid=""([^""]+)""[^>]*>"))
            {
                var tag = element.Groups[1].Value.ToLowerInvariant();
                var width = Attribute(element.Value, "width");
                var height = Attribute(element.Value, "height");
                engine.Invoke("__deltaElement", element.Groups[2].Value, tag, width ?? 300, height ?? 150);
            }

            screenCanvas = (int)engine.Invoke("__deltaCanvasOf", canvasId).AsNumber();
            if (screenCanvas < 0 || canvases[screenCanvas].Width != Width || canvases[screenCanvas].Height != Height)
            {
                throw new InvalidDataException($"The page has no {Width} by {Height} canvas #{canvasId}.");
            }

            engine.Execute(script, "page.js");
            if (pilot is not null)
            {
                engine.Execute(pilot, "pilot.js");
            }

            this.engine = engine;
            return true;
        }
        catch (Exception e)
        {
            Failure = $"{e.GetType().Name}: {First(e.Message)}";
            return false;
        }
    }

    // One animation frame at now, in ms: the inputs that came, the timers and frames, then the screen's pixels kept.
    private bool Tick(double now)
    {
        Func<Engine, JsValue>[] pending;
        lock (inputs)
        {
            pending = [.. inputs];
            inputs.Clear();
        }

        try
        {
            foreach (var input in pending)
            {
                input(engine!);
            }

            engine!.Invoke("__deltaTick", now);
            errors = 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A frame that throws is skipped, as a browser would; many in a row end the game.
            if (++errors == 1 || errors % 60 == 0)
            {
                Console.Error.WriteLine($"ARCADE script error ({e.GetType().Name}): {First(e.Message)}");
            }

            if (errors >= 600)
            {
                Failure = $"{e.GetType().Name}: {First(e.Message)}";
                return false;
            }
        }

        var shown = canvases[screenCanvas];
        lock (gate)
        {
            shown.Pixels.CopyTo(received, 0);
            for (var i = 3; i < received.Length; i += 4)
            {
                received[i] = 255;
            }

            revision++;
            Frames++;
        }

        return true;
    }

    // The functions the shim calls, by name under __host; each takes plain numbers and strings.
    private void Host(Engine engine)
    {
        var host = new JsObject(engine);
        void Function(string name, Func<JsValue[], JsValue> body) =>
            host.Set(name, new ClrFunction(engine, name, (_, args) => body(args)));
        double N(JsValue[] args, int i) => i < args.Length && args[i].IsNumber() ? args[i].AsNumber() : 0;
        string S(JsValue[] args, int i) => i < args.Length && !args[i].IsNull() && !args[i].IsUndefined() ? args[i].ToString() : "";

        Function("canvas", args =>
        {
            canvases.Add(new Canvas(Size(N(args, 0)), Size(N(args, 1))));
            return canvases.Count - 1;
        });
        Function("size", args =>
        {
            var canvas = canvases[(int)N(args, 0)];
            (canvas.Width, canvas.Height) = (Size(N(args, 1)), Size(N(args, 2)));
            canvas.Pixels = new byte[canvas.Width * canvas.Height * 4];
            Clear(canvas);
            return JsValue.Undefined;
        });
        Function("opaque", args =>
        {
            var canvas = canvases[(int)N(args, 0)];
            canvas.Opaque = true;
            Clear(canvas);
            return JsValue.Undefined;
        });
        Function("fill", args =>
        {
            Fill(canvases[(int)N(args, 0)], N(args, 1), N(args, 2), N(args, 3), N(args, 4), Color(S(args, 5)), false);
            return JsValue.Undefined;
        });
        Function("clear", args =>
        {
            Fill(canvases[(int)N(args, 0)], N(args, 1), N(args, 2), N(args, 3), N(args, 4), 0, true);
            return JsValue.Undefined;
        });
        Function("draw", args =>
        {
            Draw(canvases[(int)N(args, 0)], canvases[(int)N(args, 1)], N(args, 2), N(args, 3), N(args, 4), N(args, 5), N(args, 6), N(args, 7), N(args, 8), N(args, 9));
            return JsValue.Undefined;
        });
        Function("held", args =>
        {
            lock (down)
            {
                _ = args.Length > 1 && args[1].AsBoolean() ? down.Add(S(args, 0)) : down.Remove(S(args, 0));
            }

            return JsValue.Undefined;
        });
        Function("log", args =>
        {
            Console.Error.WriteLine($"ARCADE page says: {First(S(args, 0))}");
            return JsValue.Undefined;
        });
        Function("storeGet", args => storage.TryGetValue(S(args, 0), out var value) ? value : JsValue.Null);
        Function("storeSet", args =>
        {
            var (key, value) = (S(args, 0), S(args, 1));
            if (storage.Sum(p => p.Key.Length + p.Value.Length) + key.Length + value.Length <= MaxStorage)
            {
                storage[key] = value;
            }

            return JsValue.Undefined;
        });
        Function("storeRemove", args =>
        {
            storage.Remove(S(args, 0));
            return JsValue.Undefined;
        });
        // Stepped, for a film, the page's sound is mixed offline as its frames go (Step), on their clock, and each
        // utterance is spoken into a file and mixed in; it ends when its samples have played.
        // Muted, an utterance is not spoken: it ends after the time a voice would take to say it.
        var saying = new List<(int Id, int Voice, double End)>();
        var offline = stepped || muted;
        Function("audioNow", _ => (audio ??= new(offline)).Now);
        Function("sound", args => (audio ??= new(offline)).Play(S(args, 0), args.Length > 1 ? args[1] : JsValue.Undefined));
        Function("soundStop", args =>
        {
            audio?.Stop((int)N(args, 0), N(args, 1));
            return JsValue.Undefined;
        });
        Function("voices", _ => (speech ??= new()).Voices is { } known ? known : JsValue.Null);
        Function("speak", args =>
        {
            if (muted)
            {
                var now = audio?.Now ?? 0;
                saying.Add(((int)N(args, 0), -1, now + (0.07 * S(args, 1).Length / Math.Max(0.5, N(args, 3)))));
                return JsValue.Undefined;
            }

            if (stepped)
            {
                var samples = (speech ??= new()).Render(S(args, 1), S(args, 2), N(args, 3), N(args, 4), N(args, 5));
                var mixer = audio ??= new(true);
                saying.Add(((int)N(args, 0), mixer.PlaySamples(samples, mixer.Now), mixer.Now + (samples.Length / (double)ScriptAudio.SampleRate)));
                return JsValue.Undefined;
            }

            (speech ??= new()).Speak((int)N(args, 0), S(args, 1), S(args, 2), N(args, 3), N(args, 4), N(args, 5));
            return JsValue.Undefined;
        });
        Function("spoken", _ =>
        {
            if (!offline)
            {
                return speech?.Ended() ?? "";
            }

            var now = audio?.Now ?? 0;
            var ended = string.Join(",", saying.Where(s => s.End <= now).Select(s => s.Id));
            saying.RemoveAll(s => s.End <= now);
            return ended;
        });
        Function("hush", _ =>
        {
            var now = audio?.Now ?? 0;
            for (var i = 0; i < saying.Count; i++)
            {
                if (saying[i].Voice >= 0) audio?.Stop(saying[i].Voice, now);
                saying[i] = saying[i] with { End = now };
            }

            speech?.Hush();
            return JsValue.Undefined;
        });
        engine.SetValue("__host", host);
    }

    private static int Size(double value) => (int)Math.Clamp(double.IsFinite(value) ? value : 0, 0, 4096);

    private static void Clear(Canvas canvas)
    {
        Array.Clear(canvas.Pixels);
        if (canvas.Opaque)
        {
            for (var i = 3; i < canvas.Pixels.Length; i += 4)
            {
                canvas.Pixels[i] = 255;
            }
        }
    }

    // A rectangle of one colour, its edges rounded to whole pixels, as the page's pixel art draws them.
    private static void Fill(Canvas canvas, double x, double y, double w, double h, uint rgba, bool clear)
    {
        if (!(double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(w) && double.IsFinite(h)))
        {
            return;
        }

        if (w < 0) (x, w) = (x + w, -w);
        if (h < 0) (y, h) = (y + h, -h);
        var (x0, y0) = (Math.Max(0, (int)Math.Round(x)), Math.Max(0, (int)Math.Round(y)));
        var (x1, y1) = (Math.Min(canvas.Width, (int)Math.Round(x + w)), Math.Min(canvas.Height, (int)Math.Round(y + h)));
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        var (r, g, b, a) = ((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
        if (clear)
        {
            (r, g, b, a) = (0, 0, 0, canvas.Opaque ? (byte)255 : (byte)0);
        }
        else if (canvas.Opaque)
        {
            a = 255;
        }

        var pixels = canvas.Pixels;
        for (var row = y0; row < y1; row++)
        {
            var i = ((row * canvas.Width) + x0) * 4;
            for (var col = x0; col < x1; col++, i += 4)
            {
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, a);
            }
        }
    }

    // One canvas onto another, nearest pixel, its transparent pixels left out.
    private static void Draw(Canvas target, Canvas source, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0 || !double.IsFinite(dx + dy + dw + dh + sx + sy + sw + sh))
        {
            return;
        }

        var (x0, y0) = ((int)Math.Round(dx), (int)Math.Round(dy));
        var (x1, y1) = ((int)Math.Round(dx + dw), (int)Math.Round(dy + dh));
        var (from, to) = (source.Pixels, target.Pixels);
        for (var row = Math.Max(0, y0); row < Math.Min(target.Height, y1); row++)
        {
            var v = (int)Math.Floor(sy + ((row - dy + 0.5) * sh / dh));
            if (v < 0 || v >= source.Height) continue;
            for (var col = Math.Max(0, x0); col < Math.Min(target.Width, x1); col++)
            {
                var u = (int)Math.Floor(sx + ((col - dx + 0.5) * sw / dw));
                if (u < 0 || u >= source.Width) continue;
                var s = ((v * source.Width) + u) * 4;
                var t = ((row * target.Width) + col) * 4;
                var a = source.Opaque ? 255 : from[s + 3];
                if (a == 0) continue;
                if (a == 255)
                {
                    (to[t], to[t + 1], to[t + 2]) = (from[s], from[s + 1], from[s + 2]);
                    to[t + 3] = 255;
                    continue;
                }

                to[t] = (byte)(((from[s] * a) + (to[t] * (255 - a))) / 255);
                to[t + 1] = (byte)(((from[s + 1] * a) + (to[t + 1] * (255 - a))) / 255);
                to[t + 2] = (byte)(((from[s + 2] * a) + (to[t + 2] * (255 - a))) / 255);
                to[t + 3] = (byte)Math.Max(to[t + 3], a);
            }
        }
    }

    // A CSS colour as RGBA in a uint, R in the top byte: #rgb, #rrggbb, #rrggbbaa, rgb(), rgba(), black, white and
    // transparent, which is what such pages use; another is black.
    private uint Color(string css)
    {
        if (colors.TryGetValue(css, out var known))
        {
            return known;
        }

        var text = css.Trim().ToLowerInvariant();
        uint value = 0x000000FF;
        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => $"{c}{c}"));
            }

            if (hex.Length == 6)
            {
                hex += "ff";
            }

            if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
            }
        }
        else if (Regex.Match(text, @"^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$") is { Success: true } m)
        {
            byte Part(int i) => (byte)Math.Clamp(double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture), 0, 255);
            var alpha = m.Groups[4].Success ? Math.Clamp(double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), 0, 1) : 1;
            value = ((uint)Part(1) << 24) | ((uint)Part(2) << 16) | ((uint)Part(3) << 8) | (uint)Math.Round(alpha * 255);
        }
        else if (text == "white")
        {
            value = 0xFFFFFFFF;
        }
        else if (text == "transparent")
        {
            value = 0;
        }

        if (colors.Count < 4096)
        {
            colors[css] = value;
        }

        return value;
    }

    private static int? Attribute(string tag, string name) =>
        Regex.Match(tag, $@"\b{name}=""(\d+)""") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    private static string First(string text) => text.Split('\n')[0] is var line && line.Length > 160 ? line[..160] : text.Split('\n')[0];

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save()
    {
        if (storagePath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            File.WriteAllText(storagePath + ".tmp", JsonSerializer.Serialize(storage));
            File.Move(storagePath + ".tmp", storagePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The browser the page sees, in JavaScript over __host.
    private const string Shim = """
        (() => {
          const H = __host;
          const g = globalThis;
          g.window = g; g.self = g;
          const listeners = new Map();
          const on = (target, type, fn) => { if (typeof fn !== "function") return; const k = target.__key; if (!listeners.has(k)) listeners.set(k, {}); (listeners.get(k)[type] ||= []).push(fn); };
          const off = (target, type, fn) => { const l = listeners.get(target.__key); if (l && l[type]) l[type] = l[type].filter(f => f !== fn); };
          const fire = (target, type, event) => { const l = listeners.get(target.__key); for (const fn of (l && l[type]) || []) fn.call(target, event); const prop = target["on" + type]; if (typeof prop === "function") prop.call(target, event); };
          let keys = 0;
          const element = (tag) => {
            const e = { __key: ++keys, tagName: tag.toUpperCase(), style: {}, dataset: {}, children: [], classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } } };
            e.addEventListener = (t, f) => on(e, t, f); e.removeEventListener = (t, f) => off(e, t, f);
            e.appendChild = c => { e.children.push(c); return c; }; e.removeChild = c => c; e.setAttribute = () => {}; e.getAttribute = () => null;
            e.getBoundingClientRect = () => ({ x: 0, y: 0, left: 0, top: 0, width: 0, height: 0, right: 0, bottom: 0 });
            e.focus = () => {}; e.blur = () => {};
            return e;
          };
          const canvas = (w, h) => {
            const e = element("canvas");
            let width = w | 0, height = h | 0;
            e.__id = H.canvas(width, height);
            Object.defineProperty(e, "width", { get: () => width, set: v => { width = v | 0; H.size(e.__id, width, height); } });
            Object.defineProperty(e, "height", { get: () => height, set: v => { height = v | 0; H.size(e.__id, width, height); } });
            let context = null;
            e.getContext = (kind, options) => {
              if (kind !== "2d") return null;
              if (!context) {
                if (options && options.alpha === false) H.opaque(e.__id);
                let style = "#000000";
                context = {
                  canvas: e, imageSmoothingEnabled: false, globalAlpha: 1,
                  get fillStyle() { return style; }, set fillStyle(v) { style = String(v); },
                  fillRect(x, y, w, h) { H.fill(e.__id, x, y, w, h, style); },
                  clearRect(x, y, w, h) { H.clear(e.__id, x, y, w, h); },
                  drawImage(s, a, b, c, d, f, gg, hh, ii) {
                    if (!s || s.__id === undefined) return;
                    if (arguments.length <= 3) H.draw(e.__id, s.__id, 0, 0, s.width, s.height, a, b, s.width, s.height);
                    else if (arguments.length <= 5) H.draw(e.__id, s.__id, 0, 0, s.width, s.height, a, b, c, d);
                    else H.draw(e.__id, s.__id, a, b, c, d, f, gg, hh, ii);
                  },
                  save() {}, restore() {}, setTransform() {}, resetTransform() {},
                };
              }
              return context;
            };
            return e;
          };
          const byId = new Map();
          g.__deltaElement = (id, tag, w, h) => { byId.set(id, tag === "canvas" ? canvas(w, h) : element(tag)); };
          g.__deltaCanvasOf = id => { const e = byId.get(id); return e && e.__id !== undefined ? e.__id : -1; };
          const body = element("body");
          g.document = {
            __key: ++keys, body, documentElement: element("html"), hidden: false, visibilityState: "visible",
            getElementById: id => { if (!byId.has(id)) byId.set(id, element("div")); return byId.get(id); },
            querySelector: s => s.startsWith("#") ? g.document.getElementById(s.slice(1)) : null,
            querySelectorAll: () => [],
            createElement: tag => String(tag).toLowerCase() === "canvas" ? canvas(300, 150) : element(String(tag)),
            addEventListener: (t, f) => on(g.document, t, f), removeEventListener: (t, f) => off(g.document, t, f),
          };
          g.__key = ++keys;
          g.addEventListener = (t, f) => on(g, t, f); g.removeEventListener = (t, f) => off(g, t, f);
          g.innerWidth = 1024; g.innerHeight = 768; g.devicePixelRatio = 1;
          g.screen = { width: 1024, height: 768 };
          g.navigator = { userAgent: "DeltaArcade/1.0", language: "en-US", maxTouchPoints: 0 };
          g.location = { href: "", hostname: "", protocol: "https:", search: "", hash: "" };
          g.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} });
          g.console = { log: (...a) => H.log(a.join(" ")), warn: (...a) => H.log(a.join(" ")), error: (...a) => H.log(a.join(" ")), info() {}, debug() {} };
          g.localStorage = { getItem: k => H.storeGet(String(k)), setItem: (k, v) => H.storeSet(String(k), String(v)), removeItem: k => H.storeRemove(String(k)) };
          let clock = 0;
          g.performance = { now: () => clock };
          let timers = [], timerIds = 0, frames = [], frameIds = 0;
          g.setTimeout = (fn, ms, ...a) => { const id = ++timerIds; timers.push({ id, due: clock + Math.max(0, +ms || 0), fn, a }); return id; };
          g.setInterval = (fn, ms, ...a) => { const id = ++timerIds; timers.push({ id, due: clock + Math.max(1, +ms || 0), fn, a, every: Math.max(1, +ms || 0) }); return id; };
          g.clearTimeout = g.clearInterval = id => { timers = timers.filter(t => t.id !== id); };
          g.requestAnimationFrame = fn => { const id = ++frameIds; frames.push({ id, fn }); return id; };
          g.cancelAnimationFrame = id => { frames = frames.filter(f => f.id !== id); };
          g.queueMicrotask = fn => g.setTimeout(fn, 0);
          g.__deltaTick = now => {
            clock = now;
            g.__deltaSpoken();
            const due = timers.filter(t => t.due <= now).sort((x, y) => x.due - y.due);
            timers = timers.filter(t => t.due > now || t.every);
            // Each callback on its own, as a browser runs them: one that throws leaves the others, and the first error
            // is thrown at the end for the host to count.
            let error = null;
            const call = (fn, ...a) => { try { fn(...a); } catch (e) { error ??= e; } };
            for (const t of due) { if (t.every) t.due += t.every; if (typeof t.fn === "function") call(t.fn, ...t.a); }
            const run = frames; frames = [];
            for (const f of run) call(f.fn, now);
            if (error) throw error;
          };
          const event = (type, more) => Object.assign({ type, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, stopPropagation() {}, stopImmediatePropagation() {} }, more);
          g.__deltaKey = (type, code, key) => { H.held(code, type === "keydown"); const e = event(type, { code, key, repeat: false, shiftKey: false, ctrlKey: false, altKey: false, metaKey: false }); fire(g.document, type, e); fire(g, type, e); };
          g.__deltaClick = id => { const target = g.document.getElementById(id); for (const type of ["mousedown", "mouseup", "click"]) fire(target, type, event(type, { target, button: 0, clientX: 0, clientY: 0 })); };

          // Web Audio: each source, once started, goes to the host with the chain it feeds and every scheduled value.
          class AudioParam { constructor(v) { this.value = v; this.events = []; }
            setValueAtTime(v, t) { this.events.push([0, +v, +t]); return this; }
            linearRampToValueAtTime(v, t) { this.events.push([1, +v, +t]); return this; }
            exponentialRampToValueAtTime(v, t) { this.events.push([2, +v, +t]); return this; }
            setTargetAtTime(v, t) { this.events.push([0, +v, +t]); return this; }
            cancelScheduledValues() { this.events = []; return this; }
            get __plain() { return { value: +this.value, events: this.events }; } }
          class AudioNode { constructor(ac) { this.context = ac; this.__out = null; } connect(n) { this.__out = n; return n; } disconnect() { this.__out = null; } }
          const chainOf = node => { const chain = []; for (let n = node.__out; n && n.__kind !== "destination"; n = n.__out) chain.push(n.__plain); return chain; };
          class Source extends AudioNode {
            start(t) { this.__voice = H.sound(JSON.stringify({ source: this.__plain, chain: chainOf(this), start: +(t || 0) }), this.__samples ? this.__samples() : undefined); }
            stop(t) { if (this.__voice !== undefined) H.soundStop(this.__voice, +(t || 0)); }
          }
          class Oscillator extends Source { constructor(ac) { super(ac); this.type = "sine"; this.frequency = new AudioParam(440); this.detune = new AudioParam(0); }
            get __plain() { return { kind: "oscillator", type: this.type, frequency: this.frequency.__plain }; } }
          class BufferSource extends Source { constructor(ac) { super(ac); this.buffer = null; this.loop = false; this.playbackRate = new AudioParam(1); }
            get __plain() { return { kind: "buffer", loop: !!this.loop, rate: this.buffer ? this.buffer.sampleRate : 44100 }; }
            __samples() { return this.buffer ? this.buffer.getChannelData(0) : []; } }
          class Gain extends AudioNode { constructor(ac) { super(ac); this.gain = new AudioParam(1); } get __plain() { return { kind: "gain", gain: this.gain.__plain }; } }
          class Biquad extends AudioNode { constructor(ac) { super(ac); this.type = "lowpass"; this.frequency = new AudioParam(350); this.Q = new AudioParam(1); this.gain = new AudioParam(0); }
            get __plain() { return { kind: "filter", type: this.type, frequency: this.frequency.__plain, q: this.Q.__plain }; } }
          class AudioContext {
            constructor() { this.state = "running"; this.sampleRate = 44100; this.destination = { __kind: "destination" }; }
            get currentTime() { return H.audioNow(); }
            resume() { this.state = "running"; return Promise.resolve(); } suspend() { return Promise.resolve(); } close() { return Promise.resolve(); }
            createOscillator() { return new Oscillator(this); } createGain() { return new Gain(this); }
            createBiquadFilter() { return new Biquad(this); } createBufferSource() { return new BufferSource(this); }
            createBuffer(channels, length, rate) { const data = []; for (let c = 0; c < channels; c++) data.push(new Array(length).fill(0));
              return { numberOfChannels: channels, length, sampleRate: rate, duration: length / rate, getChannelData: i => data[i] }; }
          }
          g.AudioContext = AudioContext; g.webkitAudioContext = AudioContext;

          // Speech: Windows' voices.
          // Speech: Windows' voices. As in a browser, getVoices() may answer [] until they are known, then
          // voiceschanged fires.
          let voices = null, asked = false, said = 0;
          const saying = new Map();
          const known = () => { const v = H.voices(); if (v !== null) voices = JSON.parse(v); return voices !== null; };
          g.SpeechSynthesisUtterance = function (text) { this.text = String(text ?? ""); this.rate = 1; this.pitch = 1; this.volume = 1; this.voice = null; this.lang = ""; };
          g.speechSynthesis = {
            speaking: false, pending: false, paused: false, onvoiceschanged: null,
            getVoices() { asked = true; return voices || (known() ? voices : []); },
            speak(u) { const id = ++said; saying.set(id, u); this.speaking = true;
              H.speak(id, u.text, u.voice ? u.voice.name : "", +u.rate || 1, +u.pitch || 1, u.volume == null ? 1 : +u.volume); },
            cancel() { H.hush(); }, pause() {}, resume() {},
            addEventListener(t, f) { if (t === "voiceschanged") this.onvoiceschanged = f; },
          };
          g.__deltaSpoken = () => {
            if (asked && !voices && known() && typeof g.speechSynthesis.onvoiceschanged === "function") g.speechSynthesis.onvoiceschanged({ type: "voiceschanged" });
            const done = H.spoken();
            if (!done) return;
            for (const id of done.split(",")) { const u = saying.get(+id); saying.delete(+id); if (u && typeof u.onend === "function") u.onend({ type: "end", utterance: u }); }
            g.speechSynthesis.speaking = saying.size > 0;
          };
        })();
        """;
}
