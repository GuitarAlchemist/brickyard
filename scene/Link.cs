using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// What the brain says the robot is doing. A listener on the loopback interface takes one JSON line per change, from
// any number of connections; the render loop reads the latest state and how loud the voice is at this moment.
//
// A line carries any of: "state" (idle, listening, thinking or speaking) with "envelope" while speaking and "cost";
// "context", how full Claude's conversation is, as {"used": tokens, "limit": tokens}, where limit is where Claude Code
// compacts it; "compacting", true while Claude Code compacts the conversation; and "window", what to do with the
// window (Placement.Actions), which Window runs; "mood", an expression the face holds for MoodSeconds (Moods); and
// "shelf", a move through the shelf of sessions and plugins (ShelfMoves), which Shelf runs; and "language", "en" or
// "fr", the shelf's labels (Words), which the brain sends first on each connection. Each changes only what it names.
internal sealed class Link(Func<double> clock)
{
    private const int MaxLine = 256 * 1024;
    private const int MaxEnvelope = 30_000; // ten minutes in 20 ms steps
    private const double Step = 0.02;

    private readonly Lock gate = new();
    private string state = "idle";
    private string cost = "";
    private double since;
    private float[] envelope = [];
    private float peak = 1f;
    private float context = -1f;
    private bool compacting;
    private string mood = "neutral";
    private double moodSince = double.NegativeInfinity;
    private double heard = double.NegativeInfinity; // when the last line came

    // The expressions the face knows; "neutral" ends the one shown.
    public static readonly string[] Moods = ["neutral", "joy", "surprise", "doubt", "sorry", "proud", "wink", "focus", "sleepy"];

    // How long the face holds an expression.
    public const double MoodSeconds = 6.0;

    // What one line changes; a null field is left as it was.
    internal sealed record Change(string? State, float[] Envelope, string Cost, float? Context, bool? Compacting, string? Window = null, string? Mood = null, string? Shelf = null, string? Language = null);

    // Takes what the brain asks of the window, on the link's threads.
    public Action<string>? Window { get; set; }

    // Takes the brain's moves through the shelf, on the link's threads.
    public Action<string>? Shelf { get; set; }

    public static readonly string[] ShelfMoves = ["open", "sessions", "conversations", "plugins", "marketplaces", "close", "up", "down", "enter", "back"];

    // The robot's moment: the state, the voice's loudness as a fraction of the sentence's loudest step (0 outside
    // speech), what the answer costs ("free", "paid", or "" when the line did not say), how full the context is as a
    // fraction of where it is compacted (-1 when unknown), whether it is being compacted, the expression the face holds
    // ("neutral" for none) and how long ago the brain last said anything, in seconds.
    internal readonly record struct Moment(string State, float Level, string Cost, float Context, bool Compacting, string Mood = "neutral", float Quiet = 0f);

    // Starts listening on 127.0.0.1, or says why it cannot.
    public string? Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            return $"cannot listen on 127.0.0.1:{port}: {e.Message}";
        }

        new Thread(() =>
        {
            while (true)
            {
                var client = listener.AcceptTcpClient();
                new Thread(() => Serve(client)) { IsBackground = true, Name = "link client" }.Start();
            }
        }) { IsBackground = true, Name = "link" }.Start();
        return null;
    }

    public Moment Read(double now)
    {
        lock (gate)
        {
            var i = (int)((now - since) / Step);
            var level = state == "speaking" && i >= 0 && i < envelope.Length ? Math.Clamp(envelope[i] / peak, 0f, 1f) : 0f;
            var face = now - moodSince < MoodSeconds ? mood : "neutral";
            var quiet = double.IsNegativeInfinity(heard) ? 0f : (float)Math.Max(0, now - heard);
            return new Moment(state, level, cost, context, compacting, face, quiet);
        }
    }

    // --demo: idle, listening and thinking for 1.5 s each, then 3.5 s of speech-like loudness, 0.5 s idle again and
    // 2.5 s of compaction, while the context fills from empty to full and starts again after the compaction; then each
    // expression for 1 s.
    public const double DemoSeconds = 19.0;

    public void Demo(double now)
    {
        var t = now % DemoSeconds;
        var want = t < 1.5 ? "idle" : t < 3.0 ? "listening" : t < 4.5 ? "thinking" : t < 8.0 ? "speaking" : "idle";
        bool changed;
        lock (gate)
        {
            changed = want != state;
            context = t < 8.5 ? (float)(t / 8.5) : 0.08f;
            compacting = t is >= 8.5 and < 11.0;
            mood = t >= 11.0 ? Moods[1 + Math.Min(Moods.Length - 2, (int)(t - 11.0))] : "neutral";
            moodSince = now;
        }

        if (changed)
        {
            Apply(new Change(want, want == "speaking" ? DemoEnvelope() : [], want is "thinking" or "speaking" ? "paid" : "", null, null), now);
        }
    }

    // A line from the brain, or null and why it was refused.
    internal static Change? Parse(string line, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not an object";
                return null;
            }

            float? context = null;
            if (root.TryGetProperty("context"u8, out var c))
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("used"u8, out var used) || !c.TryGetProperty("limit"u8, out var limit) ||
                    used.ValueKind != JsonValueKind.Number || limit.ValueKind != JsonValueKind.Number || limit.GetDouble() <= 0 || used.GetDouble() < 0)
                {
                    error = "the context is not {\"used\": tokens, \"limit\": tokens above 0}";
                    return null;
                }

                context = (float)(used.GetDouble() / limit.GetDouble());
            }

            bool? compacting = null;
            if (root.TryGetProperty("compacting"u8, out var k))
            {
                if (k.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "compacting is neither true nor false";
                    return null;
                }

                compacting = k.GetBoolean();
            }

            string? window = null;
            if (root.TryGetProperty("window"u8, out var w))
            {
                window = w.ValueKind == JsonValueKind.String ? w.GetString() : null;
                if (window is null || !Placement.Actions.Contains(window))
                {
                    error = $"the window cannot {w}";
                    return null;
                }
            }

            string? shelf = null;
            if (root.TryGetProperty("shelf"u8, out var sh))
            {
                shelf = sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;
                if (shelf is null || !ShelfMoves.Contains(shelf))
                {
                    error = $"the shelf cannot {sh}";
                    return null;
                }
            }

            string? language = null;
            if (root.TryGetProperty("language"u8, out var lg))
            {
                language = lg.ValueKind == JsonValueKind.String ? lg.GetString() : null;
                if (language is null || !Words.Languages.Contains(language))
                {
                    error = $"the shelf speaks en or fr, not {lg}";
                    return null;
                }
            }

            string? mood = null;
            if (root.TryGetProperty("mood"u8, out var md))
            {
                mood = md.ValueKind == JsonValueKind.String ? md.GetString() : null;
                if (mood is null || !Moods.Contains(mood))
                {
                    error = $"the face cannot show {md}";
                    return null;
                }
            }

            if (!root.TryGetProperty("state"u8, out var s))
            {
                if (context is null && compacting is null && window is null && mood is null && shelf is null && language is null)
                {
                    error = "no state";
                    return null;
                }

                return new Change(null, [], "", context, compacting, window, mood, shelf, language);
            }

            if (s.ValueKind != JsonValueKind.String)
            {
                error = "no state";
                return null;
            }

            var state = s.GetString()!;
            if (state is not ("idle" or "listening" or "thinking" or "speaking"))
            {
                error = $"unknown state '{state}'";
                return null;
            }

            var cost = "";
            if (root.TryGetProperty("cost"u8, out var co))
            {
                cost = co.ValueKind == JsonValueKind.String ? co.GetString()! : "?";
                if (cost is not ("free" or "paid"))
                {
                    error = "the cost is neither \"free\" nor \"paid\"";
                    return null;
                }
            }

            float[] envelope = [];
            if (state == "speaking" && root.TryGetProperty("envelope"u8, out var e))
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() > MaxEnvelope)
                {
                    error = $"the envelope is not an array of at most {MaxEnvelope} numbers";
                    return null;
                }

                envelope = new float[e.GetArrayLength()];
                var i = 0;
                foreach (var v in e.EnumerateArray())
                {
                    if (v.ValueKind != JsonValueKind.Number)
                    {
                        error = "the envelope holds something other than numbers";
                        return null;
                    }

                    envelope[i++] = (float)v.GetDouble();
                }
            }

            return new Change(state, envelope, cost, context, compacting, window, mood, shelf, language);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // --selftest: the parsing, the mouth's loudness and the reading of transcripts, without a window.
    public static int SelfTest()
    {
        var failures = 0;
        void Check(bool ok, string what)
        {
            if (!ok)
            {
                failures++;
                Console.WriteLine($"SELFTEST FAIL {what}");
            }
        }

        Check(Parse("""{"state":"idle"}""", out _) is { State: "idle" }, "idle");
        Check(Parse("""{"state":"listening"}""", out _) is { State: "listening" }, "listening");
        Check(Parse("""{"state":"speaking","seconds":0.06,"envelope":[0,0.1,0.05]}""", out _) is { State: "speaking", Envelope.Length: 3 }, "speaking");
        Check(Parse("""{"state":"dancing"}""", out var unknown) is null && unknown!.Contains("dancing"), "an unknown state is refused");
        Check(Parse("""{"state":"speaking","envelope":["loud"]}""", out _) is null, "an envelope of words is refused");
        Check(Parse("not json", out _) is null, "a line that is not JSON is refused");
        Check(Parse("""{"state":"thinking"}""", out _) is { Cost: "" }, "no cost, no lamp");
        Check(Parse("""{"state":"thinking","cost":"paid"}""", out _) is { State: "thinking", Cost: "paid" }, "Claude thinks");
        Check(Parse("""{"state":"speaking","envelope":[0.1],"cost":"free"}""", out _) is { Cost: "free" }, "the small model speaks");
        Check(Parse("""{"state":"speaking","cost":"cheap"}""", out _) is null, "an unknown cost is refused");
        Check(Parse("""{"context":{"used":100000,"limit":400000}}""", out _) is { State: null, Context: 0.25f, Compacting: null }, "the context alone");
        Check(Parse("""{"context":{"used":1,"limit":0}}""", out _) is null, "a context without a limit is refused");
        Check(Parse("""{"compacting":true}""", out _) is { State: null, Context: null, Compacting: true }, "a compaction begins");
        Check(Parse("""{"compacting":"yes"}""", out _) is null, "compacting is a boolean");
        Check(Parse("""{"seconds":1}""", out var nothing) is null && nothing == "no state", "a line that changes nothing is refused");
        Check(Parse("""{"window":"top-left"}""", out _) is { State: null, Window: "top-left" }, "the window goes to a corner");
        Check(Parse("""{"window":"dance"}""", out _) is null, "an unknown window action is refused");
        Check(Parse("""{"mood":"joy"}""", out _) is { State: null, Mood: "joy" }, "a smile");
        Check(Parse("""{"mood":"rage"}""", out _) is null, "an unknown expression is refused");
        Check(Parse("""{"state":"speaking","seconds":0.2,"envelope":[0.1,0.3],"cost":"paid","mood":"proud"}""", out _) is { State: "speaking", Mood: "proud" },
            "a sentence starts with an expression");
        Check(Parse("""{"shelf":"sessions"}""", out _) is { State: null, Shelf: "sessions" }, "the shelf opens on the sessions");
        Check(Parse("""{"shelf":"delete"}""", out _) is null, "an unknown move through the shelf is refused");
        Check(Parse("""{"shelf":"conversations"}""", out _) is { Shelf: "conversations" }, "the shelf opens on the conversations");
        Conversations.SelfTest(Check);
        Check(Parse("""{"language":"fr"}""", out _) is { State: null, Language: "fr" }, "the shelf speaks French");
        Check(Parse("""{"language":"de"}""", out _) is null, "an unknown language is refused");
        Words.Language = "fr";
        var french = Words.Say("Folder", "Dossier");
        Words.Language = "de";
        var fallback = Words.Say("Folder", "Dossier");
        Words.Language = "en";
        Check(french == "Dossier" && fallback == "Folder" && Words.Say("Folder", "Dossier") == "Folder", "the labels follow the language, English by default");
        var year = DateTime.Now.Year;
        var english = (Words.Date(new DateTime(year, 10, 22, 19, 13, 0)), Words.Date(new DateTime(2020, 3, 1, 8, 5, 0)), Words.Number(1.5));
        Words.Language = "fr";
        var inFrench = (Words.Date(new DateTime(year, 10, 22, 19, 13, 0)), Words.Date(new DateTime(2020, 3, 1, 8, 5, 0)), Words.Number(1.5));
        Words.Language = "en";
        Check(english == ("October 22, 19:13", "March 1, 2020, 08:05", "1.5") && inFrench == ("22 octobre, 19:13", "1 mars 2020, 08:05", "1,5"),
            "dates and numbers in both languages, without culture data");
        var link = new Link(() => 0);
        link.Apply(new Change("speaking", [0f, 0.1f, 0.05f], "free", null, null), 10.0);
        Check(link.Read(10.03) is { State: "speaking", Level: 1f, Cost: "free", Context: -1f, Compacting: false, Mood: "neutral" }, "the loudest step opens the mouth fully");
        Check(link.Read(10.05).Level == 0.5f, "half as loud, half open");
        Check(link.Read(10.07).Level == 0f, "closed after the sentence");
        link.Apply(new Change(null, [], "", 0.5f, true), 10.08);
        Check(link.Read(10.09) is { State: "speaking", Context: 0.5f, Compacting: true }, "a compaction leaves the voice alone");
        link.Apply(new Change("idle", [], "", null, null), 11.0);
        Check(link.Read(11.01) is { State: "idle", Level: 0f, Cost: "", Context: 0.5f, Compacting: true, Mood: "neutral" }, "a state leaves the context and the compaction alone");
        link.Apply(new Change(null, [], "", null, null, null, "joy"), 12.0);
        Check(link.Read(12.5).Mood == "joy" && link.Read(12.0 + MoodSeconds + 0.1).Mood == "neutral", "an expression lasts a few seconds");
        Console.WriteLine(failures == 0 ? "SELFTEST ok" : $"SELFTEST {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private void Apply(Change change, double at)
    {
        lock (gate)
        {
            heard = at;
            if (change.Mood is { } face)
            {
                mood = face;
                moodSince = at;
            }

            if (change.Context is { } c)
            {
                context = c;
            }

            if (change.Compacting is { } k)
            {
                compacting = k;
            }

            if (change.State is { } s)
            {
                state = s;
                cost = change.Cost;
                since = at;
                envelope = change.Envelope;
                peak = Math.Max(change.Envelope.Length == 0 ? 0f : change.Envelope.Max(), 0.02f);
            }
        }

        if (change.State is { } newState)
        {
            var paying = change.Cost == "" ? "" : $" ({change.Cost})";
            Console.WriteLine(newState == "speaking" ? $"SCENE speaking{paying} {change.Envelope.Length * Step:F2} s at {at:F2} s" : $"SCENE {newState}{paying} at {at:F2} s");
        }

        if (change.Language is { } lang)
        {
            Words.Language = lang;
        }

        if (change.Window is { } action)
        {
            Window?.Invoke(action);
        }

        if (change.Shelf is { } move)
        {
            Shelf?.Invoke(move);
        }

        if (change.Compacting is { } compactingNow)
        {
            Console.WriteLine($"SCENE {(compactingNow ? "compacting" : "compacted")} at {at:F2} s");
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var line = new MemoryStream();
            var buffer = new byte[4096];
            try
            {
                for (int n; (n = stream.Read(buffer)) > 0;)
                {
                    for (var i = 0; i < n; i++)
                    {
                        if (buffer[i] != (byte)'\n')
                        {
                            if (line.Length == MaxLine)
                            {
                                Console.WriteLine("SCENE dropped a connection: a line over 256 KB");
                                return;
                            }

                            line.WriteByte(buffer[i]);
                            continue;
                        }

                        var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim();
                        line.SetLength(0);
                        if (text.Length == 0)
                        {
                            continue;
                        }

                        if (Parse(text, out var error) is { } change)
                        {
                            Apply(change, clock());
                        }
                        else
                        {
                            Console.WriteLine($"SCENE refused a line: {error}");
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The brain went away; the next one connects again.
            }
        }
    }

    private static float[] DemoEnvelope()
    {
        var e = new float[175];
        for (var i = 0; i < e.Length; i++)
        {
            var t = i * (float)Step;
            var syllables = MathF.Abs(MathF.Sin(t * MathF.PI * 4.6f));
            var breath = t % 1.6f < 1.35f ? 1f : 0.05f;
            e[i] = 0.12f * syllables * breath * (0.7f + (0.3f * MathF.Sin(t * 1.9f)));
        }

        return e;
    }
}
