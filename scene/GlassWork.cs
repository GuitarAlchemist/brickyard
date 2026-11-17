using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

// Glass in the hall, as the Observatory's glassworks shader draws it rasterized (artpass/glass): the demos' water
// globes (boules à eau) and the artifacts' paperweights (sulfures), HallGallery.Globes. What is inside them is drawn
// first, with the hall; the glass then shows the picture behind it, bent through the ball (the refracted ray carried
// to the far side and projected back onto the picture), darkened by its tint over the length it crosses, and mixed by
// Fresnel's law with the hall it mirrors and the key light's highlight.
//
// The picture behind is a copy of the hall drawn so far (Capture), taken in the middle of DLSS Super Resolution's pass
// (RasterUpscaler.ResumePass goes on with it), or the path tracer's picture. Without either (DLSS off, Low's MSAA) the
// glass is a veil (Veil): Fresnel's reflection and the highlight blended over what is inside, unbent.
internal sealed unsafe class GlassWork : IDisposable
{
    private const string VertexShader = """
        #version 450
        layout(push_constant) uniform Push { mat4 viewProj; vec4 row0; vec4 row1; vec4 row2; vec4 tint; vec4 eye; vec4 screen; } pc;
        layout(location = 0) in vec3 pos;
        layout(location = 1) in vec3 normal;
        layout(location = 0) out vec3 world;
        layout(location = 1) out vec3 facing;
        void main() {
            vec4 p = vec4(pos, 1.0);
            world = vec3(dot(pc.row0, p), dot(pc.row1, p), dot(pc.row2, p));
            facing = vec3(dot(pc.row0.xyz, normal), dot(pc.row1.xyz, normal), dot(pc.row2.xyz, normal));
            gl_Position = pc.viewProj * vec4(world, 1.0);
        }
        """;

    // Shared by both fragment shaders: the glass's Fresnel term, what it mirrors (the hall's dark floor below, its warm
    // lamps above) and the key light's highlight, for the eye and the surface's normal.
    private const string Common = """
        #version 450
        layout(push_constant) uniform Push { mat4 viewProj; vec4 row0; vec4 row1; vec4 row2; vec4 tint; vec4 eye; vec4 screen; } pc;
        layout(location = 0) in vec3 world;
        layout(location = 1) in vec3 facing;
        layout(location = 0) out vec4 color;
        vec3 hall(vec3 d) {
            float up = clamp(d.y * 0.5 + 0.5, 0.0, 1.0);
            return mix(vec3(0.018, 0.016, 0.014), vec3(0.5, 0.43, 0.33), smoothstep(0.35, 0.95, up));
        }
        float shine(vec3 n, vec3 v) {
            vec3 key = normalize(vec3(0.35, 1.0, 0.45));
            float s = max(dot(reflect(-key, n), v), 0.0);
            return pow(s, 700.0) * 5.0 + pow(s, 48.0) * 0.12;
        }
        """;

    private const string RefractShader = Common + """
        layout(set = 0, binding = 0) uniform sampler2D behind;
        void main() {
            vec3 n = normalize(facing);
            vec3 v = normalize(pc.eye.xyz - world);
            float ndv = dot(n, v);
            if (ndv <= 0.0) discard; // the far side, seen through the near one
            float ior = pc.eye.w;
            float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
            float fresnel = f0 + (1.0 - f0) * pow(1.0 - ndv, 5.0);
            // Bent at the near face, carried across the ball (2 r ndv) and projected back onto the picture behind.
            float through = 2.0 * pc.tint.w * ndv;
            vec3 far = world + refract(-v, n, 1.0 / ior) * through * pc.screen.z;
            vec4 clip = pc.viewProj * vec4(far, 1.0);
            vec2 uv = clamp(clip.xy / clip.w * 0.5 + 0.5, vec2(0.001), vec2(0.999));
            vec3 keep = pow(clamp(pc.tint.rgb, vec3(0.001), vec3(1.0)), vec3(through / 0.1));
            vec3 seen = texture(behind, uv).rgb * keep;
            color = vec4(seen * (1.0 - fresnel) + hall(reflect(-v, n)) * fresnel + vec3(shine(n, v)), 1.0);
        }
        """;

    private const string VeilShader = Common + """
        void main() {
            vec3 n = normalize(facing);
            vec3 v = normalize(pc.eye.xyz - world);
            float ndv = dot(n, v);
            if (ndv <= 0.0) discard;
            float ior = pc.eye.w;
            float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
            float fresnel = f0 + (1.0 - f0) * pow(1.0 - ndv, 5.0);
            float s = shine(n, v);
            float alpha = clamp(fresnel + 0.05 + s, 0.0, 1.0);
            color = vec4(min(hall(reflect(-v, n)) * fresnel / alpha + vec3(s) / alpha, vec3(4.0)), alpha);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct GlassPush
    {
        public SN.Matrix4x4 ViewProj;
        public SN.Vector4 Row0;
        public SN.Vector4 Row1;
        public SN.Vector4 Row2;
        public SN.Vector4 Tint; // the colour 10 cm of it keeps, and its radius
        public SN.Vector4 Eye; // where the eye is, and the index of refraction
        public SN.Vector4 Screen; // the picture's size in pixels, how far the bent ray goes (1: across the ball), 0
    }

    private const VkShaderStageFlags PushStages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private readonly Device device;
    private readonly PhysicalDevice physical;
    private readonly VkFormat format;
    private readonly VkPipelineLayout layout;
    private readonly VkShaderModule vertex, refractFragment, veilFragment;
    private readonly VkPipeline refract, veil;
    private readonly VkSampler sampler;
    private readonly DescriptorPoolEx pool;
    private readonly VkDescriptorSet copySet, tracedSet;
    private readonly Mesh ball, dome;
    private VkImage behind;
    private VkDeviceMemory behindMemory;
    private VkImageView behindView;
    private (int Width, int Height) behindSize;
    private VkImageView tracedView;

    public GlassWork(Device device, PhysicalDevice physical, VkRenderPass pass, VkSampleCountFlags samples, VkFormat colourFormat, DescriptorSetLayoutEx setLayout)
    {
        this.device = device;
        this.physical = physical;
        format = colourFormat;
        layout = device.CreatePipelineLayout([setLayout], [new VkPushConstantRange { StageFlags = PushStages, Size = ByteSize.Of<GlassPush>() }], null);
        vertex = Stage.Shader(device, VertexShader, ShaderKind.VertexShader, "glass.vert");
        refractFragment = Stage.Shader(device, RefractShader, ShaderKind.FragmentShader, "glass-refract.frag");
        veilFragment = Stage.Shader(device, VeilShader, ShaderKind.FragmentShader, "glass-veil.frag");
        refract = Stage.Pipeline(device, layout, pass, samples, vertex, refractFragment);
        veil = Stage.Pipeline(device, layout, pass, samples, vertex, veilFragment, translucent: true);
        sampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1f,
        }, null);
        pool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
        {
            MaxSets = 2,
            PoolSizes = [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 }],
        }, null);
        copySet = device.AllocateDescriptorSet(pool, setLayout);
        tracedSet = device.AllocateDescriptorSet(pool, setLayout);
        ball = new Mesh(device, physical, Sphere(48, 24, whole: true));
        dome = new Mesh(device, physical, Sphere(48, 12, whole: false));
    }

    // How a frame's glass is drawn: bent through a copy of the hall, bent through the path tracer's picture, or veiled.
    public enum Way
    {
        Copy,
        Traced,
        Veil,
    }

    // Outside any pass, the hall's pass just ended: colour (in the General layout, written as a colour attachment) copied
    // into the picture behind the glass, which the glass's fragments then sample; the colour and its depth made ready to
    // be drawn on again (RasterUpscaler.ResumePass).
    public void Capture(CommandBuffer cb, VkImage colour, int width, int height)
    {
        if (behindSize != (width, height))
        {
            device.WaitIdle();
            DestroyBehind();
            behind = device.CreateImage(new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            }, null);
            behindMemory = device.AllocateMemory(physical, behind, VkMemoryPropertyFlags.DeviceLocalBit, null);
            device.BindImageMemory(behind, behindMemory, ByteSize.Zero);
            behindView = device.CreateImageView(new VkImageViewCreateInfo
            {
                Image = behind,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            }, null);
            behindSize = (width, height);
            var info = new VkDescriptorImageInfo { Sampler = sampler, ImageView = behindView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = copySet, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &info }], []);
        }

        var drawn = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = VkAccessFlags.TransferReadBit,
        };
        cb.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [drawn], [],
            [Stage.Barrier(behind, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferWriteBit)]);
        var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
        cb.CopyImage(colour, VkImageLayout.General, behind, VkImageLayout.TransferDstOptimal,
            [new VkImageCopy { SrcSubresource = layers, DstSubresource = layers, Extent = new VkExtent3D { Width = width, Height = height, Depth = 1 } }]);
        // The copy read before the hall's colour is drawn on again, and its depth written before it is tested again.
        var again = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.TransferReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
        };
        cb.PipelineBarrier(VkPipelineStageFlags.TransferBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [again], [],
            [Stage.Barrier(behind, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit)]);
    }

    // In the pass, after everything opaque: each globe's glass, the way the frame allows, its picture width by height.
    public void Draw(CommandBuffer cb, Way way, IReadOnlyList<HallGallery.Globe> globes, SN.Matrix4x4 viewProj, SN.Vector3 eye, int width, int height, VkImageView traced = default)
    {
        if (globes.Count == 0)
        {
            return;
        }

        if (way == Way.Traced && traced.VkHandle != tracedView.VkHandle)
        {
            tracedView = traced;
            var info = new VkDescriptorImageInfo { Sampler = sampler, ImageView = traced, ImageLayout = VkImageLayout.General };
            device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = tracedSet, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &info }], []);
        }

        if (way == Way.Copy && behindView.VkHandle == 0)
        {
            way = Way.Veil;
        }

        cb.BindPipeline(VkPipelineBindPoint.Graphics, way == Way.Veil ? veil : refract);
        if (way != Way.Veil)
        {
            cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 0, [way == Way.Copy ? copySet : tracedSet], []);
        }

        foreach (var globe in globes)
        {
            var world = SN.Matrix4x4.CreateScale(globe.Radius) * globe.World;
            var push = new GlassPush
            {
                ViewProj = viewProj,
                Row0 = new(world.M11, world.M21, world.M31, world.M41),
                Row1 = new(world.M12, world.M22, world.M32, world.M42),
                Row2 = new(world.M13, world.M23, world.M33, world.M43),
                Tint = new(globe.Tint, globe.Radius),
                // A water globe's glass and water bend less than a paperweight's solid glass, which also carries the ray
                // further (it magnifies what it holds).
                Eye = new(eye, globe.Dome ? 1.5f : 1.4f),
                Screen = new(width, height, globe.Dome ? 0.5f : 0.6f, 0f),
            };
            var mesh = globe.Dome ? dome : ball;
            cb.PushConstants(layout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(GlassPush)));
            cb.BindVertexBuffer(0, mesh.Buffer);
            cb.Draw(mesh.Count, 1, 0, 0);
        }
    }

    public void Dispose()
    {
        DestroyBehind();
        ball.Dispose();
        dome.Dispose();
        device.DestroyDescriptorPool(pool, null);
        device.DestroySampler(sampler, null);
        device.DestroyPipeline(refract, null);
        device.DestroyPipeline(veil, null);
        device.DestroyShaderModule(vertex, null);
        device.DestroyShaderModule(refractFragment, null);
        device.DestroyShaderModule(veilFragment, null);
        device.DestroyPipelineLayout(layout, null);
    }

    private void DestroyBehind()
    {
        if (behindView.VkHandle == 0)
        {
            return;
        }

        device.DestroyImageView(behindView, null);
        device.DestroyImage(behind, null);
        device.FreeMemory(behindMemory, null);
        behindView = default;
        behindSize = default;
    }

    // A unit sphere, slices round and stacks from pole to pole (whole), or its upper half (a dome, its rim at y 0), as
    // triangles whose normals point out.
    internal static Vertex[] Sphere(int slices, int stacks, bool whole)
    {
        var o = new List<Vertex>();
        var top = stacks; // from the top pole down to the bottom one, or to the equator
        var reach = whole ? MathF.PI : MathF.PI / 2f;
        SN.Vector3 At(int i, int j)
        {
            var theta = reach * j / top;
            var phi = 2f * MathF.PI * i / slices;
            return new SN.Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
        }

        void Add(SN.Vector3 p) => o.Add(new Vertex(p, p, new SN.Vector3(0.9f), Material.Glass, SN.Vector3.UnitX, default));
        for (var j = 0; j < top; j++)
        {
            for (var i = 0; i < slices; i++)
            {
                var (a, b, c, d) = (At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j + 1));
                Add(a);
                Add(b);
                Add(c);
                Add(a);
                Add(c);
                Add(d);
            }
        }

        return [.. o];
    }

    public static void SelfTest(Action<bool, string> check)
    {
        var ball = Sphere(16, 8, whole: true);
        var dome = Sphere(16, 4, whole: false);
        check(ball.Length == 16 * 8 * 6 && ball.All(v => MathF.Abs(v.Pos.Length() - 1f) < 1e-4f && v.Normal == v.Pos)
            && dome.All(v => v.Pos.Y >= -1e-5f) && dome.Any(v => MathF.Abs(v.Pos.Y) < 1e-5f) && dome.Any(v => v.Pos.Y > 0.999f),
            "the glass's ball is a unit sphere with outward normals, its dome the upper half down to its rim");
        check(sizeof(GlassPush) == 160, "the glass's push constants are 160 bytes, within the GPU's 256");
    }
}
