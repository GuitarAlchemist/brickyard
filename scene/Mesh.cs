using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

// A part's vertices in a host-visible vertex buffer, made from the vertices and destroyed by Dispose: ga-brutal's
// HallSlice SliceMesh, without its count of live meshes.
internal sealed unsafe class Mesh : IDisposable
{
    private readonly Device device;
    private readonly VkDeviceMemory memory;
    private bool disposed;
    private static int liveCount;
    public static int LiveCount => Volatile.Read(ref liveCount);

    // resident: in the GPU's own memory where the driver lets the host write it (resizable BAR), else as any mesh. For a
    // mesh so large that reading it from the host's memory, at every pass, is what the frame waits on: the knots' rope
    // (1.2 million vertices, 15 ms a frame from host memory, at any size of window).
    public Mesh(Device device, PhysicalDevice physical, Vertex[] vertices, bool resident = false)
    {
        this.device = device;
        Count = vertices.Length;
        var size = new ByteSize((nuint)(Math.Max(vertices.Length, 1) * sizeof(Vertex)));
        Buffer = device.CreateBuffer(new VkBufferCreateInfo
        {
            Size = size,
            Usage = RayTraced
                ? VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit | VkBufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKHR
                : VkBufferUsageFlags.VertexBufferBit,
            SharingMode = VkSharingMode.Exclusive,
        }, null);
        var allocated = false;
        try
        {
            const VkMemoryPropertyFlags host = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit;
            VkDeviceMemory With(VkMemoryPropertyFlags wanted) => RayTraced
                ? Gpu.Allocate(device, physical, Buffer, wanted)
                : device.AllocateMemory(physical, Buffer, wanted, null);
            if (resident)
            {
                try
                {
                    memory = With(host | VkMemoryPropertyFlags.DeviceLocalBit);
                    Resident = true;
                }
                catch (Exception)
                {
                    memory = With(host);
                }
            }
            else
            {
                memory = With(host);
            }

            allocated = true;
            device.BindBufferMemory(Buffer, memory, memoryOffset: ByteSize.Zero);
            var mapped = device.MapMemory<Vertex>(new MapInfo(memory) { Region = new MapRegion { Size = size } });
            try { vertices.AsSpan().CopyTo(new Span<Vertex>((Vertex*)mapped, vertices.Length)); }
            finally { device.UnmapMemory(memory); }
        }
        catch
        {
            device.DestroyBuffer(Buffer, null);
            if (allocated) device.FreeMemory(memory, null);
            throw;
        }
        Interlocked.Increment(ref liveCount);
    }

    // Set once at start-up when the scene traces rays (RayScene): every mesh's vertices then also feed the GPU's
    // acceleration structures, by their device address.
    public static bool RayTraced { get; set; }

    public VkBuffer Buffer { get; }

    public int Count { get; }

    // In the GPU's own memory (resident, where the driver allowed it).
    public bool Resident { get; }

    public bool Disposed => disposed;

    // The mesh's bottom-level structure, once RayScene built it; disposed with the mesh.
    internal Blas? Blas { get; set; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Blas?.Dispose();
        Blas = null;

        device.DestroyBuffer(Buffer, null);
        device.FreeMemory(memory, null);
        Interlocked.Decrement(ref liveCount);
        disposed = true;
    }
}

// What a surface is made of, which the Low shader's highlights and reflections follow (Stage); Flat draws them all alike.
internal enum Material : uint
{
    Matte,
    Enamel,
    Aluminium,
    Steel,
    Rubber,
    Glass,

    // Drawn by the Low shader from Edge: x the place along a strand or a post, in metres, y the angle round it (0 to
    // 2π), z its radius. Rope: a laid rope's strand, its four yarns twisted against it and their fibres; Wood: turned
    // oak, its grain along it. Flat draws them in their colour.
    Rope,
    Wood,

    // Photographed (MaterialTextures, in its Names' order from BrickWall): the Low shader maps the scan onto the surface
    // by its place in the world and draws its relief; the colour is then a tint on the scan's own, about 1. ClayBrick
    // takes only the cast concrete's grain and relief, on its own colour. Without the scans they are matte, in their colour.
    BrickWall = 8,
    ConcreteFloor,
    ConcreteCast,
    PalletWood,
    PaintedSteel,
    ClayBrick,

    // A module's triangle painted with one of the host's globe images (SceneModuleGeometry.Images, GlobeImages): Edge
    // carries its place on the image and the image's first layer. The Flat lighting draws it in its colour.
    Imaged = 16,

    // A gallery picture inside a demo's or an artifact's glass (GalleryPictures, HallGallery.Picture): Edge carries its
    // place on the picture, from its top left, and its layer. Flat lighting and the path tracer draw its mean colour.
    Pictured = 17,

    // Oak boards, drawn by the Low shader from a box's Edge: flat-sawn grain along the face's longer side, its pores, a
    // sheen that moves with the eye, under a clear coat. The path tracer and Flat draw them in their colour.
    Oak = 18,

    // A city building's wall (OttawaStreets): the Low shader draws a window every 2.2 m along it (its Tangent) and on
    // every floor, dark glass that takes the sky's light; Edge.w varies the panes from building to building. The path
    // tracer and Flat draw the wall in its colour.
    Facade = 19,

    // A triangle of Google's Photorealistic 3D Tiles (GoogleStreets): Edge carries its place on its tile's image and the
    // image's layer (Program's aerialMaps). The photograph carries its own light, so the Low shader draws it unlit, in
    // the haze; Flat and the path tracer draw its colour sampled at each corner.
    Aerial = 20,

    // A projection screen's matte white cloth (GalleryVideo.ScreenModel): the Low shader projects the video on it alone,
    // in the projector's light rather than tinted by what it falls on. Flat and the path tracer draw it white.
    Screen = 21,

    // Painted concrete block (Gymnasium's walls): the Low shader lays 40 by 20 cm blocks in running bond by their place
    // in the world, the joints recessed and darker, the block's pores under a satin paint in its colour, and a blue band
    // at 2.3 m. Flat and the path tracer draw its colour.
    Block = 22,
}

// A surface's colour and what it is made of.
internal readonly record struct Finish(SN.Vector3 Color, Material Material);

internal static class Boxes
{
    // A box of the given size centred on c, as 12 triangles with one normal per face. Each vertex also carries its face's
    // first axis and where it lies on the face, for the Low shader's rounded edges.
    public static void Add(List<Vertex> o, SN.Vector3 c, SN.Vector3 size, SN.Vector3 color) => Add(o, c, size, new Finish(color, Material.Matte));

    public static void Add(List<Vertex> o, SN.Vector3 c, SN.Vector3 size, Finish finish)
    {
        var (color, material) = finish;
        var h = size / 2f;
        ReadOnlySpan<(SN.Vector3 N, SN.Vector3 U, SN.Vector3 V)> faces =
        [
            (SN.Vector3.UnitX, SN.Vector3.UnitY, SN.Vector3.UnitZ),
            (-SN.Vector3.UnitX, SN.Vector3.UnitZ, SN.Vector3.UnitY),
            (SN.Vector3.UnitY, SN.Vector3.UnitZ, SN.Vector3.UnitX),
            (-SN.Vector3.UnitY, SN.Vector3.UnitX, SN.Vector3.UnitZ),
            (SN.Vector3.UnitZ, SN.Vector3.UnitX, SN.Vector3.UnitY),
            (-SN.Vector3.UnitZ, SN.Vector3.UnitY, SN.Vector3.UnitX),
        ];
        foreach (var (n, u, v) in faces)
        {
            var hu = SN.Vector3.Dot(h, SN.Vector3.Abs(u));
            var hv = SN.Vector3.Dot(h, SN.Vector3.Abs(v));
            Vertex Corner(float su, float sv) => new(c + (h * (n + (su * u) + (sv * v))), n, color, material, u, new(su, sv, hu, hv));
            var p00 = Corner(-1, -1);
            var p10 = Corner(1, -1);
            var p11 = Corner(1, 1);
            var p01 = Corner(-1, 1);
            o.AddRange([p00, p10, p11, p00, p11, p01]);
        }
    }
}
