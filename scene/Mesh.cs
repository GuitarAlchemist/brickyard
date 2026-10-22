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

    public Mesh(Device device, PhysicalDevice physical, Vertex[] vertices)
    {
        this.device = device;
        Count = vertices.Length;
        var size = new ByteSize((nuint)(Math.Max(vertices.Length, 1) * sizeof(Vertex)));
        Buffer = device.CreateBuffer(new VkBufferCreateInfo
        {
            Size = size,
            Usage = VkBufferUsageFlags.VertexBufferBit,
            SharingMode = VkSharingMode.Exclusive,
        }, null);
        memory = device.AllocateMemory(physical, Buffer, VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, null);
        device.BindBufferMemory(Buffer, memory, memoryOffset: ByteSize.Zero);
        var mapped = device.MapMemory<Vertex>(new MapInfo(memory) { Region = new MapRegion { Size = size } });
        vertices.AsSpan().CopyTo(new Span<Vertex>((Vertex*)mapped, vertices.Length));
        device.UnmapMemory(memory);
    }

    public VkBuffer Buffer { get; }

    public int Count { get; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        device.DestroyBuffer(Buffer, null);
        device.FreeMemory(memory, null);
        disposed = true;
    }
}

internal static class Boxes
{
    // A box of the given size centred on c, as 12 triangles with one normal per face.
    public static void Add(List<Vertex> o, SN.Vector3 c, SN.Vector3 size, SN.Vector3 color)
    {
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
            SN.Vector3 Corner(float su, float sv) => c + (h * (n + (su * u) + (sv * v)));
            var p00 = Corner(-1, -1);
            var p10 = Corner(1, -1);
            var p11 = Corner(1, 1);
            var p01 = Corner(-1, 1);
            o.AddRange([new(p00, n, color), new(p10, n, color), new(p11, n, color), new(p00, n, color), new(p11, n, color), new(p01, n, color)]);
        }
    }
}
