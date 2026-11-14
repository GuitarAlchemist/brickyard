using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

// The hall's photographed materials (tools/textures packs them from Poly Haven's CC0 scans): for each, its colour with
// its height in alpha, its tangent-space normal and its ambient occlusion, roughness and metalness, three 2D array images
// with a layer per material and their mipmaps, made here on the CPU. The Low shader samples them by the surface's place in
// the world (Material.BrickWall and the rest). Without them, or with one unreadable, the images are 1 × 1 and neutral and
// Photographed is false: the shader draws those surfaces in their flat colours, as before.
internal sealed unsafe class MaterialTextures : IDisposable
{
    // The layers, in the order the shader's Material.BrickWall and the rest index them.
    public static readonly string[] Names = ["brick-wall", "concrete-floor", "concrete-cast", "pallet-wood", "painted-steel"];

    private static readonly string[] Maps = ["albedo.png", "normal.png", "arm.png"];

    private static readonly (int X, int Y)[] Quad = [(0, 0), (1, 0), (0, 1), (1, 1)];

    private readonly ArrayImage[] images;

    // dir: a folder holding Names' folders, each with Maps; null, or one missing, gives the neutral images.
    public MaterialTextures(Device device, PhysicalDevice physical, CommandBuffer upload, string? dir)
    {
        byte[][][]? loaded = null; // [map][layer] RGBA
        var size = 0;
        if (dir is not null)
        {
            try
            {
                loaded = new byte[Maps.Length][][];
                var sizes = new int[Maps.Length, Names.Length];
                for (var m = 0; m < Maps.Length; m++)
                {
                    loaded[m] = new byte[Names.Length][];
                }

                Parallel.For(0, Maps.Length * Names.Length, k =>
                {
                    var (m, l) = (k / Names.Length, k % Names.Length);
                    loaded[m][l] = Png.Load(Path.Combine(dir, Names[l], Maps[m]), out var w, out var h);
                    sizes[m, l] = w == h ? w : -1;
                });
                size = sizes[0, 0];
                foreach (var s in sizes)
                {
                    if (s != size || size < 1 || (size & (size - 1)) != 0)
                    {
                        throw new InvalidDataException("the maps are not all the same square power-of-two size");
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or AggregateException)
            {
                Console.Error.WriteLine($"SCENE materials: {dir} unreadable ({(e is AggregateException a ? a.InnerExceptions[0].Message : e.Message)}); flat colours instead");
                loaded = null;
            }
        }

        Photographed = loaded is not null;
        if (loaded is null)
        {
            size = 1;
            loaded = [[[255, 255, 255, 255]], [[128, 128, 255, 255]], [[255, 200, 0, 255]]];
        }

        images =
        [
            new ArrayImage(device, physical, upload, VkFormat.R8G8B8A8SRGB, size, loaded[0], normal: false),
            new ArrayImage(device, physical, upload, VkFormat.R8G8B8A8UNorm, size, loaded[1], normal: true),
            new ArrayImage(device, physical, upload, VkFormat.R8G8B8A8UNorm, size, loaded[2], normal: false),
        ];
        Size = size;
    }

    public bool Photographed { get; }

    public int Size { get; }

    public VkImageView Albedo => images[0].View;

    public VkImageView Normal => images[1].View;

    public VkImageView Arm => images[2].View;

    // The folder the scene reads: --textures DIR, else %LOCALAPPDATA%\delta\textures; null when a map is missing there.
    public static string? Find(string? option)
    {
        var dir = option ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "textures");
        return Names.All(n => Maps.All(m => File.Exists(Path.Combine(dir, n, m)))) ? dir : null;
    }

    // Once the upload has run.
    public void FreeStaging()
    {
        foreach (var image in images)
        {
            image.FreeStaging();
        }
    }

    public void Dispose()
    {
        foreach (var image in images)
        {
            image.Dispose();
        }
    }

    // RGBA, each mip level half the last, down to 1 × 1: colour and height averaged, a normal averaged then made unit again.
    private static List<byte[]> Chain(byte[] top, int size, bool normal)
    {
        var levels = new List<byte[]> { top };
        for (var s = size / 2; s >= 1; s /= 2)
        {
            var up = levels[^1];
            var down = new byte[s * s * 4];
            var sum = new float[4];
            for (var y = 0; y < s; y++)
            {
                for (var x = 0; x < s; x++)
                {
                    Array.Clear(sum);
                    foreach (var (dx, dy) in Quad)
                    {
                        var at = ((((2 * y) + dy) * 2 * s) + (2 * x) + dx) * 4;
                        for (var c = 0; c < 4; c++)
                        {
                            sum[c] += up[at + c];
                        }
                    }

                    var to = ((y * s) + x) * 4;
                    if (normal)
                    {
                        var (nx, ny, nz) = ((sum[0] / 510f) - 1f, (sum[1] / 510f) - 1f, (sum[2] / 510f) - 1f);
                        var length = MathF.Max(MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz)), 1e-4f);
                        (sum[0], sum[1], sum[2]) = (((nx / length) + 1f) * 510f, ((ny / length) + 1f) * 510f, ((nz / length) + 1f) * 510f);
                    }

                    for (var c = 0; c < 4; c++)
                    {
                        down[to + c] = (byte)Math.Clamp((int)MathF.Round(sum[c] / 4f), 0, 255);
                    }
                }
            }

            levels.Add(down);
        }

        return levels;
    }

    private sealed class ArrayImage : IDisposable
    {
        private readonly Device device;
        private readonly VkImage image;
        private readonly VkDeviceMemory memory;
        private VkBuffer staging;
        private VkDeviceMemory stagingMemory;
        private bool staged = true;

        public ArrayImage(Device device, PhysicalDevice physical, CommandBuffer upload, VkFormat format, int size, byte[][] layers, bool normal)
        {
            this.device = device;
            var chains = new List<byte[]>[layers.Length];
            Parallel.For(0, layers.Length, l => chains[l] = Chain(layers[l], size, normal));
            var levelCount = chains[0].Count;
            var total = chains.Sum(c => c.Sum(level => (long)level.Length));
            var bytes = new ByteSize((nuint)total);
            staging = device.CreateBuffer(new VkBufferCreateInfo { Size = bytes, Usage = VkBufferUsageFlags.TransferSrcBit, SharingMode = VkSharingMode.Exclusive }, null);
            stagingMemory = device.AllocateMemory(physical, staging, VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, null);
            device.BindBufferMemory(staging, stagingMemory, memoryOffset: ByteSize.Zero);
            var mapped = (byte*)device.MapMemory<byte>(new MapInfo(stagingMemory) { Region = new MapRegion { Size = bytes } });
            var copies = new VkBufferImageCopy[layers.Length * levelCount];
            long offset = 0;
            for (var l = 0; l < layers.Length; l++)
            {
                for (var level = 0; level < levelCount; level++)
                {
                    var data = chains[l][level];
                    data.CopyTo(new Span<byte>(mapped + offset, data.Length));
                    var s = Math.Max(1, size >> level);
                    copies[(l * levelCount) + level] = new VkBufferImageCopy
                    {
                        BufferOffset = new ByteSize64((ulong)offset),
                        ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, BaseArrayLayer = l, LayerCount = 1 },
                        ImageExtent = new VkExtent3D { Width = s, Height = s, Depth = 1 },
                    };
                    offset += data.Length;
                }
            }

            device.UnmapMemory(stagingMemory);
            image = device.CreateImage(new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = size, Height = size, Depth = 1 },
                MipLevels = levelCount,
                ArrayLayers = layers.Length,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            }, null);
            memory = device.AllocateMemory(physical, image, VkMemoryPropertyFlags.DeviceLocalBit, null);
            device.BindImageMemory(image, memory, ByteSize.Zero);
            var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = levelCount, LayerCount = layers.Length };
            View = device.CreateImageView(new VkImageViewCreateInfo { Image = image, ViewType = VkImageViewType._2DArray, Format = format, SubresourceRange = range }, null);
            upload.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [],
                [Barrier(image, range, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, default, VkAccessFlags.TransferWriteBit)]);
            upload.CopyBufferToImage(staging, image, VkImageLayout.TransferDstOptimal, copies);
            upload.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [], [],
                [Barrier(image, range, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit)]);
        }

        public VkImageView View { get; }

        public void FreeStaging()
        {
            if (staged)
            {
                device.DestroyBuffer(staging, null);
                device.FreeMemory(stagingMemory, null);
                staged = false;
            }
        }

        public void Dispose()
        {
            FreeStaging();
            device.DestroyImageView(View, null);
            device.DestroyImage(image, null);
            device.FreeMemory(memory, null);
        }

        private static VkImageMemoryBarrier Barrier(VkImage image, VkImageSubresourceRange range, VkImageLayout from, VkImageLayout to, VkAccessFlags src, VkAccessFlags dst) => new()
        {
            SrcAccessMask = src,
            DstAccessMask = dst,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = image,
            SubresourceRange = range,
        };
    }
}
