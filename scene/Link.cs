using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// What the brain says the robot is doing. A listener on the loopback interface takes one JSON line per change, from
// any number of connections; the render loop reads the latest state and how loud the voice is at this moment. A
// connection whose first line is no JSON object, as a web page's request to 127.0.0.1 is, is dropped there.
//
// A line carries any of: "state" (idle, listening, thinking or speaking) with "envelope" while speaking and "cost";
// "context", how full Claude's conversation is, as {"used": tokens, "limit": tokens}, where limit is where Claude Code
// compacts it; "compacting", true while Claude Code compacts the conversation; and "window", what to do with the
// window (Placement.Actions), which Window runs; "mood", an expression the face holds for MoodSeconds (Moods); and
// "shelf", a move through the shelf of sessions and plugins (ShelfMoves), which Shelf runs; and "language", "en" or
// "fr", the shelf's labels (Words), which the brain sends first on each connection; and "hearts", true, a burst of
// hearts that rise from the robot for HeartSeconds; and "heard", {"text", "to"}, a sentence Delta heard and for whom
// it was (claude, delta, wake when Delta needed its name, dropped, someone else, or whom it called), or said, a sentence
// Delta says as it starts saying it, which a caption
// under the robot shows for HeardSeconds; and "pairing", {"rows", "seconds"}, the QR code of the
// link a phone opens to pair with Delta's gateway, one string of 0 and 1 per row, and how long its code lives, which
// the phone icon's card shows on a click; and "lease", for how many seconds Delta listens without hearing its name,
// which a ring that empties shows; and "invitation", {"rows", "seconds", "copied"}, the QR code of a link that lets
// guests watch the window, which the Inviter button asked for, how long it serves and whether the clipboard has it, or
// {"refused"}, why there is none (watching, off or gateway); and "mic_level", how loud the computer's microphone is, the
// RMS of the loudest frame of the last 100 ms, from 0 to 1, which a meter on the microphone button shows; and
// "devices", how many devices, such as the user's phone, listen to Delta through the gateway, which lights the phone
// icon's screen; and "guests_hear", true while guests hear the room through the gateway's microphone, which the
// guest badge says; and "version", the brain's, which the window's corner shows; and "gaia", how the bridge to Gaia
// stands (offline, listening, connected or degraded), which her placard says. Each changes only what it names.
internal sealed class Link(Func<double> clock)
{
    private const int MaxLine = 256 * 1024;
    private const int MaxEnvelope = 30_000; // ten minutes in 20 ms steps
    private const double Step = 0.02;
    private const int MaxHeard = 800; // the brain cuts the sentence to 400 characters, each at most two UTF-16 units

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
    private double heartsSince = double.NegativeInfinity;
    private string heardText = "";
    private string heardTo = "";
    private double heardSince = double.NegativeInfinity;
    private int heardId; // counts the sentences, so that the caption is drawn again for each
    private int demoHeard = -1; // the demo's last loop that showed the caption
    private string[]? pairing; // the QR code of the last pairing link, a credential while its code lives
    private double pairingUntil = double.NegativeInfinity;
    private int pairingId; // counts the links, so that the card is drawn again for each
    private int demoPairing = -1; // the demo's last loop that brought a link
    private NetworkStream? talking; // the brain connected now, which Tell writes back to
    private double leaseUntil = double.NegativeInfinity; // until when Delta listens without its name
    private double leaseLength; // how long that was when it began, for the ring
    private bool micOff; // the user turned the computer's microphone off from the window
    private int guests; // how many guests watch the window from afar, as the gateway last said
    private double guestsUntil = double.NegativeInfinity; // until when that holds without news
    private Invite? invitation; // the last invitation the Inviter button brought, or why none came
    private double invitationUntil = double.NegativeInfinity;
    private int invitationId; // counts them, so that the card opens and is drawn again for each
    private float micLevel; // how loud the microphone last was
    private int devices; // how many devices listen to Delta through the gateway
    private string version = ""; // the brain's version, as its hello line gives it
    private bool guestsHear; // whether guests hear the room, as the gateway last said
    private string gaia = "offline"; // how the bridge to Gaia stands, as the brain last said
    private double micLevelAt = double.NegativeInfinity;

    // How long the microphone's meter holds a level without news: the brain sends one every 100 ms while it listens.
    internal const double MicLevelSeconds = 0.5;

    // How long the guest badge stays without news: the gateway says it again every 30 s while anyone watches.
    internal const double GuestsSeconds = 60;

    // The expressions the face knows; "neutral" ends the one shown.
    public static readonly string[] Moods = ["neutral", "joy", "surprise", "doubt", "sorry", "proud", "wink", "focus", "sleepy"];

    // How long the face holds an expression.
    public const double MoodSeconds = 6.0;

    // How long a burst of hearts lasts.
    public const double HeartSeconds = 4.0;

    // How long the caption shows a sentence: five seconds, and more for a long one, up to twelve.
    public static double HeardSeconds(string text) => Math.Clamp(3.0 + (text.Length / 15.0), 5.0, 12.0);

    // An invitation for guests: its link's QR code, for how many seconds it serves and whether the clipboard has the
    // link; or, with no rows, why the gateway gave none.
    internal sealed record Invite(string[]? Rows, double Seconds, bool Copied, string Refused = "");

    // What one line changes; a null field is left as it was.
    internal sealed record Change(string? State, float[] Envelope, string Cost, float? Context, bool? Compacting, string? Window = null, string? Mood = null, string? Shelf = null, string? Language = null, bool Hearts = false, string? Heard = null, string HeardTo = "", string[]? Pairing = null, double PairingSeconds = 0, double? Lease = null, bool? Mic = null, int? Guests = null, Invite? Invitation = null, float? MicLevel = null, int? Devices = null, string? Version = null, bool? GuestsHear = null, string? Gaia = null);

    // Takes what the brain asks of the window, on the link's threads.
    public Action<string>? Window { get; set; }

    // Takes the brain's moves through the shelf, on the link's threads.
    public Action<string>? Shelf { get; set; }

    public static readonly string[] ShelfMoves = ["open", "sessions", "conversations", "plugins", "marketplaces", "courses", "close", "up", "down", "enter", "back"];

    // The robot's moment: the state, the voice's loudness as a fraction of the sentence's loudest step (0 outside
    // speech), what the answer costs ("free", "paid", or "" when the line did not say), how full the context is as a
    // fraction of where it is compacted (-1 when unknown), whether it is being compacted, the expression the face holds
    // ("neutral" for none), how long ago the brain last said anything, in seconds, how long ago a burst of hearts
    // began (-1 when none is rising), and the sentence Delta last heard while its caption shows ("" otherwise), for whom, how
    // long ago it came (-1 when none shows) and its number, which changes with each sentence; and the pairing QR code's
    // rows while its code lives (null otherwise), the seconds it still lives (0 once it expired, -1 before any came) and
    // the link's number; and for how many seconds more Delta listens without its name (0 when it needs it), of how many;
    // and the last invitation, its rows only while it serves, the seconds it still serves and its number; and how loud
    // the microphone is (0 without news), how many devices listen, the brain's version ("" before it says), and whether
    // guests hear the room.
    internal readonly record struct Moment(string State, float Level, string Cost, float Context, bool Compacting, string Mood = "neutral", float Quiet = 0f, float Hearts = -1f, string Heard = "", string HeardTo = "", float HeardAge = -1f, int HeardId = 0, string[]? Pairing = null, float PairingLeft = -1f, int PairingId = 0, float Lease = 0f, float LeaseLength = 0f, bool MicOff = false, int Guests = 0, Invite? Invitation = null, float InvitationLeft = -1f, int InvitationId = 0, float MicLevel = 0f, int Devices = 0, string Version = "", bool GuestsHear = false, string Gaia = "offline");

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
            var hearts = now - heartsSince is >= 0 and < HeartSeconds ? (float)(now - heartsSince) : -1f;
            var age = now - heardSince >= 0 && now - heardSince < HeardSeconds(heardText) ? (float)(now - heardSince) : -1f;
            var left = pairingId == 0 ? -1f : (float)Math.Max(0, pairingUntil - now);
            var lease = (float)Math.Max(0, leaseUntil - now);
            var serves = invitationId == 0 ? -1f : (float)Math.Max(0, invitationUntil - now);
            var invite = serves > 0f || invitation?.Rows is null ? invitation : invitation with { Rows = null };
            return new Moment(state, level, cost, context, compacting, face, quiet, hearts, age >= 0f ? heardText : "", age >= 0f ? heardTo : "", age, heardId, left > 0f ? pairing : null, left, pairingId, lease, lease > 0f ? (float)leaseLength : 0f, micOff, now < guestsUntil ? guests : 0, invite, serves, invitationId, now - micLevelAt < MicLevelSeconds ? micLevel : 0f, devices, version, guestsHear, gaia);
        }
    }

    // --demo: idle, listening and thinking for 1.5 s each, then 3.5 s of speech-like loudness, 0.5 s idle again and
    // 2.5 s of compaction, while the context fills from empty to full and starts again after the compaction; then each
    // expression for 1 s, and the caption of a sentence let pass, which lasts into the next loop; a pairing link comes at
    // the start of each loop, its code living for the loop, and Delta listens without its name for 10 s after speaking.
    public const double DemoSeconds = 21.0;

    public void Demo(double now)
    {
        var t = now % DemoSeconds;
        var want = t < 1.5 ? "idle" : t < 3.0 ? "listening" : t < 4.5 ? "thinking" : t < 8.0 ? "speaking" : "idle";
        bool changed;
        bool caption;
        bool link;
        lock (gate)
        {
            changed = want != state;
            context = t < 8.5 ? (float)(t / 8.5) : 0.08f;
            compacting = t is >= 8.5 and < 11.0;
            mood = t is >= 11.0 and < 19.0 ? Moods[1 + Math.Min(Moods.Length - 2, (int)(t - 11.0))] : "neutral";
            moodSince = now;
            var loop = (int)(now / DemoSeconds);
            caption = t >= 19.0 && demoHeard != loop;
            if (caption)
            {
                demoHeard = loop;
            }

            link = demoPairing != loop;
            if (link)
            {
                demoPairing = loop;
            }
        }

        if (link)
        {
            Apply(new Change(null, [], "", null, null, Pairing: DemoPairing, PairingSeconds: DemoSeconds - t), now);
        }

        if (changed)
        {
            Apply(new Change(want, want == "speaking" ? DemoEnvelope() : [], want is "thinking" or "speaking" ? "paid" : "", null, null), now);
            if (want == "idle" && t >= 8.0)
            {
                Apply(new Change(null, [], "", null, null, Lease: 10.0), now);
            }
        }

        if (caption)
        {
            Apply(new Change(null, [], "", null, null, Heard: Words.Say("Can you dance?", "Est-ce que tu peux danser ?"), HeardTo: "wake"), now);
        }
    }

    // The demo's pairing link, https://192.168.1.20:8810/#code=ABCD-EFGH, as the brain encodes it (brain/qr.go).
    internal static readonly string[] DemoPairing =
    [
        "11111110001101000101001111111",
        "10000010010100110010001000001",
        "10111010101000111010001011101",
        "10111010110110110111101011101",
        "10111010101010011001001011101",
        "10000010111000111010001000001",
        "11111110101010101010101111111",
        "00000000100110100001000000000",
        "10111110000010110111101111100",
        "01110100010000011001001010001",
        "00001110001001110010010000000",
        "01010100000010000010000101010",
        "10110011000000110110110001100",
        "00101100110100011111001010001",
        "10111011111011111100000001100",
        "00100000010100110001011010010",
        "11011011100110110110110101100",
        "10100100111001011011001010101",
        "10100110101010011110011110100",
        "10101001101010011001110000010",
        "10111110100000001101111110111",
        "00000000111101000010100011111",
        "11111110010111111111101011100",
        "10000010100101011000100010000",
        "10111010100110110100111110111",
        "10111010110110001111010101111",
        "10111010101000010100011011110",
        "10000010010000011001101110010",
        "11111110111110010100011011100",
    ];

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
                if (language is null || !Words.Known(language))
                {
                    error = $"Delta speaks en, fr, or both as fr+en or en+fr, not {lg}";
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

            var hearts = false;
            if (root.TryGetProperty("hearts"u8, out var hs))
            {
                if (hs.ValueKind != JsonValueKind.True)
                {
                    error = "hearts is true or absent";
                    return null;
                }

                hearts = true;
            }

            string? heard = null;
            var heardTo = "";
            if (root.TryGetProperty("heard"u8, out var he))
            {
                heard = he.ValueKind == JsonValueKind.Object && he.TryGetProperty("text"u8, out var ht) && ht.ValueKind == JsonValueKind.String ? ht.GetString() : null;
                heardTo = he.ValueKind == JsonValueKind.Object && he.TryGetProperty("to"u8, out var to) && to.ValueKind == JsonValueKind.String ? to.GetString()! : "";
                if (string.IsNullOrWhiteSpace(heard) || heard.Length > MaxHeard || heardTo.Length is 0 or > 40)
                {
                    error = $"heard is {{\"text\": a sentence of at most {MaxHeard} characters, \"to\": for whom}}";
                    return null;
                }
            }

            string[]? pairing = null;
            var pairingSeconds = 0.0;
            if (root.TryGetProperty("pairing"u8, out var pa) && !ParsePairing(pa, out pairing, out pairingSeconds))
            {
                error = "pairing is {\"rows\": a QR code's rows of 0 and 1, \"seconds\": how long its code lives}";
                return null;
            }

            double? lease = null;
            if (root.TryGetProperty("lease"u8, out var le))
            {
                if (le.ValueKind != JsonValueKind.Number || le.GetDouble() is < 0 or > 3600)
                {
                    error = "lease is a number of seconds, from 0 to 3600";
                    return null;
                }

                lease = le.GetDouble();
            }

            bool? mic = null;
            if (root.TryGetProperty("mic"u8, out var mi))
            {
                if (mi.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "mic is true or false";
                    return null;
                }

                mic = mi.GetBoolean();
            }

            int? guests = null;
            if (root.TryGetProperty("guests"u8, out var gu))
            {
                if (gu.ValueKind != JsonValueKind.Number || !gu.TryGetInt32(out var watching) || watching is < 0 or > 99)
                {
                    error = "guests is a number of viewers, from 0 to 99";
                    return null;
                }

                guests = watching;
            }

            Invite? invitation = null;
            if (root.TryGetProperty("invitation"u8, out var iv) && (invitation = ParseInvitation(iv)) is null)
            {
                error = "invitation is {\"rows\": a QR code's rows of 0 and 1, \"seconds\": how long it serves, \"copied\": true or false} or {\"refused\": watching, off or gateway}";
                return null;
            }

            float? micLevel = null;
            if (root.TryGetProperty("mic_level"u8, out var ml))
            {
                if (ml.ValueKind != JsonValueKind.Number || ml.GetDouble() is < 0 or > 1)
                {
                    error = "mic_level is a number from 0 to 1";
                    return null;
                }

                micLevel = (float)ml.GetDouble();
            }

            int? devices = null;
            if (root.TryGetProperty("devices"u8, out var dv))
            {
                if (dv.ValueKind != JsonValueKind.Number || !dv.TryGetInt32(out var listening) || listening is < 0 or > 99)
                {
                    error = "devices is a number of devices, from 0 to 99";
                    return null;
                }

                devices = listening;
            }

            bool? guestsHear = null;
            if (root.TryGetProperty("guests_hear"u8, out var gh))
            {
                if (gh.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "guests_hear is true or false";
                    return null;
                }

                guestsHear = gh.GetBoolean();
            }

            string? gaia = null;
            if (root.TryGetProperty("gaia"u8, out var ga))
            {
                gaia = ga.ValueKind == JsonValueKind.String ? ga.GetString() : null;
                if (gaia is not ("offline" or "listening" or "connected" or "degraded"))
                {
                    error = "gaia is offline, listening, connected or degraded";
                    return null;
                }
            }

            string? version = null;
            if (root.TryGetProperty("version"u8, out var vr))
            {
                version = vr.ValueKind == JsonValueKind.String ? vr.GetString() : null;
                if (string.IsNullOrWhiteSpace(version) || version.Length > 64)
                {
                    error = "version is the brain's, of at most 64 characters";
                    return null;
                }
            }

            if (!root.TryGetProperty("state"u8, out var s))
            {
                if (context is null && compacting is null && window is null && mood is null && shelf is null && language is null && !hearts && heard is null && pairing is null && lease is null && mic is null && guests is null && invitation is null && micLevel is null && devices is null && version is null && guestsHear is null && gaia is null)
                {
                    error = "no state";
                    return null;
                }

                return new Change(null, [], "", context, compacting, window, mood, shelf, language, hearts, heard, heardTo, pairing, pairingSeconds, lease, mic, guests, invitation, micLevel, devices, version, guestsHear, gaia);
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

            return new Change(state, envelope, cost, context, compacting, window, mood, shelf, language, hearts, heard, heardTo, pairing, pairingSeconds, lease, mic, guests, invitation, micLevel, devices, version, guestsHear, gaia);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // A pairing line's QR code: square, 21 to 177 modules wide by steps of 4, of 0 and 1, with its code's lifetime.
    private static bool ParsePairing(JsonElement p, out string[]? rows, out double seconds)
    {
        rows = null;
        seconds = 0;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("rows"u8, out var r) || r.ValueKind != JsonValueKind.Array ||
            !p.TryGetProperty("seconds"u8, out var t) || t.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        seconds = t.GetDouble();
        var n = r.GetArrayLength();
        if (seconds is <= 0 or > 3600 || n < 21 || n > 177 || (n - 17) % 4 != 0)
        {
            return false;
        }

        var all = new string[n];
        var i = 0;
        foreach (var row in r.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.String || row.GetString() is not { } text || text.Length != n || text.Any(c => c is not ('0' or '1')))
            {
                return false;
            }

            all[i++] = text;
        }

        rows = all;
        return true;
    }

    // An invitation line: a QR code as a pairing line's, with how long it serves and whether the clipboard has its
    // link; or why there is none.
    private static Invite? ParseInvitation(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (p.TryGetProperty("refused"u8, out var r))
        {
            return r.ValueKind == JsonValueKind.String && r.GetString() is "watching" or "off" or "gateway" ? new Invite(null, 0, false, r.GetString()!) : null;
        }

        return ParsePairing(p, out var rows, out var seconds) && p.TryGetProperty("copied"u8, out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? new Invite(rows, seconds, c.GetBoolean())
            : null;
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
        Courses.SelfTest(Check);
        Notes.SelfTest(Check);
        GaiaPresence.SelfTest(Check);
        SceneModulePicking.SelfTest(Check);
        FreeCamera.SelfTest(Check);
        KnotFloor.SelfTest(Check);
        HallShell.SelfTest(Check, Stage.LowFragmentShader);
        HallGallery.SelfTest(Check, Stage.LowFragmentShader);
        LearnLectern.SelfTest(Check);
        GlassWork.SelfTest(Check);
        GalleryPictures.SelfTest(Check);
        MaterialTextures.SelfTest(Check);
        GalleryVideo.SelfTest(Check);
        OfficeSpin.SelfTest(Check);
        Streamline.SelfTest(Check);
        Cinematic.SelfTest(Check);
        ToolWheel.SelfTest(Check);
        WallClocks.SelfTest(Check);
        ArcadeCabinet.SelfTest(Check);
        ScriptScreen.SelfTest(Check);
        ArcadeFilm.SelfTest(Check);
        OrigamiFilm.SelfTest(Check);
        FrameGap.SelfTest(Check);
        MemoryWatch.SelfTest(Check);
        Panel.BrainSelfTest(Check);
        RoomCaptions.SelfTest(Check);
        IdleTour.SelfTest(Check);
        FrameStats.SelfTest(Check);
        WallSource.SelfTest(Check);
        KeptReadings.SelfTest(Check);
        Check(Parse("""{"language":"fr"}""", out _) is { State: null, Language: "fr" }, "the shelf speaks French");
        Check(Parse("""{"language":"de"}""", out _) is null, "an unknown language is refused");
        Check(Parse("""{"hearts":true,"mood":"joy"}""", out _) is { State: null, Hearts: true, Mood: "joy" }, "hearts and a smile");
        Check(Parse("""{"hearts":"yes"}""", out _) is null, "hearts is a boolean");
        Check(Parse("""{"heard":{"text":"Tu peux danser ?","to":"wake"}}""", out _) is { State: null, Heard: "Tu peux danser ?", HeardTo: "wake" }, "a sentence Delta let pass");
        Check(Parse("""{"heard":{"text":"Lance les tests.","to":"claude"}}""", out _) is { Heard: "Lance les tests.", HeardTo: "claude" }, "a sentence for Claude");
        Check(Parse("""{"state":"speaking","envelope":[0.1],"heard":{"text":"Je lance les tests.","to":"said"}}""", out _) is { State: "speaking", Heard: "Je lance les tests.", HeardTo: "said" }, "a sentence Delta says, with its speech");
        Check(Parse("""{"heard":{"text":" ","to":"wake"}}""", out _) is null && Parse("""{"heard":"Tu peux danser ?"}""", out _) is null &&
            Parse("""{"heard":{"text":"Tu peux danser ?"}}""", out _) is null &&
            Parse($$$"""{"heard":{"text":"{{{new string('a', MaxHeard + 1)}}}","to":"wake"}}""", out _) is null, "heard is a sentence and for whom");
        Check(Parse($$$"""{"heard":{"text":"{{{string.Concat(Enumerable.Repeat("😀", 400))}}}","to":"claude"}}""", out _) is { Heard.Length: 800 }, "400 characters as the brain counts them, emoji included");
        Check(Parse("""{"language":"fr+en"}""", out _) is { Language: "fr+en" } && Parse("""{"language":"fr+fr"}""", out _) is null &&
            Parse("""{"language":"fr+en+de"}""", out _) is null, "Delta speaks one language, or two");
        Check(Parse("""{"mic":false}""", out _) is { State: null, Mic: false } && Parse("""{"mic":"off"}""", out _) is null, "the microphone is on or off");
        Words.Speak("en+fr");
        Check(Words.Language == "en" && Words.Setting == "en+fr" && Words.Next("en+fr") == "en" && Words.Next("en") == "fr" && Words.Next("fr") == "fr+en", "the button goes through French, both and English");
        Words.Speak("en");
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
        Check(link.Read(12.5).Hearts == -1f, "no hearts until asked");
        link.Apply(new Change(null, [], "", null, null, Hearts: true), 20.0);
        Check(Math.Abs(link.Read(21.0).Hearts - 1f) < 1e-4 && link.Read(20.0 + HeartSeconds + 0.1).Hearts == -1f, "hearts rise for a few seconds");
        Check(link.Read(29.0) is { Heard: "", HeardAge: -1f, HeardId: 0 }, "no caption until a sentence is heard");
        link.Apply(new Change(null, [], "", null, null, Heard: "Tu peux danser ?", HeardTo: "wake"), 30.0);
        var caption = link.Read(31.0);
        Check(caption is { Heard: "Tu peux danser ?", HeardTo: "wake", HeardId: 1 } && Math.Abs(caption.HeardAge - 1f) < 1e-4 &&
            link.Read(30.0 + HeardSeconds("Tu peux danser ?") + 0.1) is { Heard: "", HeardTo: "", HeardAge: -1f, HeardId: 1 }, "the caption shows for a few seconds");
        link.Apply(new Change(null, [], "", null, null, Heard: "Tu peux danser ?", HeardTo: "wake"), 40.0);
        Check(link.Read(40.5) is { HeardId: 2 }, "the same sentence again is another caption");
        Check(HeardSeconds("Oui.") == 5.0 && HeardSeconds(new string('a', 90)) == 9.0 && HeardSeconds(new string('a', 400)) == 12.0, "a long sentence shows longer, up to twelve seconds");
        string PairingLine(IEnumerable<string> rows, string seconds) => $$$"""{"pairing":{"rows":[{{{string.Join(",", rows.Select(r => $"\"{r}\""))}}}],"seconds":{{{seconds}}}}}""";
        Check(Parse(PairingLine(DemoPairing, "120"), out _) is { State: null, Pairing.Length: 29, PairingSeconds: 120 }, "a pairing link's QR code");
        Check(Parse(PairingLine(DemoPairing.Take(28), "120"), out _) is null && Parse(PairingLine(["0101"], "120"), out _) is null &&
            Parse(PairingLine(DemoPairing.Select((r, i) => i == 3 ? r[..^1] + "2" : r), "120"), out _) is null &&
            Parse(PairingLine(DemoPairing.Select((r, i) => i == 3 ? r[..^1] : r), "120"), out _) is null, "a pairing code is a square of 0 and 1");
        Check(Parse(PairingLine(DemoPairing, "0"), out _) is null && Parse("""{"pairing":{"rows":[]}}""", out _) is null &&
            Parse("""{"pairing":"https://192.168.1.20:8810/#code=ABCD-EFGH"}""", out var noLink) is null && !noLink!.Contains("ABCD"), "a pairing code lives a while, and a link alone is refused unrepeated");
        Check(link.Read(49.0) is { Pairing: null, PairingLeft: -1f, PairingId: 0 }, "no pairing code until a link comes");
        link.Apply(new Change(null, [], "", null, null, Pairing: DemoPairing, PairingSeconds: 120), 50.0);
        var paired = link.Read(60.0);
        Check(paired is { Pairing.Length: 29, PairingId: 1 } && Math.Abs(paired.PairingLeft - 110f) < 1e-3, "the code shows while it lives");
        Check(link.Read(170.5) is { Pairing: null, PairingLeft: 0f, PairingId: 1 }, "an expired code is no longer shown");
        Check(Parse("""{"lease":30}""", out _) is { State: null, Lease: 30.0 } && Parse("""{"lease":0}""", out _) is { Lease: 0.0 }, "how long Delta listens without its name");
        Check(Parse("""{"lease":-1}""", out _) is null && Parse("""{"lease":"30"}""", out _) is null, "a lease is a number of seconds");
        Check(link.Read(179.0) is { Lease: 0f, LeaseLength: 0f }, "Delta needs its name until a conversation begins");
        link.Apply(new Change(null, [], "", null, null, Lease: 30), 180.0);
        var listening = link.Read(190.0);
        Check(Math.Abs(listening.Lease - 20f) < 1e-3 && listening.LeaseLength == 30f && link.Read(210.5) is { Lease: 0f, LeaseLength: 0f }, "the ring empties in 30 s");
        link.Apply(new Change(null, [], "", null, null, Lease: 0), 195.0);
        Check(link.Read(196.0) is { Lease: 0f }, "a sentence for someone else ends the conversation at once");
        Check(Parse("""{"guests":2}""", out _) is { State: null, Guests: 2 } && Parse("""{"guests":-1}""", out _) is null && Parse("""{"guests":"2"}""", out _) is null, "guests is a number of viewers");
        link.Apply(new Change(null, [], "", null, null, Guests: 1), 300.0);
        Check(link.Read(330.0) is { Guests: 1 } && link.Read(361.0) is { Guests: 0 }, "a guest badge nobody renews clears itself after 60 s");
        link.Apply(new Change(null, [], "", null, null, Guests: 2), 362.0);
        link.Apply(new Change(null, [], "", null, null, Guests: 0), 363.0);
        Check(link.Read(364.0) is { Guests: 0 }, "the badge goes when the last guest leaves");
        string InvitationLine(string seconds, string copied) => $$$"""{"invitation":{"rows":[{{{string.Join(",", DemoPairing.Select(r => $"\"{r}\""))}}}],"seconds":{{{seconds}}},"copied":{{{copied}}}}}""";
        Check(Parse(InvitationLine("3600", "true"), out _) is { State: null, Invitation: { Rows.Length: 29, Seconds: 3600, Copied: true } }, "an invitation's QR code");
        Check(Parse(InvitationLine("0", "true"), out _) is null && Parse(InvitationLine("60", "\"yes\""), out _) is null, "an invitation serves a while, and says whether the clipboard has it");
        Check(Parse("""{"invitation":{"refused":"watching"}}""", out _) is { Invitation: { Rows: null, Refused: "watching" } } && Parse("""{"invitation":{"refused":"why"}}""", out _) is null &&
            Parse("""{"invitation":"https://delta.example/#invite=0123"}""", out var noInvite) is null && !noInvite!.Contains("0123"), "a refusal says why, and a link alone is refused unrepeated");
        Check(link.Read(399.0) is { Invitation: null, InvitationId: 0 }, "no invitation until the button asks");
        link.Apply(new Change(null, [], "", null, null, Invitation: new Invite(DemoPairing, 3600, true)), 400.0);
        Check(link.Read(410.0) is { Invitation.Rows.Length: 29, InvitationId: 1 } invited && Math.Abs(invited.InvitationLeft - 3590f) < 1e-2 &&
            link.Read(4001.0) is { Invitation: { Rows: null, Refused: "" }, InvitationLeft: 0f }, "the invitation's code shows while it serves");
        Check(Parse("""{"mic_level":0.042}""", out _) is { State: null, MicLevel: 0.042f } && Parse("""{"mic_level":1.5}""", out _) is null && Parse("""{"mic_level":"0.1"}""", out _) is null, "mic_level is a loudness from 0 to 1");
        var awake = link.Read(4100.0).Quiet;
        link.Apply(new Change(null, [], "", null, null, MicLevel: 0.05f), 4100.0);
        Check(link.Read(4100.2) is { MicLevel: 0.05f } quiet && quiet.Quiet > awake && link.Read(4100.6) is { MicLevel: 0f }, "the meter fades without news, and does not keep the robot awake");
        Check(Parse("""{"language":"fr","mic":true,"devices":1}""", out _) is { Devices: 1, Mic: true } && Parse("""{"devices":-1}""", out _) is null, "devices is a number of devices, in the hello line too");
        link.Apply(new Change(null, [], "", null, null, Devices: 1), 4200.0);
        Check(link.Read(4300.0) is { Devices: 1 }, "a phone stays connected until the brain says otherwise");
        Check(Parse("""{"language":"fr","version":"dev-v0.6.0-40-gc31a28e"}""", out _) is { Version: "dev-v0.6.0-40-gc31a28e" } && Parse("""{"version":""}""", out _) is null &&
            Parse($$$"""{"version":"{{{new string('9', 65)}}}"}""", out _) is null, "version is the brain's, short");
        Check(Parse("""{"guests":1,"guests_hear":true}""", out _) is { Guests: 1, GuestsHear: true } && Parse("""{"guests_hear":"yes"}""", out _) is null, "guests_hear is true or false");
        Check(Parse("""{"language":"fr","gaia":"listening"}""", out _) is { Gaia: "listening" } && Parse("""{"gaia":"up"}""", out _) is null && Parse("""{"gaia":1}""", out _) is null, "gaia is one of four words, in the hello line too");
        link.Apply(new Change(null, [], "", null, null, Gaia: "connected"), 4400.0);
        Check(link.Read(4500.0) is { Gaia: "connected" }, "Gaia stays connected until the brain says otherwise");
        WireSelfTest(Check, PairingLine(DemoPairing, "120"));
        var face = Tray.FaceRgba(64);
        int At(int x, int y) => ((y * 64) + x) * 4;
        Check(face.Length == 64 * 64 * 4 && face[At(21, 29)..(At(21, 29) + 4)].SequenceEqual(new byte[] { 0x59, 0xCC, 0xFF, 0xFF }) && face[At(0, 0) + 3] == 0 &&
            face[At(32, 12)..(At(32, 12) + 4)].SequenceEqual(new byte[] { 0xB3, 0xAD, 0x9E, 0xFF }), "the window's icon is Delta's face in RGBA, cyan eyes on the visor, clear around the shell");
        Console.WriteLine(failures == 0 ? "SELFTEST ok" : $"SELFTEST {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // The listener itself, on a free loopback port: a web page can post to it, as to any port of 127.0.0.1, and a probe
    // can connect and say nothing; neither is the brain.
    private static void WireSelfTest(Action<bool, string> check, string pairingLine)
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var wire = new Link(() => 1.0);
        check(wire.Listen(port) is null, "the link listens on a free port");

        // What fetch("http://127.0.0.1:port/", {method: "POST", mode: "no-cors", body}) sends: a request line and
        // headers, then a body of JSON lines.
        bool WebPage()
        {
            using var page = new TcpClient("127.0.0.1", port);
            var s = page.GetStream();
            s.ReadTimeout = 2000;
            var body = $"\n{pairingLine}\n{{\"heard\":{{\"text\":\"Scanne ce code.\",\"to\":\"said\"}}}}\n";
            s.Write(Encoding.UTF8.GetBytes($"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: http://localhost:8000\r\nContent-Type: text/plain;charset=UTF-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}"));
            try
            {
                return s.Read(new byte[1]) == 0; // the scene hung up
            }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
            {
                return true; // hung up with the body unread: Windows resets rather than closes
            }
            catch (IOException)
            {
                return false; // still open after 2 s
            }
        }

        var dropped = WebPage();
        check(dropped && wire.Read(2.0) is { PairingId: 0, HeardId: 0 }, "a web page's request is dropped at its request line, before its body's lines");

        using var brain = new TcpClient("127.0.0.1", port);
        var bs = brain.GetStream();
        bs.ReadTimeout = 2000;
        bs.Write("{\"language\":\"en\",\"version\":\"dev-wire\"}\n"u8);
        var greeted = SpinWait.SpinUntil(() => wire.Read(2.0).Version == "dev-wire", 2000);
        using (new TcpClient("127.0.0.1", port))
        {
            // a probe, such as another brain asking whether a window listens, connects and says nothing
        }

        WebPage();
        Thread.Sleep(300);
        var told = wire.Tell("{\"shelf\":\"open\"}");
        string? back = null;
        try
        {
            back = new StreamReader(bs).ReadLine();
        }
        catch (IOException)
        {
        }

        check(greeted && told && back == "{\"shelf\":\"open\"}", "the window still answers the brain after a probe and a web page connected");
    }

    internal void Apply(Change change, double at)
    {
        lock (gate)
        {
            if (change.MicLevel is { } loud)
            {
                micLevel = loud;
                micLevelAt = at;
            }
            else
            {
                heard = at; // the microphone's meter alone does not keep the robot awake
            }

            if (change.Hearts)
            {
                heartsSince = at;
            }

            if (change.Mic is { } on)
            {
                micOff = !on;
            }

            if (change.Devices is { } listening)
            {
                devices = listening;
            }

            if (change.GuestsHear is { } hear)
            {
                guestsHear = hear;
            }

            if (change.Gaia is { } bridge)
            {
                gaia = bridge;
            }

            if (change.Version is { } brain)
            {
                version = brain;
            }

            if (change.Guests is { } watching)
            {
                guests = watching;
                guestsUntil = watching > 0 ? at + GuestsSeconds : double.NegativeInfinity;
            }

            if (change.Lease is { } seconds)
            {
                leaseUntil = at + seconds;
                leaseLength = seconds;
            }

            if (change.Pairing is { } code)
            {
                pairing = code;
                pairingUntil = at + change.PairingSeconds;
                pairingId++;
            }

            if (change.Invitation is { } invite)
            {
                invitation = invite;
                invitationUntil = at + invite.Seconds;
                invitationId++;
            }

            if (change.Heard is { } words)
            {
                heardText = words;
                heardTo = change.HeardTo;
                heardSince = at;
                heardId++;
            }

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
            Words.Speak(lang);
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

        if (change.Hearts)
        {
            Console.WriteLine($"SCENE hearts at {at:F2} s");
        }

        if (change.Heard is { } sentence)
        {
            Console.WriteLine($"SCENE heard a sentence of {sentence.Length} characters for {change.HeardTo} at {at:F2} s");
        }

        if (change.Pairing is not null)
        {
            Console.WriteLine($"SCENE pairing code for {change.PairingSeconds:F0} s at {at:F2} s"); // never the code
        }

        if (change.Invitation is { } asked)
        {
            Console.WriteLine(asked.Rows is null ? $"SCENE no invitation ({asked.Refused}) at {at:F2} s" : $"SCENE invitation for {asked.Seconds:F0} s at {at:F2} s"); // never the link
        }
    }

    // Writes a JSON line back to the brain connected now, such as {"language":"fr+en"} when the user picks another
    // language; false when no brain is connected.
    public bool Tell(string json)
    {
        NetworkStream? stream;
        lock (gate)
        {
            stream = talking;
        }

        try
        {
            stream?.Write(Encoding.UTF8.GetBytes(json + "\n"));
            return stream is not null;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var first = true; // a connection is the brain's once its first line is a JSON object
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

                        if (first)
                        {
                            // A browser's request starts with its request line, such as "POST / HTTP/1.1", whatever a
                            // page puts in its body: drop it there, before the body's lines can show a QR code or a
                            // caption. Only then may the connection take the window's answers from the brain's.
                            if (!IsObject(text))
                            {
                                Console.WriteLine("SCENE dropped a connection whose first line is no JSON object, as a web page's request");
                                return;
                            }

                            first = false;
                            lock (gate)
                            {
                                talking = stream;
                            }
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
            finally
            {
                lock (gate)
                {
                    if (talking == stream)
                    {
                        talking = null;
                    }
                }
            }
        }
    }

    private static bool IsObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
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
