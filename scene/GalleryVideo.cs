using System.ComponentModel;
using System.Diagnostics;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

// The demos' videos playing inside their water globes (tools/gallery-pictures/videos.ps1 cuts them, 512 × 512, into
// %LOCALAPPDATA%\delta\gallery\videos under their pictures' keys). When the eye comes within Near of globes whose picture
// and video are both held, FFmpeg decodes each one's video, looping at its own pace, into RGBA on a thread of its own;
// each new image, its mips made there, replaces the picture's layer in the gallery's array (GalleryPictures) at the start
// of the next frame. Past Far a decoder stops and the still picture comes back. At most Most play at once, the nearest.
// Without FFmpeg on PATH, or without videos, the globes keep their still pictures.
internal sealed unsafe class GalleryVideo : IDisposable
{
    public const float Near = 4.5f, Far = 5f; // metres from the eye to a globe's centre: starts within Near, stops past Far (the gallery's view stands 3.6 m off)
    public const int Most = 4;
    private const int Side = GalleryPictures.Side;

    private static readonly int Bytes = MaterialTextures.ChainBytes(Side);

    private readonly Device device;
    private readonly MaterialTextures.ArrayImage image;
    private readonly Dictionary<string, (string File, int Layer, byte[] Still)> videos = new(StringComparer.Ordinal);
    private readonly string? ffmpeg;
    private readonly int room; // images a frame's staging holds: as many stills put back, then as many playing
    private readonly VkBuffer[] buffers;
    private readonly VkDeviceMemory[] memories;
    private readonly nint[] mapped;
    private readonly Dictionary<string, Player> playing = new(StringComparer.Ordinal);
    private readonly Queue<(int Layer, byte[] Still)> restores = new(); // stopped videos' layers, their pictures to put back
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private bool failed;
    private double lastUpdate;
    private IReadOnlyList<HallGallery.Globe>? placedFor;
    private SN.Vector4 screen;

    // The video projected on the gallery's wall, as the Low shader reads it (Frame): the nearest one playing, its
    // picture's layer, its strength (fading in and out over Fade seconds; 0: none), and where (Place).
    internal readonly record struct Projection(float Layer, float Strength, SN.Vector4 Screen)
    {
        public static readonly Projection None = new(0f, 0f, default);
    }

    public const float Fade = 0.8f;

    public Projection Projected { get; private set; } = Projection.None;

    // Where the wall shows the projection: over the demos' water globes on the left wall, centred on them along it, 4:3,
    // its foot 0.45 m over their tops; as (its centre's z, its centre's y, its half width, the wall's x). Zero when the
    // gallery has no water globe.
    internal static SN.Vector4 Place(IEnumerable<HallGallery.Globe> globes, float wallX)
    {
        var (lo, hi, top, any) = (float.MaxValue, float.MinValue, float.MinValue, false);
        foreach (var g in globes.Where(g => !g.Dome))
        {
            var c = g.World.Translation;
            (lo, hi, top, any) = (MathF.Min(lo, c.Z), MathF.Max(hi, c.Z), MathF.Max(top, c.Y + g.Radius), true);
        }

        if (!any)
        {
            return default;
        }

        var halfWidth = Math.Clamp(((hi - lo) / 2f) + 0.15f, 0.6f, 1.1f);
        return new SN.Vector4((lo + hi) / 2f, top + 0.45f + (halfWidth * 0.75f), halfWidth, wallX);
    }

    // A pull-down projection screen where Place puts the picture (the user, 2026-11-18: « je veux un vrai ecran de
    // projection avec un fonds blanc »; on the wall's concrete the video's lines beat against the scan's into moiré):
    // the white cloth just over the picture, a black border round it, and its roller's case above, on the wall.
    internal static Vertex[] ScreenModel(SN.Vector4 place)
    {
        var (z, y, halfWidth, wallX) = (place.X, place.Y, place.Z, place.W);
        var halfHeight = halfWidth * 0.75f;
        const float border = 0.06f;
        var o = new List<Vertex>();
        var cloth = new Finish(new(0.86f, 0.86f, 0.84f), Material.Screen);
        var black = new Finish(new(0.03f, 0.03f, 0.035f), Material.Matte);
        var case_ = new Finish(new(0.8f, 0.81f, 0.82f), Material.Aluminium);
        Boxes.Add(o, new(wallX + 0.012f, y, z), new(0.008f, 2f * (halfHeight + 0.01f), 2f * (halfWidth + 0.01f)), cloth);
        Boxes.Add(o, new(wallX + 0.014f, y + halfHeight + (border / 2f), z), new(0.01f, border, 2f * (halfWidth + border)), black);
        Boxes.Add(o, new(wallX + 0.014f, y - halfHeight - (border / 2f), z), new(0.01f, border, 2f * (halfWidth + border)), black);
        Boxes.Add(o, new(wallX + 0.014f, y, z + halfWidth + (border / 2f)), new(0.01f, 2f * halfHeight, border), black);
        Boxes.Add(o, new(wallX + 0.014f, y, z - halfWidth - (border / 2f)), new(0.01f, 2f * halfHeight, border), black);
        Boxes.Add(o, new(wallX + 0.05f, y + halfHeight + border + 0.05f, z), new(0.09f, 0.09f, 2f * (halfWidth + border) + 0.16f), case_);
        Boxes.Add(o, new(wallX + 0.03f, y - halfHeight - border - 0.012f, z), new(0.03f, 0.024f, 2f * (halfWidth + border) + 0.04f), black); // the weighted bar
        return [.. o];
    }

    public GalleryVideo(Device device, PhysicalDevice physical, GalleryPictures pictures, GalleryPictures.Pixels pixels, string? dir, int framesInFlight)
    {
        this.device = device;
        image = pictures.Image;
        ffmpeg = Find("ffmpeg.exe");
        if (dir is not null && Directory.Exists(dir) && pixels.Size == Side)
        {
            foreach (var (key, held) in pixels.Held)
            {
                var file = Path.Combine(dir, key + ".mp4");
                if (File.Exists(file))
                {
                    var still = new byte[Bytes];
                    MaterialTextures.Chain(pixels.Layers[held.Layer], still, Side);
                    videos[key] = (file, held.Layer, still);
                }
            }
        }

        // Each frame in flight its own staging.
        room = Math.Min(videos.Count, Most);
        var count = room > 0 ? framesInFlight : 0;
        (buffers, memories, mapped) = (new VkBuffer[count], new VkDeviceMemory[count], new nint[count]);
        var size = new ByteSize((nuint)(2 * room * Bytes));
        for (var i = 0; i < count; i++)
        {
            buffers[i] = device.CreateBuffer(new VkBufferCreateInfo { Size = size, Usage = VkBufferUsageFlags.TransferSrcBit, SharingMode = VkSharingMode.Exclusive }, null);
            memories[i] = device.AllocateMemory(physical, buffers[i], VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, null);
            device.BindBufferMemory(buffers[i], memories[i], memoryOffset: ByteSize.Zero);
            mapped[i] = (nint)device.MapMemory<byte>(new MapInfo(memories[i]) { Region = new MapRegion { Size = size } });
        }

        Console.WriteLine(videos.Count == 0 ? "SCENE gallery videos: none" : ffmpeg is null
            ? $"SCENE gallery videos: {videos.Count} held, but no ffmpeg on PATH: their globes keep their pictures"
            : $"SCENE gallery videos: {videos.Count} from {dir}, played by {ffmpeg}");
    }

    // --videos DIR, else %LOCALAPPDATA%\delta\gallery\videos.
    public static string Dir(string? option) => option ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "gallery", "videos");

    // The globes whose videos should play for an eye there, nearest first, at most most: those held within Near, and
    // those playing while they stay within Far.
    internal static List<string> Pick(SN.Vector3 eye, IEnumerable<HallGallery.Globe> globes, Func<string, bool> held, Func<string, bool> playing, int most)
    {
        var near = new List<(string Key, float D)>();
        foreach (var g in globes)
        {
            var d = SN.Vector3.Distance(eye, g.World.Translation);
            if (held(g.Key) && (d < Near || (d <= Far && playing(g.Key))))
            {
                near.Add((g.Key, d));
            }
        }

        return [.. near.OrderBy(n => n.D).Take(most).Select(n => n.Key)];
    }

    // On the render thread, before Record: starts and stops decoders for where the eye is.
    public void Update(SN.Vector3 eye, IReadOnlyList<HallGallery.Globe> globes)
    {
        if (ffmpeg is null || failed || room == 0)
        {
            return;
        }

        var want = Pick(eye, globes, videos.ContainsKey, playing.ContainsKey, room);
        Project(want.Count > 0 ? videos[want[0]].Layer : null, globes);
        foreach (var key in playing.Keys.Where(k => !want.Contains(k)).ToList())
        {
            Stop(key);
        }

        foreach (var key in want.Where(k => !playing.ContainsKey(k)))
        {
            var (file, layer, _) = videos[key];
            try
            {
                playing[key] = new Player(ffmpeg, file, key, layer);
                Console.WriteLine($"SCENE gallery video: playing {key}");
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                failed = true;
                Console.Error.WriteLine($"SCENE gallery video: ffmpeg did not start ({e.Message}); the globes keep their pictures");
                foreach (var k in playing.Keys.ToList())
                {
                    Stop(k);
                }

                return;
            }
        }
    }

    // The nearest video wanted (its layer) fades in on the wall; with none, the last one fades out.
    private void Project(int? layer, IReadOnlyList<HallGallery.Globe> globes)
    {
        if (!ReferenceEquals(globes, placedFor))
        {
            placedFor = globes;
            screen = Place(globes, HallGallery.Footprint.Centre.X - HallGallery.Footprint.Half.X);
            Console.WriteLine($"SCENE gallery videos: projected on the left wall at z {screen.X:F2}, y {screen.Y:F2}, {2f * screen.Z:F2} m wide");
        }

        var now = clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Min(now - lastUpdate, 0.1);
        lastUpdate = now;
        var strength = Math.Clamp(Projected.Strength + ((layer is null ? -dt : dt) / Fade), 0f, 1f);
        Projected = new(layer ?? Projected.Layer, screen == default ? 0f : strength, screen);
    }

    // After cb.Begin, outside any render pass: stills put back, then the playing videos' latest images, each in this
    // frame's staging.
    public void Record(CommandBuffer cb, int slot)
    {
        if (buffers.Length == 0)
        {
            return;
        }

        var at = (byte*)mapped[slot];
        long offset = 0;
        for (var n = 0; n < room && restores.TryDequeue(out var r); n++)
        {
            r.Still.CopyTo(new Span<byte>(at + offset, Bytes));
            image.Replace(cb, buffers[slot], offset, r.Layer);
            offset += Bytes;
        }

        foreach (var player in playing.Values)
        {
            if (player.Take(new Span<byte>(at + offset, Bytes)))
            {
                image.Replace(cb, buffers[slot], offset, player.Layer);
                offset += Bytes;
            }
        }
    }

    public void Dispose()
    {
        foreach (var player in playing.Values)
        {
            player.Dispose();
        }

        playing.Clear();
        for (var i = 0; i < buffers.Length; i++)
        {
            device.UnmapMemory(memories[i]);
            device.DestroyBuffer(buffers[i], null);
            device.FreeMemory(memories[i], null);
        }
    }

    private void Stop(string key)
    {
        var player = playing[key];
        Console.WriteLine($"SCENE gallery video: {key} stopped after {player.Shown} images{(player.Ended is { } why ? $" ({why})" : "")}");
        restores.Enqueue((player.Layer, videos[key].Still));
        player.Dispose();
        playing.Remove(key);
    }

    private static string? Find(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), exe)).FirstOrDefault(File.Exists);

    // One video's decoder: FFmpeg's raw RGBA read whole images at a time, each made a flat mip chain into the back
    // buffer, which is then swapped with the one ready. Take swaps the ready one out to the render thread.
    private sealed class Player : IDisposable
    {
        private readonly Process process;
        private readonly Thread reader;
        private readonly Lock gate = new();
        private byte[] back = new byte[Bytes], ready = new byte[Bytes], front = new byte[Bytes];
        private bool fresh;
        private volatile bool stopping;

        public Player(string ffmpeg, string file, string key, int layer)
        {
            (Key, Layer) = (key, layer);
            var info = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var a in (string[])["-nostdin", "-hide_banner", "-loglevel", "error", "-stream_loop", "-1", "-re", "-i", file, "-an",
                "-vf", $"scale={Side}:{Side}", "-f", "rawvideo", "-pix_fmt", "rgba", "-"])
            {
                info.ArgumentList.Add(a);
            }

            process = Process.Start(info) ?? throw new InvalidOperationException("no process");
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data) && !stopping)
                {
                    Console.Error.WriteLine($"SCENE gallery video: ffmpeg: {e.Data}");
                }
            };
            process.BeginErrorReadLine();
            reader = new Thread(Read) { IsBackground = true, Name = "gallery video" };
            reader.Start();
        }

        public string Key { get; }

        public int Layer { get; }

        public int Shown { get; private set; }

        public string? Ended { get; private set; }

        // Copies the latest image not yet taken into to; false when none came since.
        public bool Take(Span<byte> to)
        {
            lock (gate)
            {
                if (!fresh)
                {
                    return false;
                }

                (front, ready, fresh) = (ready, front, false);
            }

            front.CopyTo(to);
            Shown++;
            return true;
        }

        public void Dispose()
        {
            stopping = true;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // it ended on its own
            }

            reader.Join(1000);
            process.Dispose();
        }

        private void Read()
        {
            var raw = new byte[Side * Side * 4];
            var stream = process.StandardOutput.BaseStream;
            try
            {
                while (!stopping)
                {
                    stream.ReadExactly(raw);
                    MaterialTextures.Chain(raw, back, Side);
                    lock (gate)
                    {
                        (back, ready, fresh) = (ready, back, true);
                    }
                }
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
            {
                if (!stopping)
                {
                    Ended = "the decoder ended";
                    Console.Error.WriteLine($"SCENE gallery video: {Key}'s decoder ended; its last image stays");
                }
            }
        }
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        static HallGallery.Globe At(float x, string key) => new(SN.Matrix4x4.CreateTranslation(x, 1f, 0f), 0.1f, false, SN.Vector3.One, key);
        HallGallery.Globe[] globes = [At(0f, "a"), At(2f, "b"), At(3f, "c"), At(2.2f, "d")];
        static bool Held(string k) => k != "c";
        static bool None(string k) => false;
        check(Pick(new(2.9f, 1f, 0f), globes, Held, None, 4) is ["d", "b", "a"], "gallery videos play in the held globes within reach, nearest first");
        check(Pick(new(2.9f, 1f, 0f), globes, Held, None, 2) is ["d", "b"], "at most so many gallery videos play, the nearest");
        check(Pick(new(-4.7f, 1f, 0f), globes, Held, k => k == "a", 4) is ["a"] && Pick(new(-4.7f, 1f, 0f), globes, Held, None, 4) is [],
            "a playing video goes on to Far, and none starts past Near");
        check(Pick(new(-5.1f, 1f, 0f), globes, Held, k => k == "a", 4) is [], "a playing video stops past Far");
        var screen = Place([At(0f, "a") with { World = SN.Matrix4x4.CreateTranslation(-6f, 1f, 0f) }, At(0f, "b") with { World = SN.Matrix4x4.CreateTranslation(-6f, 1.4f, 2f) },
            At(0f, "p") with { Dome = true, World = SN.Matrix4x4.CreateTranslation(-6f, 3f, 9f) }], -6.25f);
        check(MathF.Abs(screen.X - 1f) < 1e-5f && screen.Z == 1.1f && MathF.Abs(screen.Y - 2.775f) < 1e-5f && screen.W == -6.25f, "the projection is centred over the water globes, 4:3, above them, paperweights aside");
        check(Place([], -6.25f) == default, "no water globe, no projection");
    }
}
