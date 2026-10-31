using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Jint;
using Jint.Native;

// The little of Web Audio a page's game uses, played by Windows' waveOut: oscillators and sample buffers, each through
// a chain of gains and biquad filters, with values set and ramped at given times. The clock is the samples mixed.
internal sealed class ScriptAudio : IDisposable
{
    private const int Rate = 44100;
    private const int Block = 512; // four of them ahead, 46 ms
    private const int Blocks = 4;
    private const int MaxVoices = 64;
    private readonly object gate = new();
    private readonly List<Voice> voices = [];
    private readonly Thread? mixer;
    private readonly Stopwatch fallback = Stopwatch.StartNew();
    private IntPtr device;
    private long mixed;
    private int ids;
    private volatile bool stopping;

    public ScriptAudio()
    {
        var format = new WaveFormat { Tag = 1, Channels = 1, Rate = Rate, BytesPerSecond = Rate * 2, Align = 2, Bits = 16 };
        if (waveOutOpen(out device, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
        {
            device = IntPtr.Zero;
            Console.Error.WriteLine("ARCADE: no sound device, the game is silent.");
            return;
        }

        mixer = new Thread(Mix) { IsBackground = true, Name = "arcade sound", Priority = ThreadPriority.AboveNormal };
        mixer.Start();
    }

    // The context's currentTime, in seconds.
    public double Now => device == IntPtr.Zero ? fallback.Elapsed.TotalSeconds : Interlocked.Read(ref mixed) / (double)Rate;

    // Starts a source described in JSON by the shim; samples are a buffer source's, in -1 to 1. Returns its number.
    public JsValue Play(string json, JsValue samples)
    {
        if (device == IntPtr.Zero)
        {
            return -1;
        }

        Voice voice;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var source = root.GetProperty("source");
            voice = new Voice
            {
                Id = ++ids,
                Start = root.GetProperty("start").GetDouble(),
                Kind = source.GetProperty("kind").GetString() ?? "",
            };
            if (voice.Kind == "oscillator")
            {
                voice.Wave = source.GetProperty("type").GetString() ?? "sine";
                voice.Frequency = Param.From(source.GetProperty("frequency"));
            }
            else
            {
                voice.Loop = source.GetProperty("loop").GetBoolean();
                voice.Step = source.GetProperty("rate").GetDouble() / Rate;
                voice.Samples = Samples(samples);
            }

            foreach (var node in root.GetProperty("chain").EnumerateArray())
            {
                voice.Chain.Add(node.GetProperty("kind").GetString() == "filter"
                    ? new Node { Filter = node.GetProperty("type").GetString() ?? "lowpass", Value = Param.From(node.GetProperty("frequency")), Q = Param.From(node.GetProperty("q")) }
                    : new Node { Value = Param.From(node.GetProperty("gain")) });
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return -1;
        }

        lock (gate)
        {
            if (voices.Count >= MaxVoices)
            {
                voices.RemoveAt(0);
            }

            voices.Add(voice);
        }

        return voice.Id;
    }

    public void Stop(int id, double at)
    {
        lock (gate)
        {
            foreach (var voice in voices.Where(v => v.Id == id))
            {
                voice.End = Math.Max(at, voice.Start);
            }
        }
    }

    public void Dispose()
    {
        stopping = true;
        mixer?.Join(1000);
    }

    private static float[] Samples(JsValue value)
    {
        if (value is not JsArray array)
        {
            return [];
        }

        var samples = new float[Math.Min(array.Length, (uint)Rate * 10)];
        for (var i = 0u; i < samples.Length; i++)
        {
            var sample = array.Get(i);
            samples[i] = sample.IsNumber() ? (float)Math.Clamp(sample.AsNumber(), -1, 1) : 0;
        }

        return samples;
    }

    private void Mix()
    {
        var headers = new IntPtr[Blocks];
        var size = Marshal.SizeOf<WaveHeader>();
        var block = new float[Block];
        var pcm = new short[Block];
        for (var i = 0; i < Blocks; i++)
        {
            headers[i] = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new WaveHeader { Data = Marshal.AllocHGlobal(Block * 2), Length = Block * 2, Flags = Done }, headers[i], false);
        }

        try
        {
            while (!stopping)
            {
                var wrote = false;
                foreach (var header in headers)
                {
                    var h = Marshal.PtrToStructure<WaveHeader>(header);
                    if ((h.Flags & Done) == 0)
                    {
                        continue;
                    }

                    if ((h.Flags & Prepared) != 0)
                    {
                        waveOutUnprepareHeader(device, header, size);
                    }

                    Render(block);
                    for (var i = 0; i < Block; i++)
                    {
                        pcm[i] = (short)(Math.Clamp(block[i], -1f, 1f) * short.MaxValue);
                    }

                    Marshal.Copy(pcm, 0, h.Data, Block);
                    h.Flags = 0;
                    Marshal.StructureToPtr(h, header, false);
                    waveOutPrepareHeader(device, header, size);
                    waveOutWrite(device, header, size);
                    wrote = true;
                }

                if (!wrote)
                {
                    Thread.Sleep(4);
                }
            }
        }
        finally
        {
            waveOutReset(device);
            foreach (var header in headers)
            {
                var h = Marshal.PtrToStructure<WaveHeader>(header);
                if ((h.Flags & Prepared) != 0)
                {
                    waveOutUnprepareHeader(device, header, size);
                }

                Marshal.FreeHGlobal(h.Data);
                Marshal.FreeHGlobal(header);
            }

            waveOutClose(device);
        }
    }

    // One block of samples from every voice playing in it; voices that ended are dropped.
    private void Render(float[] block)
    {
        Array.Clear(block);
        var start = Interlocked.Read(ref mixed);
        lock (gate)
        {
            foreach (var voice in voices)
            {
                for (var i = 0; i < block.Length; i++)
                {
                    var t = (start + i) / (double)Rate;
                    if (t < voice.Start)
                    {
                        continue;
                    }

                    if (t >= voice.End || voice.Finished)
                    {
                        voice.Finished = true;
                        break;
                    }

                    var sample = voice.Next(t);
                    foreach (var node in voice.Chain)
                    {
                        sample = node.Apply(sample, t, (i & 31) == 0);
                    }

                    block[i] += sample;
                }
            }

            voices.RemoveAll(v => v.Finished || (start / (double)Rate) - v.Start > 30);
        }

        Interlocked.Add(ref mixed, block.Length);
    }

    private sealed class Voice
    {
        public int Id;
        public double Start;
        public double End = double.PositiveInfinity;
        public string Kind = "";
        public string Wave = "sine";
        public Param Frequency = new(440, []);
        public float[] Samples = [];
        public bool Loop;
        public double Step = 1;
        public bool Finished;
        public readonly List<Node> Chain = [];
        private double phase;

        public float Next(double t)
        {
            if (Kind != "oscillator")
            {
                var index = (int)phase;
                if (index >= Samples.Length)
                {
                    if (!Loop || Samples.Length == 0)
                    {
                        Finished = true;
                        return 0;
                    }

                    phase -= Samples.Length;
                    index = (int)phase;
                }

                phase += Step;
                return Samples[index];
            }

            phase += Frequency.At(t) / Rate;
            phase -= Math.Floor(phase);
            return Wave switch
            {
                "square" => phase < 0.5 ? 1f : -1f,
                "sawtooth" => (float)((2 * phase) - 1),
                "triangle" => (float)(phase < 0.5 ? (4 * phase) - 1 : 3 - (4 * phase)),
                _ => (float)Math.Sin(2 * Math.PI * phase),
            };
        }
    }

    // A gain, or a biquad filter (RBJ's cookbook) whose coefficients follow its frequency every 32 samples.
    private sealed class Node
    {
        public string? Filter;
        public Param Value = new(1, []);
        public Param Q = new(1, []);
        private double b0, b1, b2, a1, a2, x1, x2, y1, y2;
        private bool ready;

        public float Apply(float x, double t, bool refresh)
        {
            if (Filter is null)
            {
                return (float)(x * Value.At(t));
            }

            if (refresh || !ready)
            {
                Coefficients(Value.At(t), Q.At(t));
                ready = true;
            }

            var y = (b0 * x) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2);
            (x2, x1, y2, y1) = (x1, x, y1, y);
            return (float)y;
        }

        private void Coefficients(double frequency, double q)
        {
            var w = 2 * Math.PI * Math.Clamp(frequency, 10, (Rate / 2) - 100) / Rate;
            // Web Audio gives a lowpass or highpass Q in decibels, a bandpass Q as it is.
            var quality = Filter == "bandpass" ? Math.Max(q, 0.0001) : Math.Pow(10, q / 20);
            var alpha = Math.Sin(w) / (2 * quality);
            var cos = Math.Cos(w);
            var (n0, n1, n2) = Filter switch
            {
                "highpass" => ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2),
                "bandpass" => (alpha, 0.0, -alpha),
                _ => ((1 - cos) / 2, 1 - cos, (1 - cos) / 2),
            };
            var a0 = 1 + alpha;
            (b0, b1, b2, a1, a2) = (n0 / a0, n1 / a0, n2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }
    }

    // An AudioParam: its value, then events of (0 set, 1 linear ramp, 2 exponential ramp, value, time) in order.
    private sealed record Param(double Value, (int Kind, double Value, double Time)[] Events)
    {
        public static Param From(JsonElement json) => new(
            json.GetProperty("value").GetDouble(),
            [.. json.GetProperty("events").EnumerateArray().Select(e => (e[0].GetInt32(), e[1].GetDouble(), e[2].GetDouble()))]);

        public double At(double t)
        {
            var (value, from) = (Value, 0.0);
            foreach (var (kind, target, time) in Events)
            {
                if (t < time)
                {
                    if (kind == 0 || time <= from)
                    {
                        return value;
                    }

                    var k = (t - from) / (time - from);
                    return kind == 2 && value > 0 && target > 0 ? value * Math.Pow(target / value, k) : value + ((target - value) * k);
                }

                (value, from) = (target, time);
            }

            return value;
        }
    }

    private const uint Done = 1, Prepared = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort Align, Bits, Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);
}
