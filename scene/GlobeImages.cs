using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

// The globes a module's triangles may be painted with (Material.Imaged), which the host keeps and no module ships:
// tools/textures writes each as two square halves, west.png (180° W to 0°) and east.png, into
// %LOCALAPPDATA%\delta\materials\globes\<name>, with globe.json naming its source and credit. They are one 2D array
// image, two layers a globe, with their mipmaps; the Low shader picks the half by u. Without them the array is two
// white 1 × 1 layers and no globe is named, so the triangles keep their colours.
internal sealed class GlobeImages : IDisposable
{
    // The globes the scene looks for, in the order of their layers.
    public static readonly string[] Names = ["earth"];

    private readonly MaterialTextures.ArrayImage image;

    // The globes' pixels, read before any module's triangles are made so that they know which globes are held.
    public sealed record Pixels(byte[][] Layers, int Size, IReadOnlyDictionary<string, int> Held);

    public static Pixels Read(string? dir)
    {
        var layers = new List<byte[]>();
        var held = new Dictionary<string, int>();
        var size = 0;
        if (dir is not null)
        {
            foreach (var name in Names)
            {
                try
                {
                    var halves = new byte[2][];
                    var sizes = new int[2];
                    Parallel.For(0, 2, i =>
                    {
                        halves[i] = Png.Load(Path.Combine(dir, name, i == 0 ? "west.png" : "east.png"), out var w, out var h);
                        sizes[i] = w == h ? w : -1;
                    });
                    if (sizes[0] != sizes[1] || sizes[0] < 1 || (sizes[0] & (sizes[0] - 1)) != 0 || (size != 0 && sizes[0] != size))
                    {
                        throw new InvalidDataException("the halves are not the same square power-of-two size");
                    }

                    size = sizes[0];
                    held[name] = layers.Count;
                    layers.AddRange(halves);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or AggregateException)
                {
                    Console.Error.WriteLine($"SCENE globes: {name} unreadable in {dir} ({(e is AggregateException a ? a.InnerExceptions[0].Message : e.Message)}); its triangles keep their colours");
                }
            }
        }

        if (layers.Count == 0)
        {
            size = 1;
            layers = [[255, 255, 255, 255], [255, 255, 255, 255]];
        }

        return new Pixels([.. layers], size, held);
    }

    public GlobeImages(Device device, PhysicalDevice physical, CommandBuffer upload, Pixels pixels)
    {
        image = new MaterialTextures.ArrayImage(device, physical, upload, VkFormat.R8G8B8A8SRGB, pixels.Size, pixels.Layers, normal: false);
        Held = pixels.Held;
        Size = pixels.Size;
    }

    // Each globe held, by name: its first layer.
    public IReadOnlyDictionary<string, int> Held { get; }

    // A half's side in pixels: the globe is twice as wide.
    public int Size { get; }

    public VkImageView View => image.View;

    // --globes DIR, else %LOCALAPPDATA%\delta\materials\globes; null when no globe is there.
    public static string? Find(string? option)
    {
        var dir = option ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "materials", "globes");
        return Names.Any(n => File.Exists(Path.Combine(dir, n, "west.png")) && File.Exists(Path.Combine(dir, n, "east.png"))) ? dir : null;
    }

    public void FreeStaging() => image.FreeStaging();

    public void Dispose() => image.Dispose();
}
