using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

// An sRGB RGBA image for the fragment shader to sample. The constructor records its upload into a command buffer that
// the caller submits; once that has run, FreeStaging releases the host copy.
internal sealed unsafe class Texture : IDisposable
{
    private readonly Device device;
    private readonly VkImage image;
    private readonly VkDeviceMemory memory;
    private readonly VkBuffer staging;
    private readonly VkDeviceMemory stagingMemory;
    private readonly int byteCount;
    private bool staged = true;
    private bool disposed;
    private static int liveCount;
    public static int LiveCount => Volatile.Read(ref liveCount);

    public Texture(Device device, PhysicalDevice physical, CommandBuffer upload, byte[] rgba, int width, int height)
    {
        this.device = device;
        byteCount = rgba.Length;
        var size = new ByteSize((nuint)rgba.Length);
        staging = device.CreateBuffer(new VkBufferCreateInfo { Size = size, Usage = VkBufferUsageFlags.TransferSrcBit, SharingMode = VkSharingMode.Exclusive }, null);
        stagingMemory = device.AllocateMemory(physical, staging, VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, null);
        device.BindBufferMemory(staging, stagingMemory, memoryOffset: ByteSize.Zero);
        var mapped = (byte*)device.MapMemory<byte>(new MapInfo(stagingMemory) { Region = new MapRegion { Size = size } });
        rgba.CopyTo(new Span<byte>(mapped, rgba.Length));
        device.UnmapMemory(stagingMemory);

        image = device.CreateImage(new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = VkFormat.R8G8B8A8SRGB,
            Extent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        }, null);
        memory = device.AllocateMemory(physical, image, VkMemoryPropertyFlags.DeviceLocalBit, null);
        device.BindImageMemory(image, memory, ByteSize.Zero);
        View = device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R8G8B8A8SRGB,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        }, null);

        upload.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [],
            [Stage.Barrier(image, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, default, VkAccessFlags.TransferWriteBit)]);
        upload.CopyBufferToImage(staging, image, VkImageLayout.TransferDstOptimal,
            [new VkBufferImageCopy { ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 }, ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 } }]);
        upload.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [], [],
            [Stage.Barrier(image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit)]);
        Interlocked.Increment(ref liveCount);
    }

    public VkImageView View { get; }

    // Only after the fence for this texture's frame-in-flight slot has completed. No other slot samples it.
    // Keep staging for animated screens; static overlays continue to call FreeStaging after their first upload.
    public void UpdateAfterFence(CommandBuffer upload, ReadOnlySpan<byte> rgba, int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!staged || rgba.Length != byteCount || byteCount != width * height * 4)
            throw new InvalidDataException("Animated screen upload exceeds its fixed buffer.");
        var mapped = device.MapMemory<byte>(new MapInfo(stagingMemory) { Region = new MapRegion { Size = new ByteSize((nuint)byteCount) } });
        try { rgba.CopyTo(new Span<byte>((byte*)mapped, byteCount)); }
        finally { device.UnmapMemory(stagingMemory); }
        upload.PipelineBarrier(VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [],
            [Stage.Barrier(image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal, VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferWriteBit)]);
        upload.CopyBufferToImage(staging, image, VkImageLayout.TransferDstOptimal,
            [new VkBufferImageCopy { ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 }, ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 } }]);
        upload.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [], [],
            [Stage.Barrier(image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit)]);
    }

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
        if (disposed) return;
        FreeStaging();
        device.DestroyImageView(View, null);
        device.DestroyImage(image, null);
        device.FreeMemory(memory, null);
        disposed = true;
        Interlocked.Decrement(ref liveCount);
    }
}
