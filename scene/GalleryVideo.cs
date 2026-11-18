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
    private bool failed;

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
    }
}
