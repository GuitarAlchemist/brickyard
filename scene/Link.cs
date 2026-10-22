using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// What the brain says the robot is doing. A listener on the loopback interface takes one JSON line per change, from
// any number of connections; the render loop reads the latest state and how loud the voice is at this moment.
internal sealed class Link(Func<double> clock)
{
    private const int MaxLine = 256 * 1024;
    private const int MaxEnvelope = 30_000; // ten minutes in 20 ms steps
    private const double Step = 0.02;

    private readonly Lock gate = new();
    private string state = "idle";
    private double since;
    private float[] envelope = [];
    private float peak = 1f;

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

    // The state, and the voice's loudness now as a fraction of the sentence's loudest step (0 outside speech).
    public (string State, float Level) Read(double now)
    {
        lock (gate)
        {
            var i = (int)((now - since) / Step);
            var level = state == "speaking" && i >= 0 && i < envelope.Length ? Math.Clamp(envelope[i] / peak, 0f, 1f) : 0f;
            return (state, level);
        }
    }

    // --demo: idle for 2 s, thinking for 2.5 s, then 3.5 s of speech-like loudness, again and again.
    public void Demo(double now)
    {
        var t = now % 8.0;
        var want = t < 2.0 ? "idle" : t < 4.5 ? "thinking" : "speaking";
        bool changed;
        lock (gate)
        {
            changed = want != state;
        }

        if (changed)
        {
            Set(want, now, want == "speaking" ? DemoEnvelope() : []);
        }
    }

    // A line from the brain, or null and why it was refused.
    internal static (string State, float[] Envelope)? Parse(string line, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state"u8, out var s) || s.ValueKind != JsonValueKind.String)
            {
                error = "no state";
                return null;
            }

            var state = s.GetString()!;
            if (state is not ("idle" or "thinking" or "speaking"))
            {
                error = $"unknown state '{state}'";
                return null;
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

            return (state, envelope);
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
        Check(Parse("""{"state":"speaking","seconds":0.06,"envelope":[0,0.1,0.05]}""", out _) is { State: "speaking", Envelope.Length: 3 }, "speaking");
        Check(Parse("""{"state":"dancing"}""", out var unknown) is null && unknown!.Contains("dancing"), "an unknown state is refused");
        Check(Parse("""{"state":"speaking","envelope":["loud"]}""", out _) is null, "an envelope of words is refused");
        Check(Parse("not json", out _) is null, "a line that is not JSON is refused");
        var link = new Link(() => 0);
        link.Set("speaking", 10.0, [0f, 0.1f, 0.05f]);
        Check(link.Read(10.03) == ("speaking", 1f), "the loudest step opens the mouth fully");
        Check(link.Read(10.05).Level == 0.5f, "half as loud, half open");
        Check(link.Read(10.07).Level == 0f, "closed after the sentence");
        link.Set("idle", 11.0, []);
        Check(link.Read(11.01) == ("idle", 0f), "idle");
        Console.WriteLine(failures == 0 ? "SELFTEST ok" : $"SELFTEST {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private void Set(string newState, double at, float[] newEnvelope)
    {
        lock (gate)
        {
            state = newState;
            since = at;
            envelope = newEnvelope;
            peak = Math.Max(newEnvelope.Length == 0 ? 0f : newEnvelope.Max(), 0.02f);
        }

        Console.WriteLine(newState == "speaking" ? $"SCENE speaking {newEnvelope.Length * Step:F2} s at {at:F2} s" : $"SCENE {newState} at {at:F2} s");
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
                            Set(change.State, clock(), change.Envelope);
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
