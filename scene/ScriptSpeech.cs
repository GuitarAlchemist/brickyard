using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

// speechSynthesis for a page's game, by Windows' own voices (SAPI's SpVoice, through COM on a thread of its own):
// utterances are spoken one after another, and the page learns which have ended, as a browser fires their onend.
internal sealed class ScriptSpeech : IDisposable
{
    private const int Async = 1, Purge = 2, Xml = 8;
    private readonly BlockingCollection<(int Id, string? Text, string Voice, double Rate, double Pitch, double Volume)> queue = new(64);
    private readonly ConcurrentQueue<int> ended = new();
    private readonly TaskCompletionSource<string> voices = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private volatile bool hushed;

    public ScriptSpeech()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "arcade speech" };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
    }

    // The voices as JSON: name, lang (as en-US), default; null until SAPI has listed them.
    public string? Voices => voices.Task.IsCompleted ? voices.Task.Result : null;

    public void Speak(int id, string text, string voice, double rate, double pitch, double volume)
    {
        if (!queue.IsAddingCompleted && !queue.TryAdd((id, text, voice, rate, pitch, volume)))
        {
            ended.Enqueue(id);
        }
    }

    // Stops what is said and drops what waits, as speechSynthesis.cancel().
    public void Hush()
    {
        hushed = true;
        queue.TryAdd((0, null, "", 0, 0, 0));
    }

    // The utterances that ended since the last call, by number, comma separated.
    public string Ended()
    {
        var list = new List<int>();
        while (ended.TryDequeue(out var id))
        {
            list.Add(id);
        }

        return string.Join(",", list);
    }

    public void Dispose()
    {
        queue.CompleteAdding();
        hushed = true;
        thread.Join(1000);
    }

    private void Run()
    {
        object? sapi = null;
        var tokens = new Dictionary<string, object>();
        try
        {
            var type = OperatingSystem.IsWindows() ? Type.GetTypeFromProgID("SAPI.SpVoice") : null;
            sapi = type is null ? null : Activator.CreateInstance(type);
            if (sapi is not null)
            {
                var list = Call(sapi, "GetVoices", "", "")!;
                var found = new List<object>();
                for (var i = 0; i < (int)Get(list, "Count")!; i++)
                {
                    var token = Call(list, "Item", i)!;
                    var name = (string)Call(token, "GetDescription", 0)!;
                    var lcid = ((string)Call(token, "GetAttribute", "Language")!).Split(';')[0];
                    var lang = int.TryParse(lcid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ? Language(code) : "";
                    tokens[name] = token;
                    found.Add(new { name, lang, voiceURI = name, localService = true, @default = i == 0 });
                }

                voices.TrySetResult(JsonSerializer.Serialize(found));
            }
        }
        catch (Exception e) when (e is TargetInvocationException or COMException or InvalidCastException)
        {
            Console.Error.WriteLine($"ARCADE: no Windows voice ({e.GetType().Name}), the game is silent.");
            sapi = null;
        }

        voices.TrySetResult("[]");
        foreach (var (id, text, voice, rate, pitch, volume) in queue.GetConsumingEnumerable())
        {
            if (text is null)
            {
                hushed = false;
                continue;
            }

            if (sapi is null || hushed)
            {
                ended.Enqueue(id);
                continue;
            }

            try
            {
                if (tokens.TryGetValue(voice, out var token))
                {
                    Set(sapi, "Voice", token);
                }

                // The page's rate 1 is SAPI's 0, and SAPI's 10 three times as fast; pitch 0 to 2 becomes -10 to 10.
                Set(sapi, "Rate", (int)Math.Clamp(Math.Round(10 * Math.Log(Math.Max(rate, 0.1)) / Math.Log(3)), -10, 10));
                Set(sapi, "Volume", (int)Math.Clamp(Math.Round(volume * 100), 0, 100));
                var tone = (int)Math.Clamp(Math.Round((pitch - 1) * 10), -10, 10);
                Call(sapi, "Speak", $"<pitch absmiddle=\"{tone}\">{SecurityElement.Escape(text)}</pitch>", Async | Purge | Xml);
                while (!(bool)Call(sapi, "WaitUntilDone", 30)!)
                {
                    if (hushed)
                    {
                        Call(sapi, "Speak", "", Async | Purge);
                        break;
                    }
                }
            }
            catch (Exception e) when (e is TargetInvocationException or COMException or InvalidCastException)
            {
                Console.Error.WriteLine($"ARCADE speech failed ({e.GetType().Name}).");
            }

            ended.Enqueue(id);
        }
    }

    // A voice's language from its Windows LCID, as en-US: the scene runs with invariant globalization, without the
    // cultures' table, so the usual voices' are named here and any other by its language alone.
    private static string Language(int lcid) => lcid switch
    {
        0x409 => "en-US", 0x809 => "en-GB", 0xC09 => "en-AU", 0x1009 => "en-CA", 0x4009 => "en-IN",
        0x40C => "fr-FR", 0xC0C => "fr-CA", 0x407 => "de-DE", 0xC0A => "es-ES", 0x80A => "es-MX",
        0x410 => "it-IT", 0x416 => "pt-BR", 0x411 => "ja-JP", 0x412 => "ko-KR", 0x804 => "zh-CN",
        _ => (lcid & 0x3FF) switch
        {
            0x09 => "en", 0x0C => "fr", 0x07 => "de", 0x0A => "es", 0x10 => "it", 0x16 => "pt",
            0x11 => "ja", 0x12 => "ko", 0x04 => "zh", 0x13 => "nl", 0x19 => "ru", _ => "",
        },
    };

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    // Voice takes an object by reference, Rate and Volume a number.
    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, value is int ? BindingFlags.SetProperty : BindingFlags.PutRefDispProperty, null, target, [value], CultureInfo.InvariantCulture);
}
