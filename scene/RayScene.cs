using System.Runtime.InteropServices;
using Brutal;
using Brutal.Pointers;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

// The hall for ray queries (VK_KHR_ray_query): the Low shader traces its key light's shadows and the ambient occlusion
// through it instead of painting them. Each mesh gets a bottom-level structure of its triangles the first time it is
// drawn, kept until the mesh is disposed; each frame in flight has a top-level one, rebuilt at the start of the frame
// from the meshes and world transforms the last frame drew (one frame behind, 16 ms, which no shadow shows).
internal sealed unsafe class RayScene : IDisposable
{
    public static readonly string[] Extensions = ["VK_KHR_acceleration_structure", "VK_KHR_ray_query", "VK_KHR_deferred_host_operations"];

    public const int MaxInstances = 4096;

    private readonly Device device;
    private readonly PhysicalDevice physical;
    private readonly Slot[] slots;
    private readonly List<(Gpu.Buffer Buffer, int Frames)> graveyard = []; // scratch kept until no frame can still use it
    private readonly List<Blas> built = [];

    public RayScene(Device device, PhysicalDevice physical, int frames)
    {
        this.device = device;
        this.physical = physical;
        slots = new Slot[frames];
        for (var i = 0; i < frames; i++)
        {
            slots[i] = new Slot(this);
        }
    }

    // A frame's meshes, their world transforms' three rows (Push's Row0 to Row2) and their glow, as the frame drew them.
    public List<(Mesh Mesh, System.Numerics.Vector4 R0, System.Numerics.Vector4 R1, System.Numerics.Vector4 R2, System.Numerics.Vector4 Glow)> Drawn { get; private set; } = [];

    private List<(Mesh Mesh, System.Numerics.Vector4 R0, System.Numerics.Vector4 R1, System.Numerics.Vector4 R2, System.Numerics.Vector4 Glow)> last = [];

    // Where each drawn mesh stood at the structure before, by the mesh and its turn among its own draws (the robot's
    // parts are drawn twice): the path tracer follows a moving surface back to its last place (PathTracer).
    private Dictionary<(Mesh, int), (System.Numerics.Vector4, System.Numerics.Vector4, System.Numerics.Vector4)> before = [];
    private Dictionary<(Mesh, int), (System.Numerics.Vector4, System.Numerics.Vector4, System.Numerics.Vector4)> now = [];

    // Each slot's table of what its instances are, in the top level's order (InstanceInfo), for the path tracer.
    public Gpu.Buffer Infos(int slot) => slots[slot].Infos;

    public int Instances { get; private set; }

    public int Structures => built.Count;

    public VkAccelerationStructureKHR Top(int slot) => slots[slot].Tlas;

    // Whether the GPU can: the extensions and the three features.
    public static bool Offered(PhysicalDevice physical, HashSet<string> extensions)
    {
        if (!Extensions.All(extensions.Contains))
        {
            return false;
        }

        var query = Features(out var v12, out var structures, out var rays);
        query.Next = &v12;
        v12.Next = &structures;
        structures.Next = &rays;
        physical.GetFeatures2(ref query);
        return v12.BufferDeviceAddress && structures.AccelerationStructure && rays.RayQuery;
    }

    public static VkPhysicalDeviceFeatures2 Features(out VkPhysicalDeviceVulkan12Features v12, out VkPhysicalDeviceAccelerationStructureFeaturesKHR structures, out VkPhysicalDeviceRayQueryFeaturesKHR rays)
    {
        v12 = new VkPhysicalDeviceVulkan12Features();
        structures = new VkPhysicalDeviceAccelerationStructureFeaturesKHR();
        rays = new VkPhysicalDeviceRayQueryFeaturesKHR();
        return new VkPhysicalDeviceFeatures2();
    }

    // Before the frame's render pass, in its command buffer: the meshes new to the last frame's list get their
    // structures, then the frame's top level is built over them all.
    public void Record(CommandBuffer cb, int slot)
    {
        (last, Drawn) = (Drawn, last);
        Drawn.Clear();
        for (var i = graveyard.Count - 1; i >= 0; i--)
        {
            if (graveyard[i].Frames <= 0)
            {
                graveyard[i].Buffer.Dispose();
                graveyard.RemoveAt(i);
            }
            else
            {
                graveyard[i] = (graveyard[i].Buffer, graveyard[i].Frames - 1);
            }
        }

        var fresh = false;
        foreach (var (mesh, _, _, _, _) in last)
        {
            if (!mesh.Disposed && mesh.Blas is null && mesh.Count >= 3)
            {
                mesh.Blas = BuildBottom(cb, mesh);
                fresh = true;
            }
        }

        if (fresh)
        {
            Barrier(cb, VkPipelineStageFlags.AccelerationStructureBuildBitKHR);
        }

        var s = slots[slot];
        var instances = (Instance*)s.Instances.Mapped;
        var infos = (InstanceInfo*)s.Infos.Mapped;
        var n = 0;
        (before, now) = (now, before);
        now.Clear();
        foreach (var (mesh, r0, r1, r2, glow) in last)
        {
            var turn = 0;
            while (now.ContainsKey((mesh, turn)))
            {
                turn++;
            }

            now[(mesh, turn)] = (r0, r1, r2);
            if (n == MaxInstances || mesh.Disposed || mesh.Blas is not Blas blas)
            {
                continue;
            }

            var (p0, p1, p2) = before.TryGetValue((mesh, turn), out var was) ? was : (r0, r1, r2);
            infos[n] = new InstanceInfo(blas.Vertices, (uint)mesh.Count, glow, p0, p1, p2);
            instances[n++] = new Instance(r0, r1, r2, blas.Address);
        }

        Instances = n;
        var geometry = new VkAccelerationStructureGeometryKHR
        {
            GeometryType = VkGeometryTypeKHR.InstancesKHR,
            Flags = VkGeometryFlagsKHR.OpaqueBitKHR,
        };
        geometry.Geometry.Instances = new VkAccelerationStructureGeometryInstancesDataKHR { Data = new VkDeviceOrHostAddressConstKHR { DeviceAddress = new VkDeviceAddress(s.Instances.Address) } };
        var info = new VkAccelerationStructureBuildGeometryInfoKHR
        {
            Type = VkAccelerationStructureTypeKHR.TopLevelKHR,
            Flags = VkBuildAccelerationStructureFlagsKHR.PreferFastTraceBitKHR,
            Mode = VkBuildAccelerationStructureModeKHR.BuildKHR,
            DstAccelerationStructure = s.Tlas,
            GeometryCount = 1,
            Geometries = &geometry,
            ScratchData = new VkDeviceOrHostAddressKHR { DeviceAddress = new VkDeviceAddress(s.Scratch.Address) },
        };
        var range = new VkAccelerationStructureBuildRangeInfoKHR { PrimitiveCount = n };
        cb.BuildAccelerationStructuresKHR([info], [new Ptr<VkAccelerationStructureBuildRangeInfoKHR>(&range)]);
        Barrier(cb, ReadBy);
    }

    // The stages that read the top level: the Low shader's and the path tracer's, its ray generation shader's too when
    // the ray tracing pipeline is on.
    public VkPipelineStageFlags ReadBy { get; set; } = VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit;

    // What a draw remembers for the next frame's structure: the mesh, its world rows and its glow.
    public void Remember(Mesh mesh, in Push push)
    {
        if (Drawn.Count < MaxInstances)
        {
            Drawn.Add((mesh, push.Row0, push.Row1, push.Row2, push.Glow));
        }
    }

    public void Dispose()
    {
        foreach (var s in slots)
        {
            s.Dispose();
        }

        foreach (var (buffer, _) in graveyard)
        {
            buffer.Dispose();
        }

        graveyard.Clear();
    }

    private static void Barrier(CommandBuffer cb, VkPipelineStageFlags then)
    {
        var barrier = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.AccelerationStructureWriteBitKHR,
            DstAccessMask = VkAccessFlags.AccelerationStructureReadBitKHR,
        };
        cb.PipelineBarrier(VkPipelineStageFlags.AccelerationStructureBuildBitKHR, then, VkDependencyFlags.None, [barrier], [], []);
    }

    private Blas BuildBottom(CommandBuffer cb, Mesh mesh)
    {
        var geometry = new VkAccelerationStructureGeometryKHR
        {
            GeometryType = VkGeometryTypeKHR.TrianglesKHR,
            Flags = VkGeometryFlagsKHR.OpaqueBitKHR,
        };
        geometry.Geometry.Triangles = new VkAccelerationStructureGeometryTrianglesDataKHR
        {
            VertexFormat = VkFormat.R32G32B32SFloat,
            VertexData = new VkDeviceOrHostAddressConstKHR { DeviceAddress = new VkDeviceAddress(Gpu.Address(device, mesh.Buffer)) },
            VertexStride = new ByteSize64((ulong)sizeof(Vertex)),
            MaxVertex = mesh.Count - 1,
            IndexType = VkIndexType.NoneKHR,
        };
        var info = new VkAccelerationStructureBuildGeometryInfoKHR
        {
            Type = VkAccelerationStructureTypeKHR.BottomLevelKHR,
            Flags = VkBuildAccelerationStructureFlagsKHR.PreferFastTraceBitKHR,
            Mode = VkBuildAccelerationStructureModeKHR.BuildKHR,
            GeometryCount = 1,
            Geometries = &geometry,
        };
        var triangles = mesh.Count / 3;
        var sizes = new VkAccelerationStructureBuildSizesInfoKHR();
        device.GetAccelerationStructureBuildSizesKHR(VkAccelerationStructureBuildTypeKHR.DeviceKHR, ref info, [triangles], ref sizes);
        var blas = new Blas(device, physical, sizes.AccelerationStructureSize, VkAccelerationStructureTypeKHR.BottomLevelKHR)
        {
            Vertices = Gpu.Address(device, mesh.Buffer),
        };
        var scratch = new Gpu.Buffer(device, physical, sizes.BuildScratchSize, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit, hostVisible: false);
        info.DstAccelerationStructure = blas.Handle;
        info.ScratchData = new VkDeviceOrHostAddressKHR { DeviceAddress = new VkDeviceAddress(scratch.Address) };
        var range = new VkAccelerationStructureBuildRangeInfoKHR { PrimitiveCount = triangles };
        cb.BuildAccelerationStructuresKHR([info], [new Ptr<VkAccelerationStructureBuildRangeInfoKHR>(&range)]);
        graveyard.Add((scratch, slots.Length + 1));
        built.Add(blas);
        built.RemoveAll(b => b.Disposed);
        return blas;
    }

    // VkAccelerationStructureInstanceKHR, 64 bytes: the 3 × 4 transform by rows, a custom index and a mask, the binding
    // table's offset and the flags (each 24 and 8 bits), and the bottom-level structure's address.
    [StructLayout(LayoutKind.Sequential)]
    private struct Instance(System.Numerics.Vector4 r0, System.Numerics.Vector4 r1, System.Numerics.Vector4 r2, ulong blas)
    {
        public System.Numerics.Vector4 Row0 = r0;
        public System.Numerics.Vector4 Row1 = r1;
        public System.Numerics.Vector4 Row2 = r2;
        public uint IndexAndMask = 0xFFu << 24;
        public uint OffsetAndFlags = 0x5u << 24; // TRIANGLE_FACING_CULL_DISABLE | FORCE_OPAQUE
        public ulong Blas = blas;
    }

    // What the path tracer reads of an instance, 80 bytes as std430 lays it out: its vertices' address and count (each
    // Vertex 17 floats, three a triangle), its glow, and the rows of its world transform at the structure before.
    [StructLayout(LayoutKind.Sequential)]
    internal struct InstanceInfo(ulong vertices, uint count, System.Numerics.Vector4 glow, System.Numerics.Vector4 p0, System.Numerics.Vector4 p1, System.Numerics.Vector4 p2)
    {
        public ulong Vertices = vertices;
        public uint Count = count;
        public uint Pad = 0;
        public System.Numerics.Vector4 Glow = glow;
        public System.Numerics.Vector4 Previous0 = p0;
        public System.Numerics.Vector4 Previous1 = p1;
        public System.Numerics.Vector4 Previous2 = p2;
    }

    private sealed class Slot : IDisposable
    {
        private readonly Blas top;

        public Slot(RayScene scene)
        {
            var size = new ByteSize((nuint)(MaxInstances * sizeof(Instance)));
            Instances = new Gpu.Buffer(scene.device, scene.physical, (ulong)(MaxInstances * sizeof(Instance)),
                VkBufferUsageFlags.ShaderDeviceAddressBit | VkBufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKHR, hostVisible: true);
            var geometry = new VkAccelerationStructureGeometryKHR { GeometryType = VkGeometryTypeKHR.InstancesKHR, Flags = VkGeometryFlagsKHR.OpaqueBitKHR };
            geometry.Geometry.Instances = new VkAccelerationStructureGeometryInstancesDataKHR { Data = new VkDeviceOrHostAddressConstKHR { DeviceAddress = new VkDeviceAddress(Instances.Address) } };
            var info = new VkAccelerationStructureBuildGeometryInfoKHR
            {
                Type = VkAccelerationStructureTypeKHR.TopLevelKHR,
                Flags = VkBuildAccelerationStructureFlagsKHR.PreferFastTraceBitKHR,
                Mode = VkBuildAccelerationStructureModeKHR.BuildKHR,
                GeometryCount = 1,
                Geometries = &geometry,
            };
            var sizes = new VkAccelerationStructureBuildSizesInfoKHR();
            scene.device.GetAccelerationStructureBuildSizesKHR(VkAccelerationStructureBuildTypeKHR.DeviceKHR, ref info, [MaxInstances], ref sizes);
            top = new Blas(scene.device, scene.physical, sizes.AccelerationStructureSize, VkAccelerationStructureTypeKHR.TopLevelKHR);
            Scratch = new Gpu.Buffer(scene.device, scene.physical, sizes.BuildScratchSize, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit, hostVisible: false);
            Infos = new Gpu.Buffer(scene.device, scene.physical, (ulong)(MaxInstances * sizeof(InstanceInfo)), VkBufferUsageFlags.StorageBufferBit, hostVisible: true);
        }

        public Gpu.Buffer Infos { get; }

        public Gpu.Buffer Instances { get; }

        public Gpu.Buffer Scratch { get; }

        public VkAccelerationStructureKHR Tlas => top.Handle;

        public void Dispose()
        {
            top.Dispose();
            Scratch.Dispose();
            Instances.Dispose();
            Infos.Dispose();
        }
    }
}

// An acceleration structure and the buffer it lives in.
internal sealed unsafe class Blas : IDisposable
{
    private readonly Device device;
    private readonly Gpu.Buffer storage;

    public Blas(Device device, PhysicalDevice physical, ByteSize64 size, VkAccelerationStructureTypeKHR type)
    {
        this.device = device;
        storage = new Gpu.Buffer(device, physical, size, VkBufferUsageFlags.AccelerationStructureStorageBitKHR | VkBufferUsageFlags.ShaderDeviceAddressBit, hostVisible: false);
        Handle = device.CreateAccelerationStructureKHR(new VkAccelerationStructureCreateInfoKHR { Buffer = storage.Handle, Size = size, Type = type }, null);
        Address = device.GetAccelerationStructureDeviceAddressKHR(new VkAccelerationStructureDeviceAddressInfoKHR { AccelerationStructure = Handle }).Value;
    }

    public VkAccelerationStructureKHR Handle { get; }

    public ulong Address { get; }

    public ulong Vertices { get; init; } // a bottom level's: its mesh's vertex buffer's address

    public bool Disposed { get; private set; }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        device.DestroyAccelerationStructureKHR(Handle, null);
        storage.Dispose();
        Disposed = true;
    }
}

// Buffers whose device address the GPU takes, and the memory for them.
internal static unsafe class Gpu
{
    public static ulong Address(Device device, VkBuffer buffer) => device.GetBufferDeviceAddress(new VkBufferDeviceAddressInfo { Buffer = buffer }).Value;

    // Memory for buffer of the given properties, allocated so that its device address can be taken.
    public static VkDeviceMemory Allocate(Device device, PhysicalDevice physical, VkBuffer buffer, VkMemoryPropertyFlags wanted)
    {
        var needs = device.GetBufferMemoryRequirements(buffer);
        var properties = physical.GetMemoryProperties();
        var type = -1;
        for (var i = 0; i < properties.MemoryTypeCount && type < 0; i++)
        {
            if ((needs.MemoryTypeBits & (1u << i)) != 0 && (properties.MemoryTypes[i].PropertyFlags & wanted) == wanted)
            {
                type = i;
            }
        }

        if (type < 0)
        {
            throw new InvalidOperationException($"no memory type has {wanted}");
        }

        var flags = new VkMemoryAllocateFlagsInfo { Flags = VkMemoryAllocateFlags.DeviceAddressBit };
        return device.AllocateMemory(new VkMemoryAllocateInfo { Next = &flags, AllocationSize = needs.Size, MemoryTypeIndex = type }, null);
    }

    internal sealed class Buffer : IDisposable
    {
        private readonly Device device;
        private readonly VkDeviceMemory memory;
        private bool disposed;

        public Buffer(Device device, PhysicalDevice physical, ByteSize64 size, VkBufferUsageFlags usage, bool hostVisible)
            : this(device, physical, size.Value, usage, hostVisible)
        {
        }

        public Buffer(Device device, PhysicalDevice physical, ulong size, VkBufferUsageFlags usage, bool hostVisible)
        {
            this.device = device;
            Handle = device.CreateBuffer(new VkBufferCreateInfo { Size = new ByteSize((nuint)Math.Max(size, 16UL)), Usage = usage, SharingMode = VkSharingMode.Exclusive }, null);
            memory = Allocate(device, physical, Handle, hostVisible ? VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit : VkMemoryPropertyFlags.DeviceLocalBit);
            device.BindBufferMemory(Handle, memory, memoryOffset: ByteSize.Zero);
            Address = Gpu.Address(device, Handle);
            if (hostVisible)
            {
                Mapped = (byte*)device.MapMemory<byte>(new MapInfo(memory) { Region = new MapRegion { Size = new ByteSize((nuint)Math.Max(size, 16UL)) } });
            }
        }

        public VkBuffer Handle { get; }

        public ulong Address { get; }

        public byte* Mapped { get; }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            if (Mapped is not null)
            {
                device.UnmapMemory(memory);
            }

            device.DestroyBuffer(Handle, null);
            device.FreeMemory(memory, null);
            disposed = true;
        }
    }
}
