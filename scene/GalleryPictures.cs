using System.Security.Cryptography;
using System.Text;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

// What a demo or an artifact shows, painted inside its glass in the gallery (Material.Pictured, HallGallery): a
// screenshot of the page, cropped 4:3 and stretched to a 512 × 512 PNG in %LOCALAPPDATA%\delta\gallery\pictures, named
// by the first 16 hex digits of the SHA-1 of its URL in UTF-8, as the gallery reads it (Uri.AbsoluteUri: an accent is %-escaped). They stay on this machine, since the artifacts are
// private. They are one 2D array image, a layer a picture, with their mipmaps; without them the array is one white
// 1 × 1 layer, no URL is held, and the glasses hold their titles alone.
internal sealed class GalleryPictures : IDisposable
{
    public const int Side = 512;
    public const int Most = 64; // 64 MB of RGBA, a third more with the mipmaps

    private readonly MaterialTextures.ArrayImage image;

    // The pictures' pixels, read before the gallery is built so that it knows which are held: by key, the layer and
    // the picture's mean colour, linear (what the Flat lighting and the path tracer draw).
    public sealed record Pixels(byte[][] Layers, int Size, IReadOnlyDictionary<string, (int Layer, SN.Vector3 Mean)> Held);

    public static string Key(string url) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16];

    public static Pixels Read(string? dir)
    {
        var layers = new List<byte[]>();
        var held = new Dictionary<string, (int, SN.Vector3)>();
        if (dir is not null && Directory.Exists(dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.png").Order(StringComparer.Ordinal))
            {
                var key = Path.GetFileNameWithoutExtension(path);
                if (key.Length != 16 || !key.All(Uri.IsHexDigit) || layers.Count == Most)
                {
                    continue;
                }

                try
                {
                    var rgba = Png.Load(path, out var w, out var h);
                    if (w != Side || h != Side)
                    {
                        throw new InvalidDataException($"{w} x {h}, not {Side} x {Side}");
                    }

                    held[key] = (layers.Count, Mean(rgba));
                    layers.Add(rgba);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"SCENE gallery pictures: {Path.GetFileName(path)} unreadable ({e.Message}); its glass holds its title alone");
                }
            }
        }

        return layers.Count == 0 ? new Pixels([[255, 255, 255, 255]], 1, held) : new Pixels([.. layers], Side, held);
    }

    // The mean of a picture's pixels, each turned linear from sRGB first.
    internal static SN.Vector3 Mean(byte[] rgba)
    {
        var sum = SN.Vector3.Zero;
        var n = rgba.Length / 4;
        for (var i = 0; i < n; i++)
        {
            sum += new SN.Vector3(Linear(rgba[4 * i]), Linear(rgba[(4 * i) + 1]), Linear(rgba[(4 * i) + 2]));
        }

        return n == 0 ? SN.Vector3.One : sum / n;

        static float Linear(byte c) => MathF.Pow(c / 255f, 2.2f);
    }

    public GalleryPictures(Device device, PhysicalDevice physical, CommandBuffer upload, Pixels pixels)
    {
        image = new MaterialTextures.ArrayImage(device, physical, upload, VkFormat.R8G8B8A8SRGB, pixels.Size, pixels.Layers, normal: false);
        Count = pixels.Held.Count;
    }

    public int Count { get; }

    public VkImageView View => image.View;

    // The array itself, whose layers GalleryVideo replaces while a video plays.
    internal MaterialTextures.ArrayImage Image => image;

    // --pictures DIR, else %LOCALAPPDATA%\delta\gallery\pictures.
    public static string Find(string? option) => option ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "gallery", "pictures");

    public void FreeStaging() => image.FreeStaging();

    public void Dispose() => image.Dispose();

    internal static void SelfTest(Action<bool, string> check)
    {
        // The key the screenshots are saved under: the first 16 hex digits of the URL's SHA-1, as the capture wrote them.
        check(Key("https://spareilleux.github.io/learn/threejs-demos/demos/guitar.html") == "6e53c9fc07355339", "a gallery picture's key is its URL's SHA-1, 16 hex digits");
        var grey = Mean([128, 128, 128, 255, 128, 128, 128, 255]);
        check(MathF.Abs(grey.X - 0.2195f) < 0.01f && grey.X == grey.Z, "a gallery picture's mean colour is linear");
        check(Read(null) is { Size: 1, Layers.Length: 1, Held.Count: 0 }, "without pictures the array is one white layer and none is held");
    }
}
