using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// What the brain says the robot is doing. A listener on the loopback interface takes one JSON line per change, from
// any number of connections; the render loop reads the latest state and how loud the voice is at this moment.
//
// A line carries any of: "state" (idle, listening, thinking or speaking) with "envelope" while speaking and "cost";
// "context", how full Claude's conversation is, as {"used": tokens, "limit": tokens}, where limit is where Claude Code
// compacts it; and "compacting", true while Claude Code compacts the conversation. Each changes only what it names.
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

    // What one line changes; a null field is left as it was.
    internal sealed record Change(string? State, float[] Envelope, string Cost, float? Context, bool? Compacting);

    // The robot's moment: the state, the voice's loudness as a fraction of the sentence's loudest step (0 outside
    // speech), what the answer costs ("free", "paid", or "" when the line did not say), how full the context is as a
    // fraction of where it is compacted (-1 when unknown), and whether it is being compacted.
    internal readonly record struct Moment(string State, float Level, string Cost, float Context, bool Compacting);

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
            return new Moment(state, level, cost, context, compacting);
        }
    }

    // --demo: idle, listening and thinking for 1.5 s each, then 3.5 s of speech-like loudness, 0.5 s idle again and
    // 2.5 s of compaction, while the context fills from empty to full and starts again after the compaction.
    public void Demo(double now)
    {
        var t = now % 11.0;
        var want = t < 1.5 ? "idle" : t < 3.0 ? "listening" : t < 4.5 ? "thinking" : t < 8.0 ? "speaking" : "idle";
        bool changed;
        lock (gate)
        {
            changed = want != state;
            context = t < 8.5 ? (float)(t / 8.5) : 0.08f;
            compacting = t >= 8.5;
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

            if (!root.TryGetProperty("state"u8, out var s))
            {
                if (context is null && compacting is null)
                {
                    error = "no state";
                    return null;
                }

                return new Change(null, [], "", context, compacting);
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

            return new Change(state, envelope, cost, context, compacting);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // --selftest: the parsing and the mouth's loudness, without a window.
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
        var link = new Link(() => 0);
        link.Apply(new Change("speaking", [0f, 0.1f, 0.05f], "free", null, null), 10.0);
        Check(link.Read(10.03) == new Moment("speaking", 1f, "free", -1f, false), "the loudest step opens the mouth fully");
        Check(link.Read(10.05).Level == 0.5f, "half as loud, half open");
        Check(link.Read(10.07).Level == 0f, "closed after the sentence");
        link.Apply(new Change(null, [], "", 0.5f, true), 10.08);
        Check(link.Read(10.09) is { State: "speaking", Context: 0.5f, Compacting: true }, "a compaction leaves the voice alone");
        link.Apply(new Change("idle", [], "", null, null), 11.0);
        Check(link.Read(11.01) == new Moment("idle", 0f, "", 0.5f, true), "a state leaves the context and the compaction alone");
        Console.WriteLine(failures == 0 ? "SELFTEST ok" : $"SELFTEST {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private void Apply(Change change, double at)
    {
        lock (gate)
        {
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
