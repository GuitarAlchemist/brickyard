using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.ShaderCApi;
using Delta.SceneModules;
using System.Runtime.InteropServices;
using SN = System.Numerics;

// A fixed chest display, in front of decorative vents but above the existing status lamps; or any other flat screen of
// fixed size, drawn in its own frame as rect at depth z, with scanlines as a CRT's when asked (the arcade cabinet's).
// One image/staging pair per frame-in-flight slot; uploads only after that slot's existing fence.
internal sealed unsafe class ScreenGpuRenderer : IDisposable
{
    private readonly Device device;
    private readonly List<Texture> textures = new(2);
    private readonly VkDescriptorSet[] sets;
    private readonly ulong[] revisions;
    private readonly int width, height;
    private readonly SN.Vector4 rect, parameters;
    private DescriptorPoolEx pool;
    private VkPipelineLayout layout;
    private VkPipeline pipeline;
    private VkShaderModule vertex, fragment;
    private bool hasPool, hasLayout, hasPipeline, hasVertex, hasFragment, disposed;
    public ulong Uploads { get; private set; }
    public int ImageCount => textures.Count;
    private const VkShaderStageFlags PushStage = VkShaderStageFlags.VertexBit;
    private const string VertexShader = """
        #version 450
        layout(push_constant) uniform Push { mat4 mvp; vec4 rect; vec4 params; } pc;
        layout(location = 0) out vec2 uv;
        layout(location = 1) flat out vec2 lines;
        const vec2 corners[6] = vec2[](vec2(0,0),vec2(1,0),vec2(0,1),vec2(1,0),vec2(1,1),vec2(0,1));
        void main() {
            uv = corners[gl_VertexIndex];
            lines = pc.params.yz;
            vec3 p = vec3(mix(pc.rect.x, pc.rect.z, uv.x), mix(pc.rect.y, pc.rect.w, uv.y), pc.params.x);
            gl_Position = pc.mvp * vec4(p, 1);
        }
        """;
    private const string FragmentShader = """
        #version 450
        layout(set = 0, binding = 0) uniform sampler2D pixels;
        layout(location = 0) in vec2 uv;
        layout(location = 1) flat in vec2 lines;
        layout(location = 0) out vec4 color;
        void main() {
            vec4 c = texture(pixels, uv);
            // lines.x: how dark the gap between a CRT's rows is, over lines.y rows; 0 leaves the pixels as they are.
            float row = fract(uv.y * lines.y);
            color = vec4(c.rgb * (1.0 - lines.x * smoothstep(0.55, 0.95, row)), c.a);
        }
        """;
    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPush(SN.Matrix4x4 mvp, SN.Vector4 rect, SN.Vector4 parameters)
    {
        public SN.Matrix4x4 Mvp = mvp;
        public SN.Vector4 Rect = rect;
        public SN.Vector4 Parameters = parameters;
    }
    public ScreenGpuRenderer(Device device, PhysicalDevice physical, CommandBuffer initialUpload, VkRenderPass pass,
        VkSampleCountFlags samples, VkSampler sampler, DescriptorSetLayoutEx setLayout, int slots, ScreenFrame initial)
        : this(device, physical, initialUpload, pass, samples, sampler, setLayout, slots, Checked(initial), initial.Width, initial.Height,
            initial.Revision, new(-0.15f, 0.63f, 0.15f, 0.45f), 0.218f, 0f)
    {
    }

    // rect: left, top, right and bottom in the frame Draw is given, at depth z; scanlines: 0 for none, up to 1 for black gaps.
    public ScreenGpuRenderer(Device device, PhysicalDevice physical, CommandBuffer initialUpload, VkRenderPass pass,
        VkSampleCountFlags samples, VkSampler sampler, DescriptorSetLayoutEx setLayout, int slots, byte[] pixels, int width, int height,
        ulong revision, SN.Vector4 rect, float z, float scanlines)
    {
        if (slots is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(slots));
        if (width <= 0 || height <= 0 || pixels.Length != width * height * 4) throw new InvalidDataException("Screen pixels do not match their size.");
        this.device = device;
        this.width = width;
        this.height = height;
        this.rect = rect;
        parameters = new(z, scanlines, height, 0f);
        sets = new VkDescriptorSet[slots]; revisions = new ulong[slots];
        try
        {
            pool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo { MaxSets = slots,
                PoolSizes = [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = slots }] }, null);
            hasPool = true;
            for (var i = 0; i < slots; i++)
            {
                var texture = new Texture(device, physical, initialUpload, pixels, width, height);
                textures.Add(texture);
                sets[i] = device.AllocateDescriptorSet(pool, setLayout);
                var image = new VkDescriptorImageInfo { Sampler = sampler, ImageView = texture.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = sets[i], DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image }], []);
                revisions[i] = revision;
            }
            layout = device.CreatePipelineLayout([setLayout], [new VkPushConstantRange { StageFlags = PushStage, Size = ByteSize.Of<ScreenPush>() }], null); hasLayout = true;
            vertex = Stage.Shader(device, VertexShader, ShaderKind.VertexShader, "arcade-screen.vert"); hasVertex = true;
            fragment = Stage.Shader(device, FragmentShader, ShaderKind.FragmentShader, "arcade-screen.frag"); hasFragment = true;
            pipeline = Stage.Pipeline(device, layout, pass, samples, vertex, fragment, vertexless: true); hasPipeline = true;
        }
        catch { Dispose(); throw; }
    }
    public void UploadAfterFence(CommandBuffer commands, int slot, ScreenFrame frame)
    {
        frame.Validate();
        UploadAfterFence(commands, slot, frame.Rgba.Span, frame.Revision);
    }

    public void UploadAfterFence(CommandBuffer commands, int slot, ReadOnlySpan<byte> pixels, ulong revision)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((uint)slot >= (uint)sets.Length) throw new ArgumentOutOfRangeException(nameof(slot));
        if (revisions[slot] == revision) return;
        textures[slot].UpdateAfterFence(commands, pixels, width, height);
        revisions[slot] = revision; Uploads++;
    }
    public void Draw(CommandBuffer commands, int slot, SN.Matrix4x4 torsoViewProjection)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        commands.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 0, [sets[slot]], []);
        var push = new ScreenPush(torsoViewProjection, rect, parameters);
        commands.PushConstants(layout, PushStage, 0, new ReadOnlySpan<byte>(&push, sizeof(ScreenPush)));
        commands.Draw(6, 1, 0, 0);
    }
    private static byte[] Checked(ScreenFrame initial)
    {
        initial.Validate();
        return initial.Rgba.ToArray(); // one bounded initialization copy; no per-frame allocation of pixels/images
    }

    public void Dispose()
    {
        if (disposed) return;
        if (hasPipeline) device.DestroyPipeline(pipeline, null);
        if (hasLayout) device.DestroyPipelineLayout(layout, null);
        if (hasVertex) device.DestroyShaderModule(vertex, null);
        if (hasFragment) device.DestroyShaderModule(fragment, null);
        if (hasPool) device.DestroyDescriptorPool(pool, null);
        foreach (var texture in textures) texture.Dispose();
        disposed = true;
    }
}
