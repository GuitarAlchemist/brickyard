using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

// Path tracing (F9, --path-tracing): the hall drawn by following light through it, as the Observatory's RTX scene and
// the games' path-traced modes do, instead of the Low lighting's painted and partly traced light. A compute pass sends
// a ray from the eye through each pixel (jittered within it, so that edges smooth out over frames) into the ray scene
// (RayScene's top level, VK_KHR_ray_query), reads the surface it meets from its mesh's own vertices by their address
// (colour, material, the photographed scans and their normals, the globe), lights it from the key and the fill as the
// Low lighting places them, through traced shadows, and goes on for up to three bounces, sampling each surface's
// reflection (Lambert and GGX); what glows lights what is near it; what leaves the hall sees the hall's surroundings.
// Each pixel's light is kept over frames: its surface is followed to where it was the frame before (the camera's move
// and the mesh's own, RayScene's previous rows), and averaged with what was there when it is the same surface. Three
// passes of an edge-aware à-trous filter then smooth what noise is left, less as the light settles. The image goes
// over the raster pass's, which keeps the depth for what is drawn after (the screens, the cards).
internal sealed unsafe class PathTracer : IDisposable
{
    public const int Bounces = 3;

    // Max out (F6, as the Observatory's): more bounces and several paths a pixel from its first surface, for less noise
    // and more light carried into the corners, at a cost the exit line measures.
    public const int MaxOutBounces = 5;
    public const int MaxOutSamples = 4;

    public bool MaxOut { get; set; }
    public const int MaxHistory = 32; // frames a pixel's light is averaged over: about half a second at 60 fps

    private const int Group = 8;
    private const int UniformStep = 256;

    private readonly Device device;
    private readonly PhysicalDevice physical;
    private readonly RayScene rays;
    private readonly DescriptorSetLayoutEx setLayout;
    private readonly DescriptorPoolEx pool;
    private readonly VkDescriptorSet[] sets;
    private readonly VkPipelineLayout layout;
    private readonly VkPipeline trace;
    private readonly VkPipeline filter;
    private readonly Gpu.Buffer uniforms;
    private readonly VkDescriptorSet composite;
    private readonly VkSampler compositeSampler;
    private readonly Target[] targets = new Target[Images.Length];
    private readonly VkPipeline raygen; // the ray tracing pipeline's, with SER when offered; default when traced in compute
    private readonly Gpu.Buffer? table; // its shader binding table: ray generation, miss, hit group
    private readonly VkStridedDeviceAddressRegionKHR generate, miss, hits;
    private readonly VkQueryPool timestamps;
    private readonly bool[] timed;
    private readonly float tick; // nanoseconds a timestamp's unit
    private SN.Matrix4x4 previousViewProj;
    private bool fresh = true;
    private uint frame;

    private const VkShaderStageFlags Stages = VkShaderStageFlags.ComputeBit | VkShaderStageFlags.RaygenBitKHR;

    // How the rays are traced: from the ray tracing pipeline with shader execution reordering, from it without, or
    // from the compute pass's ray queries.
    public string How { get; }

    public bool Pipeline => table is not null;

    public bool Reordering { get; }

    // The GPU's milliseconds for the trace and the filter, each frame's, as the timestamps measured them.
    public List<double> GpuMilliseconds { get; } = [];

    // The images, their bindings from 7 on: positions, normals, kept light, colour and glow, each this frame's and
    // last frame's; the filter's two; the picture.
    private static readonly VkFormat[] Images =
    [
        VkFormat.R32G32B32A32SFloat, VkFormat.R32G32B32A32SFloat, // 7, 8: the eye's surface's place, its distance in w (0: nothing)
        VkFormat.R16G16B16A16SFloat, VkFormat.R16G16B16A16SFloat, // 9, 10: its normal
        VkFormat.R32G32B32A32SFloat, VkFormat.R32G32B32A32SFloat, // 11, 12: its light over its colour, kept, and for how many frames in w
        VkFormat.R16G16B16A16SFloat, VkFormat.R16G16B16A16SFloat, // 13, 14: its colour, kept
        VkFormat.R16G16B16A16SFloat, VkFormat.R16G16B16A16SFloat, // 15, 16: its glow, kept
        VkFormat.R16G16B16A16SFloat, VkFormat.R16G16B16A16SFloat, // 17, 18: the filter's
        VkFormat.R16G16B16A16SFloat, // 19: the picture
        // Ray Reconstruction's (DLSS-RR, --dlss): what it is given at the size traced, and what it gives at the
        // window's; a pixel each when it is off.
        VkFormat.R16G16SFloat, // 20: in pixels, from where the surface is to where it was a frame ago
        VkFormat.R32SFloat, // 21: its depth along the view
        VkFormat.R16G16B16A16SFloat, // 22: its diffuse colour
        VkFormat.R16G16B16A16SFloat, // 23: its specular colour, as the light around reflects it
        VkFormat.R16G16B16A16SFloat, // 24: its normal, and its roughness in w
        VkFormat.R16G16B16A16SFloat, // 25: its light, one path's, unfiltered
        VkFormat.R16G16B16A16SFloat, // 26: Ray Reconstruction's picture
        VkFormat.R32SFloat, // 27: how far the first bounce went when it was a reflection (0 when diffuse)
        // Frame generation's (DLSS-G, --dlss-g), with the motion above: the depth as a depth buffer holds it, 0 near to
        // 1 far. Ray Reconstruction takes the depth along the view when both are given (sl.dlss_d's dlss_dEntry.cpp).
        VkFormat.R32SFloat, // 28
    ];

    private const int Output = 12; // the picture's index in Images
    private const int Motion = 13, Depth = 14, Diffuse = 15, Specular = 16, NormalRoughness = 17, Colour = 18, Upscaled = 19, SpecularHit = 20, DepthBuffer = 21;

    // Ray Reconstruction (--dlss): DLSS's mode (1 performance ... 6 DLAA, Streamline.Modes), 0 when off. The trace
    // then runs at the size DLSS asks for that mode, one jittered path a pixel without the frames kept or the filter,
    // and Ray Reconstruction denoises and upscales it to the window's size.
    public uint UpscaleMode { get; set; }

    // DLSS takes the jitter as minus the sample's offset from the pixel's centre, as NVIDIA's own Vulkan sample passes
    // it (vk_denoise_dlssrr: InJitterOffset = -jitter; Streamline hands jitterOffset to NGX as it is). -1 tries the
    // other sign (--dlss-jitter-flip): thin edges and the bricks' grain then go soft.
    public float JitterSign { get; set; } = 1f;

    public bool Upscaling { get; private set; }

    public int OutputWidth { get; private set; }

    public int OutputHeight { get; private set; }

    public long Evaluations { get; private set; }

    public string? UpscaleError { get; private set; }

    private uint sizedFor;

    public PathTracer(Device device, PhysicalDevice physical, int slots, RayScene rays, VkImageView albedo, VkImageView normal, VkImageView arm, VkSampler materialSampler,
        VkImageView globes, VkSampler globeSampler, DescriptorPoolEx splashPool, DescriptorSetLayoutEx splashLayout, bool rtPipeline, bool reorder, float timestampPeriod)
    {
        this.device = device;
        this.physical = physical;
        this.rays = rays;
        var stages = rtPipeline ? Stages : VkShaderStageFlags.ComputeBit;
        var bindings = new List<VkDescriptorSetLayoutBinding>
        {
            new() { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = stages },
            new() { Binding = 1, DescriptorType = VkDescriptorType.AccelerationStructureKHR, DescriptorCount = 1, StageFlags = stages },
            new() { Binding = 2, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = stages },
        };
        for (var b = 3; b <= 6; b++)
        {
            bindings.Add(new() { Binding = b, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = stages });
        }

        for (var i = 0; i < Images.Length; i++)
        {
            bindings.Add(new() { Binding = 7 + i, DescriptorType = VkDescriptorType.StorageImage, DescriptorCount = 1, StageFlags = stages });
        }

        setLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = [.. bindings] }, null);
        pool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
        {
            MaxSets = slots,
            PoolSizes =
            [
                new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = slots },
                new VkDescriptorPoolSize { Type = VkDescriptorType.AccelerationStructureKHR, DescriptorCount = slots },
                new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = slots },
                new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 4 * slots },
                new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = Images.Length * slots },
            ],
        }, null);
        uniforms = new Gpu.Buffer(device, physical, (ulong)(UniformStep * slots), VkBufferUsageFlags.UniformBufferBit, hostVisible: true);
        sets = new VkDescriptorSet[slots];
        var maps = stackalloc VkDescriptorImageInfo[4];
        maps[0] = new VkDescriptorImageInfo { Sampler = materialSampler, ImageView = albedo, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        maps[1] = new VkDescriptorImageInfo { Sampler = materialSampler, ImageView = normal, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        maps[2] = new VkDescriptorImageInfo { Sampler = materialSampler, ImageView = arm, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        maps[3] = new VkDescriptorImageInfo { Sampler = globeSampler, ImageView = globes, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        for (var s = 0; s < slots; s++)
        {
            sets[s] = device.AllocateDescriptorSet(pool, setLayout);
            var uniform = new VkDescriptorBufferInfo { Buffer = uniforms.Handle, Offset = new ByteSize64((ulong)(s * UniformStep)), Range = ByteSize.Of<TraceFrame>() };
            var table = new VkDescriptorBufferInfo { Buffer = rays.Infos(s).Handle, Offset = new ByteSize64(0UL), Range = new ByteSize64((ulong)(RayScene.MaxInstances * sizeof(RayScene.InstanceInfo))) };
            var top = rays.Top(s);
            var structure = new VkWriteDescriptorSetAccelerationStructureKHR { AccelerationStructureCount = 1, AccelerationStructures = &top };
            device.UpdateDescriptorSets(
            [
                new VkWriteDescriptorSet { DstSet = sets[s], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &uniform },
                new VkWriteDescriptorSet { Next = &structure, DstSet = sets[s], DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.AccelerationStructureKHR },
                new VkWriteDescriptorSet { DstSet = sets[s], DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &table },
                new VkWriteDescriptorSet { DstSet = sets[s], DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[0] },
                new VkWriteDescriptorSet { DstSet = sets[s], DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[1] },
                new VkWriteDescriptorSet { DstSet = sets[s], DstBinding = 5, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[2] },
                new VkWriteDescriptorSet { DstSet = sets[s], DstBinding = 6, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[3] },
            ], []);
        }

        pushStages = stages;
        var pushRange = new VkPushConstantRange { StageFlags = stages, Offset = ByteSize.Zero, Size = ByteSize.Of<PassPush>() };
        layout = device.CreatePipelineLayout([setLayout], [pushRange], null);
        filter = Compute("#version 460\n" + Header + FilterShader, "path-filter.comp");
        if (rtPipeline)
        {
            Reordering = reorder;
            (raygen, table, generate, miss, hits) = RayPipeline(reorder);
            How = reorder ? "the ray tracing pipeline, with shader execution reordering" : "the ray tracing pipeline";
        }
        else
        {
            trace = Compute("#version 460\n" + Header + TraceShader, "path-trace.comp");
            How = "ray queries in compute";
        }

        // Two timestamps a slot, around the trace and the filter.
        tick = timestampPeriod;
        timed = new bool[slots];
        timestamps = device.CreateQueryPool(new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = 2 * slots }, null);

        // The picture, drawn over the raster pass's by the splash's pipeline: one of its sets, nearest, as the pixels are.
        compositeSampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Nearest,
            MinFilter = VkFilter.Nearest,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1f,
        }, null);
        composite = device.AllocateDescriptorSet(splashPool, splashLayout);
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    // The images at the window's size, or at the size Ray Reconstruction traces at, made again when it or the mode
    // changes; the kept light starts over.
    public void Resize(int width, int height)
    {
        if (width == OutputWidth && height == OutputHeight && UpscaleMode == sizedFor)
        {
            return;
        }

        device.WaitIdle();
        DestroyTargets();
        OutputWidth = width;
        OutputHeight = height;
        sizedFor = UpscaleMode;
        (Width, Height) = (width, height);
        Upscaling = false;
        if (UpscaleMode != 0)
        {
            if (Streamline.RenderSize(UpscaleMode, width, height) is { } traced)
            {
                (Width, Height) = traced;
                Upscaling = true;
            }
            else
            {
                UpscaleError = $"no render size for DLSS {Streamline.Modes[UpscaleMode]} at {width}x{height}";
                Console.WriteLine($"SCENE ray reconstruction: {UpscaleError}; traced without it");
            }
        }

        for (var i = 0; i < Images.Length; i++)
        {
            var (w, h) = 7 + i < 20 ? (Width, Height) : !Upscaling ? (1, 1) : i == Upscaled ? (width, height) : (Width, Height);
            targets[i] = new Target(device, physical, Images[i], w, h);
        }

        var infos = stackalloc VkDescriptorImageInfo[Images.Length];
        foreach (var set in sets)
        {
            for (var i = 0; i < Images.Length; i++)
            {
                infos[i] = new VkDescriptorImageInfo { ImageView = targets[i].View, ImageLayout = VkImageLayout.General };
                device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = set, DstBinding = 7 + i, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &infos[i] }], []);
            }
        }

        var picture = new VkDescriptorImageInfo { Sampler = compositeSampler, ImageView = targets[Upscaling ? Upscaled : Output].View, ImageLayout = VkImageLayout.General };
        device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = composite, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &picture }], []);
        fresh = true;
    }

    // Starts again from nothing kept, as after a cut or being switched on.
    public void Forget() => fresh = true;

    // Before the render pass, after RayScene.Record: the frame traced, kept and filtered into the picture.
    public void Record(CommandBuffer cb, int slot, SN.Matrix4x4 view, SN.Matrix4x4 projection, SN.Vector3 eye, float fovDegrees, float haze, bool photographed, SN.Vector4 background)
    {
        var viewProj = view * projection;
        // The slot's last timestamps, its fence waited for before this recording.
        if (timed[slot])
        {
            var stamps = stackalloc ulong[2];
            device.GetQueryPoolResults(timestamps, 2 * slot, 2, new ReadOnlySpan<byte>(stamps, 16), new ByteSize64(8UL), VkQueryResultFlags._64Bit);
            if (stamps[1] > stamps[0] && GpuMilliseconds.Count < 100_000)
            {
                GpuMilliseconds.Add((stamps[1] - stamps[0]) * (double)tick / 1e6);
            }
        }

        cb.ResetQueryPool(timestamps, 2 * slot, 2);
        timed[slot] = true;

        SN.Matrix4x4.Invert(viewProj, out var inverse);
        var reset = fresh;
        var tracing = Pipeline ? VkPipelineStageFlags.RayTracingShaderBitKHR : VkPipelineStageFlags.ComputeShaderBit;
        if (fresh)
        {
            // Every image from nothing to General, where it stays.
            var barriers = targets.Select(t => Stage.Barrier(t.Image, VkImageLayout.Undefined, VkImageLayout.General, VkAccessFlags.None, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit)).ToArray();
            cb.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, tracing | VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [], [], barriers);
            previousViewProj = viewProj;
            fresh = false;
        }
        else
        {
            // Last frame's filter, Ray Reconstruction and picture read and written before this one's trace starts.
            Memory(cb, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit | (Upscaling ? VkPipelineStageFlags.AllCommandsBit : 0), tracing);
        }

        // Ray Reconstruction's jitter: Halton's (2, 3) sequence, over as many phases as DLSS asks for the scale.
        var (jx, jy) = (0f, 0f);
        if (Upscaling)
        {
            var scale = OutputWidth / (float)Width;
            var phases = Math.Max(8, (int)MathF.Ceiling(8f * scale * scale));
            var index = (int)(frame % phases) + 1;
            (jx, jy) = (Halton(index, 2), Halton(index, 3));
        }

        SN.Matrix4x4.Invert(view, out var toWorld);
        var forward = -new SN.Vector3(toWorld.M31, toWorld.M32, toWorld.M33);
        var pixelAngle = 2f * MathF.Tan(fovDegrees * MathF.PI / 360f) / Height;
        *(TraceFrame*)(uniforms.Mapped + (slot * UniformStep)) = new TraceFrame
        {
            InvViewProj = inverse,
            PrevViewProj = previousViewProj,
            Eye = new(eye, haze),
            Size = new(Width, Height, frame, MaxOut ? MaxOutSamples : 1),
            Options = new(photographed ? 1f : 0f, MaxOut ? MaxOutBounces : Bounces, MaxHistory, reset ? 1f : 0f),
            Background = background with { W = pixelAngle },
            Upscale = new(Upscaling ? 1f : 0f, jx, jy, -projection.M33),
            Forward = new(forward, projection.M43),
        };
        var prior = previousViewProj;
        previousViewProj = viewProj;
        var parity = (int)(frame & 1);
        var frameIndex = frame;
        frame++;

        // Written once everything before it is done (the ray scene's build, the last frame), so that the two stamps
        // hold the trace and the filter alone.
        cb.WriteTimestamp(VkPipelineStageFlags.BottomOfPipeBit, timestamps, 2 * slot);
        var (gx, gy) = ((Width + Group - 1) / Group, (Height + Group - 1) / Group);
        Push(cb, new PassPush(parity, 0, 0));
        if (Pipeline)
        {
            cb.BindPipeline(VkPipelineBindPoint.RayTracingKHR, raygen);
            cb.BindDescriptorSets(VkPipelineBindPoint.RayTracingKHR, layout, 0, [sets[slot]], []);
            var (g, m, h, none) = (generate, miss, hits, default(VkStridedDeviceAddressRegionKHR));
            cb.TraceRaysKHR(&g, &m, &h, &none, Width, Height, 1);
        }
        else
        {
            cb.BindPipeline(VkPipelineBindPoint.Compute, trace);
            cb.BindDescriptorSets(VkPipelineBindPoint.Compute, layout, 0, [sets[slot]], []);
            cb.Dispatch(gx, gy, 1);
        }

        if (Upscaling)
        {
            // The guides written, Ray Reconstruction denoises and upscales the light into the window's picture; it
            // leaves the command buffer's bindings its own, so the shoulder's pass binds all again after it.
            Memory(cb, tracing, VkPipelineStageFlags.AllCommandsBit);
            SN.Matrix4x4.Invert(projection, out var toView);
            SN.Matrix4x4.Invert(prior, out var fromPrior);
            var constants = new Streamline.Constants
            {
                CameraViewToClip = projection,
                ClipToCameraView = toView,
                ClipToLensClip = SN.Matrix4x4.Identity,
                ClipToPrevClip = inverse * prior,
                PrevClipToClip = fromPrior * viewProj,
                JitterOffset = new SN.Vector2(0.5f - jx, 0.5f - jy) * JitterSign,
                MvecScale = new(1f / Width, 1f / Height),
                CameraPos = eye,
                CameraUp = new(toWorld.M21, toWorld.M22, toWorld.M23),
                CameraRight = new(toWorld.M11, toWorld.M12, toWorld.M13),
                CameraFwd = forward,
                // System.Numerics' perspective, depth 0 to 1: M33 = far / (near - far), M43 = near far / (near - far).
                CameraNear = projection.M43 / projection.M33,
                CameraFar = projection.M43 / (projection.M33 + 1f),
                CameraFov = fovDegrees * MathF.PI / 180f,
                CameraAspectRatio = OutputWidth / (float)OutputHeight,
                MotionVectorsInvalidValue = float.MaxValue,
                CameraMotionIncluded = 1,
                Reset = (byte)(reset ? 1 : 0),
                MinRelativeLinearDepthObjectSeparation = 40f,
            };
            var options = new Streamline.DLSSDOptions
            {
                Mode = UpscaleMode,
                OutputWidth = (uint)OutputWidth,
                OutputHeight = (uint)OutputHeight,
                PreExposure = 1f,
                ExposureScale = 1f,
                ColorBuffersHDR = 1,
                NormalRoughnessMode = 1, // packed: the roughness in the normal's w
                WorldToCameraView = view,
                CameraViewToWorld = toWorld,
            };
            var resources = stackalloc Streamline.Resource[9];
            int[] tagged = [Colour, Upscaled, Depth, Motion, Diffuse, Specular, NormalRoughness, SpecularHit, DepthBuffer];
            uint[] types = [Streamline.BufferScalingInputColor, Streamline.BufferScalingOutputColor, Streamline.BufferLinearDepth, Streamline.BufferMotionVectors,
                Streamline.BufferAlbedo, Streamline.BufferSpecularAlbedo, Streamline.BufferNormalRoughness, Streamline.BufferSpecularHitDistance, Streamline.BufferDepth];
            var tags = new Streamline.ResourceTag[tagged.Length];
            for (var i = 0; i < tagged.Length; i++)
            {
                var target = targets[tagged[i]];
                resources[i] = target.Resource();
                tags[i] = Streamline.Tag(&resources[i], types[i], target.Width, target.Height);
            }

            if (Streamline.EvaluateRayReconstruction(cb.VkHandle, frameIndex, ref constants, ref options, tags) is { } failed)
            {
                if (UpscaleError is null)
                {
                    Console.WriteLine($"SCENE ray reconstruction failed: {failed}");
                }

                UpscaleError = failed;
            }
            else
            {
                Evaluations++;
            }

            Memory(cb, VkPipelineStageFlags.AllCommandsBit, VkPipelineStageFlags.ComputeShaderBit);
            cb.BindPipeline(VkPipelineBindPoint.Compute, filter);
            cb.BindDescriptorSets(VkPipelineBindPoint.Compute, layout, 0, [sets[slot]], []);
            Push(cb, new PassPush(parity, 0, 2));
            cb.Dispatch((OutputWidth + Group - 1) / Group, (OutputHeight + Group - 1) / Group, 1);
        }
        else
        {
            cb.BindPipeline(VkPipelineBindPoint.Compute, filter);
            cb.BindDescriptorSets(VkPipelineBindPoint.Compute, layout, 0, [sets[slot]], []);
            for (var pass = 0; pass < 3; pass++)
            {
                Memory(cb, pass == 0 ? tracing : VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit);
                Push(cb, new PassPush(parity, pass, pass == 2 ? 1 : 0));
                cb.Dispatch(gx, gy, 1);
            }
        }

        cb.WriteTimestamp(VkPipelineStageFlags.BottomOfPipeBit, timestamps, (2 * slot) + 1);
        Memory(cb, VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.FragmentShaderBit);
    }

    // The trace's and the filter's GPU time, the middle and the 95th percentile, for the log.
    public string Measured()
    {
        if (GpuMilliseconds.Count == 0)
        {
            return "no frame measured";
        }

        var sorted = GpuMilliseconds.Order().ToArray();
        var upscaled = Upscaling
            ? $"; traced at {Width}x{Height}, Ray Reconstruction ({Streamline.Modes[UpscaleMode]}) to {OutputWidth}x{OutputHeight}, {Evaluations} evaluations{(UpscaleError is { } e ? $", last error: {e}" : "")}"
            : "";
        return $"GPU {sorted[sorted.Length / 2]:F2} ms p50, {sorted[(int)(sorted.Length * 0.95)]:F2} ms p95 over {sorted.Length} frames, by {How}{upscaled}";
    }

    private static float Halton(int index, int b)
    {
        var (f, r) = (1f, 0f);
        while (index > 0)
        {
            f /= b;
            r += f * (index % b);
            index /= b;
        }

        return r;
    }

    // In the render pass, after the meshes: the picture over them, through the splash's pipeline.
    public void Composite(CommandBuffer cb, VkPipeline splashPipeline, VkPipelineLayout splashLayout)
    {
        cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
        cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [composite], []);
        var push = new SplashPush(new SN.Vector4(-1f, -1f, 1f, 1f), SN.Vector4.One);
        cb.PushConstants(splashLayout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
        cb.Draw(6, 1, 0, 0);
    }

    public void Dispose()
    {
        DestroyTargets();
        if (Pipeline)
        {
            device.DestroyPipeline(raygen, null);
            table!.Dispose();
        }
        else
        {
            device.DestroyPipeline(trace, null);
        }

        device.DestroyQueryPool(timestamps, null);
        device.DestroyPipeline(filter, null);
        device.DestroyPipelineLayout(layout, null);
        device.DestroyDescriptorPool(pool, null);
        device.DestroyDescriptorSetLayout(setLayout, null);
        device.DestroySampler(compositeSampler, null);
        uniforms.Dispose();
    }

    private void DestroyTargets()
    {
        for (var i = 0; i < targets.Length; i++)
        {
            targets[i]?.Dispose();
            targets[i] = null!;
        }
    }

    private readonly VkShaderStageFlags pushStages;

    private void Push(CommandBuffer cb, PassPush push) =>
        cb.PushConstants(layout, pushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(PassPush)));

    // The ray tracing pipeline: the trace as a ray generation shader, a miss shader that does nothing (the hit objects
    // are read in the ray generation shader, no hit shader runs) and a triangles' hit group without shaders; and its
    // shader binding table, each group's handle at the base alignment.
    private (VkPipeline, Gpu.Buffer, VkStridedDeviceAddressRegionKHR, VkStridedDeviceAddressRegionKHR, VkStridedDeviceAddressRegionKHR) RayPipeline(bool reorder)
    {
        var source = "#version 460\n#define RAYGEN 1\n" + (reorder ? "#define SER 1\n" : "") + Header + TraceShader;
        var generateModule = Stage.Shader(device, source, ShaderKind.RayGenShader, "path-trace.rgen", vulkan13: true);
        var missModule = Stage.Shader(device, "#version 460\n#extension GL_EXT_ray_tracing : require\nlayout(location = 0) rayPayloadInEXT float unused;\nvoid main() { }\n",
            ShaderKind.MissShader, "path-trace.rmiss", vulkan13: true);
        const int unused = ~0;
        var entry = "main"u8;
        VkPipeline pipeline;
        fixed (byte* entryName = entry)
        {
            var shaderStages = stackalloc VkPipelineShaderStageCreateInfo[]
            {
                new() { Stage = VkShaderStageFlags.RaygenBitKHR, Module = generateModule, Name = entryName },
                new() { Stage = VkShaderStageFlags.MissBitKHR, Module = missModule, Name = entryName },
            };
            var groups = stackalloc VkRayTracingShaderGroupCreateInfoKHR[]
            {
                new() { Type = VkRayTracingShaderGroupTypeKHR.GeneralKHR, GeneralShader = 0, ClosestHitShader = unused, AnyHitShader = unused, IntersectionShader = unused },
                new() { Type = VkRayTracingShaderGroupTypeKHR.GeneralKHR, GeneralShader = 1, ClosestHitShader = unused, AnyHitShader = unused, IntersectionShader = unused },
                new() { Type = VkRayTracingShaderGroupTypeKHR.TrianglesHitGroupKHR, GeneralShader = unused, ClosestHitShader = unused, AnyHitShader = unused, IntersectionShader = unused },
            };
            device.CreateRayTracingPipelinesKHR(default, default,
                [new VkRayTracingPipelineCreateInfoKHR { StageCount = 2, Stages = shaderStages, GroupCount = 3, Groups = groups, MaxPipelineRayRecursionDepth = 1, Layout = layout }],
                default, new Span<VkPipeline>(&pipeline, 1));
        }

        device.DestroyShaderModule(generateModule, null);
        device.DestroyShaderModule(missModule, null);

        var properties = new VkPhysicalDeviceRayTracingPipelinePropertiesKHR();
        var asked = new VkPhysicalDeviceProperties2 { Next = &properties };
        physical.GetProperties2(ref asked);
        var handle = (int)properties.ShaderGroupHandleSize;
        var step = Align(Align(handle, (int)properties.ShaderGroupHandleAlignment), (int)properties.ShaderGroupBaseAlignment);
        var handles = new byte[3 * handle];
        device.GetRayTracingShaderGroupHandlesKHR(pipeline, 0, 3, handles);
        var align = (int)properties.ShaderGroupBaseAlignment;
        var buffer = new Gpu.Buffer(device, physical, (ulong)((3 * step) + align), VkBufferUsageFlags.ShaderBindingTableBitKHR | VkBufferUsageFlags.ShaderDeviceAddressBit, hostVisible: true);
        var start = (int)((ulong)Align((long)buffer.Address, align) - buffer.Address);
        for (var g = 0; g < 3; g++)
        {
            handles.AsSpan(g * handle, handle).CopyTo(new Span<byte>(buffer.Mapped + start + (g * step), handle));
        }

        VkStridedDeviceAddressRegionKHR Region(int g) => new()
        {
            DeviceAddress = new VkDeviceAddress(buffer.Address + (ulong)start + (ulong)(g * step)),
            Stride = new ByteSize64((ulong)step),
            Size = new ByteSize64((ulong)step),
        };
        return (pipeline, buffer, Region(0), Region(1), Region(2));
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    private static long Align(long value, int to) => (value + to - 1) / to * to;

    private static void Memory(CommandBuffer cb, VkPipelineStageFlags from, VkPipelineStageFlags to)
    {
        var barrier = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.ShaderWriteBit | VkAccessFlags.ShaderReadBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
        };
        cb.PipelineBarrier(from, to, VkDependencyFlags.None, [barrier], [], []);
    }

    private VkPipeline Compute(string source, string name)
    {
        var module = Stage.Shader(device, source, ShaderKind.ComputeShader, name, vulkan13: true);
        var entry = "main"u8;
        VkPipeline pipeline;
        fixed (byte* entryName = entry)
        {
            device.CreateComputePipelines(default,
                [new VkComputePipelineCreateInfo { Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = module, Name = entryName }, Layout = layout }],
                default, new Span<VkPipeline>(&pipeline, 1));
        }

        device.DestroyShaderModule(module, null);
        return pipeline;
    }

    // What both passes read once a frame, std140: the camera's inverse and the last frame's view, the eye and the
    // haze's density, the picture's size and the frame's number, the options (photographed, bounces, frames kept,
    // start over), and what is drawn where nothing is, with a pixel's angle in w.
    [StructLayout(LayoutKind.Sequential)]
    private struct TraceFrame
    {
        public SN.Matrix4x4 InvViewProj;
        public SN.Matrix4x4 PrevViewProj;
        public SN.Vector4 Eye;
        public SN.Vector4 Size;
        public SN.Vector4 Options;
        public SN.Vector4 Background;
        public SN.Vector4 Upscale; // Ray Reconstruction: on, and the jitter within the pixel
        public SN.Vector4 Forward; // the view's direction, for the depth along it
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PassPush(int parity, int pass, int final)
    {
        public readonly int Parity = parity;
        public readonly int Pass = pass;
        public readonly int Final = final;
        public readonly int Pad = 0;
    }

    // A storage image of the picture's size, and its memory and view.
    private sealed class Target : IDisposable
    {
        private readonly Device device;
        private readonly VkDeviceMemory memory;

        public const VkImageUsageFlags Usage = VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit;

        public Target(Device device, PhysicalDevice physical, VkFormat format, int width, int height)
        {
            this.device = device;
            Format = format;
            Width = width;
            Height = height;
            Image = device.CreateImage(new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = Usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            }, null);
            memory = device.AllocateMemory(physical, Image, VkMemoryPropertyFlags.DeviceLocalBit, null);
            device.BindImageMemory(Image, memory, ByteSize.Zero);
            View = device.CreateImageView(new VkImageViewCreateInfo
            {
                Image = Image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            }, null);
        }

        public VkImage Image { get; }

        public VkImageView View { get; }

        public VkFormat Format { get; }

        public int Width { get; }

        public int Height { get; }

        // As Streamline takes it, in the General layout.
        public Streamline.Resource Resource() =>
            Streamline.Image(Image.VkHandle, memory.VkHandle, View.VkHandle, Width, Height, (uint)Format, (uint)Usage);

        public void Dispose()
        {
            device.DestroyImageView(View, null);
            device.DestroyImage(Image, null);
            device.FreeMemory(memory, null);
        }
    }

    // After #version and the defines: RAYGEN for the ray tracing pipeline's ray generation shader, SER with NVIDIA's
    // hit objects and their reordering.
    private const string Header = """
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #ifdef RAYGEN
        #extension GL_EXT_ray_tracing : require
        #else
        layout(local_size_x = 8, local_size_y = 8) in;
        #endif
        #ifdef SER
        #extension GL_NV_shader_invocation_reorder : require
        #endif
        layout(set = 0, binding = 0) uniform Trace { mat4 invViewProj; mat4 prevViewProj; vec4 eye; vec4 size; vec4 options; vec4 background; vec4 upscale; vec4 forward; } t;
        layout(set = 0, binding = 1) uniform accelerationStructureEXT hallRays;
        struct Inst { uvec2 vertices; uint count; uint pad; vec4 glow; vec4 prev0; vec4 prev1; vec4 prev2; };
        layout(std430, set = 0, binding = 2) readonly buffer Instances { Inst inst[]; };
        layout(set = 0, binding = 3) uniform sampler2DArray albedoMaps;
        layout(set = 0, binding = 4) uniform sampler2DArray normalMaps;
        layout(set = 0, binding = 5) uniform sampler2DArray armMaps;
        layout(set = 0, binding = 6) uniform sampler2DArray globeMaps;
        layout(set = 0, binding = 7, rgba32f) uniform image2D posA;
        layout(set = 0, binding = 8, rgba32f) uniform image2D posB;
        layout(set = 0, binding = 9, rgba16f) uniform image2D nrmA;
        layout(set = 0, binding = 10, rgba16f) uniform image2D nrmB;
        layout(set = 0, binding = 11, rgba32f) uniform image2D histA;
        layout(set = 0, binding = 12, rgba32f) uniform image2D histB;
        layout(set = 0, binding = 13, rgba16f) uniform image2D albedoA;
        layout(set = 0, binding = 14, rgba16f) uniform image2D albedoB;
        layout(set = 0, binding = 15, rgba16f) uniform image2D glowA;
        layout(set = 0, binding = 16, rgba16f) uniform image2D glowB;
        layout(set = 0, binding = 17, rgba16f) uniform image2D filtA;
        layout(set = 0, binding = 18, rgba16f) uniform image2D filtB;
        layout(set = 0, binding = 19, rgba16f) uniform image2D picture;
        layout(set = 0, binding = 20, rg16f) uniform image2D rrMotion;
        layout(set = 0, binding = 21, r32f) uniform image2D rrDepth;
        layout(set = 0, binding = 22, rgba16f) uniform image2D rrDiffuse;
        layout(set = 0, binding = 23, rgba16f) uniform image2D rrSpecular;
        layout(set = 0, binding = 24, rgba16f) uniform image2D rrNormal;
        layout(set = 0, binding = 25, rgba16f) uniform image2D rrColour;
        layout(set = 0, binding = 26, rgba16f) uniform image2D rrUpscaled;
        layout(set = 0, binding = 27, r32f) uniform image2D rrSpecularHit;
        layout(set = 0, binding = 28, r32f) uniform image2D fgDepth;
        layout(push_constant) uniform Pass { int parity; int pass; int final; int pad; } pc;

        // This frame's (now) or last frame's images, the two swapping each frame.
        vec4 loadPos(ivec2 p, bool now) { return (pc.parity == 0) == now ? imageLoad(posA, p) : imageLoad(posB, p); }
        vec4 loadNrm(ivec2 p, bool now) { return (pc.parity == 0) == now ? imageLoad(nrmA, p) : imageLoad(nrmB, p); }
        vec4 loadHist(ivec2 p, bool now) { return (pc.parity == 0) == now ? imageLoad(histA, p) : imageLoad(histB, p); }
        vec4 loadAlbedo(ivec2 p, bool now) { return (pc.parity == 0) == now ? imageLoad(albedoA, p) : imageLoad(albedoB, p); }
        vec4 loadGlow(ivec2 p, bool now) { return (pc.parity == 0) == now ? imageLoad(glowA, p) : imageLoad(glowB, p); }

        // This frame's surface, light, colour and glow.
        void store(ivec2 p, vec4 pos, vec4 nrm, vec4 hist, vec4 albedo, vec4 glow)
        {
            if (pc.parity == 0)
            {
                imageStore(posA, p, pos); imageStore(nrmA, p, nrm); imageStore(histA, p, hist); imageStore(albedoA, p, albedo); imageStore(glowA, p, glow);
            }
            else
            {
                imageStore(posB, p, pos); imageStore(nrmB, p, nrm); imageStore(histB, p, hist); imageStore(albedoB, p, albedo); imageStore(glowB, p, glow);
            }
        }

        float luma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

        // The haze, as the Low lighting's.
        vec3 haze(vec3 c, vec3 p)
        {
            float density = t.eye.w * exp(-0.35 * max(0.5 * (p.y + t.eye.y), 0.0));
            float through = exp(-density * max(length(p - t.eye.xyz) - 3.0, 0.0));
            return mix(vec3(0.075, 0.09, 0.115), c, through);
        }

        """;

    private const string TraceShader = """
        layout(buffer_reference, std430, buffer_reference_align = 4) readonly buffer Floats { float f[]; };

        const float PI = 3.14159265;
        uint seed;
        float rnd()
        {
            seed = seed * 747796405u + 2891336453u;
            uint w = ((seed >> ((seed >> 28u) + 4u)) ^ seed) * 277803737u;
            return float((w >> 22u) ^ w) * (1.0 / 4294967296.0);
        }

        // As the Low lighting: each photographed material's layer, tile, relief and whether only its grain is taken,
        // and each plain material's reflectance, gloss, metal and grazing.
        const vec4 scans[6] = vec4[](
            vec4(0.0, 2.7, 0.023, 0.0), vec4(1.0, 3.0, 0.003, 0.0), vec4(2.0, 3.0, 0.004, 0.0),
            vec4(3.0, 0.5, 0.003, 0.0), vec4(4.0, 1.0, 0.0008, 0.0), vec4(2.0, 0.6, 0.002, 1.0));
        const vec4 materials[8] = vec4[](
            vec4(0.02, 6.0, 0.0, 0.12), vec4(0.05, 90.0, 0.0, 0.8), vec4(0.0, 40.0, 1.0, 1.0),
            vec4(0.0, 24.0, 1.0, 0.8), vec4(0.02, 4.0, 0.0, 0.1), vec4(0.04, 260.0, 0.0, 1.0),
            vec4(0.03, 5.0, 0.0, 0.35), vec4(0.04, 36.0, 0.0, 0.55)); // rope and turned oak (Material.Rope, Material.Wood)

        void project(vec3 p, vec3 face, float metres, out vec2 uv, out vec3 tt, out vec3 bb)
        {
            vec3 a = abs(face);
            if (a.y >= a.x && a.y >= a.z)
            {
                float s = face.y < 0.0 ? -1.0 : 1.0;
                uv = vec2(p.x, s * p.z) / metres; tt = vec3(1.0, 0.0, 0.0); bb = vec3(0.0, 0.0, -s);
            }
            else if (a.x >= a.z)
            {
                float s = face.x < 0.0 ? -1.0 : 1.0;
                uv = vec2(-s * p.z, -p.y) / metres; tt = vec3(0.0, 0.0, -s); bb = vec3(0.0, 1.0, 0.0);
            }
            else
            {
                float s = face.z < 0.0 ? -1.0 : 1.0;
                uv = vec2(s * p.x, -p.y) / metres; tt = vec3(s, 0.0, 0.0); bb = vec3(0.0, 1.0, 0.0);
            }
        }

        void across(vec3 n, out vec3 tt, out vec3 bb)
        {
            tt = normalize(cross(n, abs(n.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
            bb = cross(n, tt);
        }

        // What the hall's surroundings show a ray leaving it, as the Low lighting's reflections.
        vec3 hall(vec3 r)
        {
            vec3 ground = vec3(0.11, 0.095, 0.08);
            vec3 wall = vec3(0.06, 0.035, 0.025);
            vec3 roof = vec3(0.012, 0.013, 0.016);
            vec3 c = r.y < 0.0 ? mix(wall, ground, smoothstep(0.0, -0.35, r.y)) : mix(wall, roof, smoothstep(0.05, 0.6, r.y));
            return c * (r.z < 0.0 ? 1.0 : 0.7);
        }

        // The lamps' reach: the Low lighting's pool around the robot and its dark above, here the shape of the key
        // and the fill's beams.
        float beam(vec3 p)
        {
            float pool = 1.0 - smoothstep(0.8, 6.0, length(p.xz - vec2(0.0, -0.3)));
            float high = 1.0 - 0.6 * smoothstep(1.6, 4.5, p.y);
            return (0.1 + 0.9 * pool) * high;
        }

        struct Surface
        {
            vec3 p; vec3 n; vec3 face; vec3 base; vec3 glow; vec3 before;
            float alpha; float metal; float f0; float dist;
        };

        vec3 at3(Floats v, int i) { return vec3(v.f[i], v.f[i + 1], v.f[i + 2]); }
        vec4 at4(Floats v, int i) { return vec4(v.f[i], v.f[i + 1], v.f[i + 2], v.f[i + 3]); }

        // The first surface along d from o within tmax, read from its mesh: false when there is none. spread is the
        // ray's width there, in metres, for the scans' detail.
        #ifdef SER
        layout(location = 0) rayPayloadEXT float unusedPayload;
        layout(location = 0) hitObjectAttributeNV vec2 hitBarycentrics;
        #endif

        bool hit(vec3 o, vec3 d, float tmax, float spread, bool sort, out Surface s)
        {
            #ifdef SER
            // Traced into a hit object; a bounce's invocations are then sorted by the instance they hit, so that those
            // reading the same mesh, scans and material run together (the eye's rays are coherent already, and sorting
            // them cost more than it gave). No hit shader runs.
            hitObjectNV q;
            hitObjectTraceRayNV(q, hallRays, gl_RayFlagsOpaqueEXT, 0xFFu, 0u, 0u, 0u, o, 0.0, d, tmax, 0);
            if (sort)
            {
                uint hint = hitObjectIsHitNV(q) ? uint(hitObjectGetInstanceIdNV(q)) + 1u : 0u;
                reorderThreadNV(q, hint, 8);
            }
            if (!hitObjectIsHitNV(q))
            {
                return false;
            }

            float h = hitObjectGetRayTMaxNV(q);
            Inst ins = inst[hitObjectGetInstanceIdNV(q)];
            int prim = hitObjectGetPrimitiveIndexNV(q);
            hitObjectGetAttributesNV(q, 0);
            vec2 bc = hitBarycentrics;
            mat4x3 toWorld = hitObjectGetObjectToWorldNV(q);
            mat4x3 toObject = hitObjectGetWorldToObjectNV(q);
            #else
            rayQueryEXT q;
            rayQueryInitializeEXT(q, hallRays, gl_RayFlagsOpaqueEXT, 0xFFu, o, 0.0, d, tmax);
            while (rayQueryProceedEXT(q))
            {
            }

            if (rayQueryGetIntersectionTypeEXT(q, true) == gl_RayQueryCommittedIntersectionNoneEXT)
            {
                return false;
            }

            float h = rayQueryGetIntersectionTEXT(q, true);
            Inst ins = inst[rayQueryGetIntersectionInstanceIdEXT(q, true)];
            int prim = rayQueryGetIntersectionPrimitiveIndexEXT(q, true);
            vec2 bc = rayQueryGetIntersectionBarycentricsEXT(q, true);
            mat4x3 toWorld = rayQueryGetIntersectionObjectToWorldEXT(q, true);
            mat4x3 toObject = rayQueryGetIntersectionWorldToObjectEXT(q, true);
            #endif
            Floats v = Floats(ins.vertices);
            int i0 = prim * 51, i1 = i0 + 17, i2 = i0 + 34;
            vec3 w = vec3(1.0 - bc.x - bc.y, bc.x, bc.y);
            vec3 p0 = at3(v, i0), p1 = at3(v, i1), p2 = at3(v, i2);
            vec3 local = p0 * w.x + p1 * w.y + p2 * w.z;
            mat3 normals = transpose(mat3(toObject));
            vec3 geometric = normalize(normals * cross(p1 - p0, p2 - p0));
            vec3 shading = normalize(normals * (at3(v, i0 + 3) * w.x + at3(v, i1 + 3) * w.y + at3(v, i2 + 3) * w.z));
            vec3 colour = at3(v, i0 + 6);
            uint material = floatBitsToUint(v.f[i0 + 9]);
            vec3 tangent = mat3(toWorld) * (at3(v, i0 + 10) * w.x + at3(v, i1 + 10) * w.y + at3(v, i2 + 10) * w.z);
            vec4 edge = at4(v, i0 + 13) * w.x + at4(v, i1 + 13) * w.y + at4(v, i2 + 13) * w.z;

            s.dist = h;
            s.p = o + d * h;
            s.face = dot(geometric, d) > 0.0 ? -geometric : geometric; // seen from the ray's side, both sides drawn
            shading = dot(shading, s.face) < 0.0 ? -shading : shading;
            s.before = vec3(dot(ins.prev0, vec4(local, 1.0)), dot(ins.prev1, vec4(local, 1.0)), dot(ins.prev2, vec4(local, 1.0)));
            s.glow = ins.glow.rgb * ins.glow.a;
            s.base = colour;
            s.metal = 0.0;
            s.f0 = 0.04;
            if (material == 16u)
            {
                vec2 uv = edge.xy * vec2(2.0, 1.0);
                float east = uv.x >= 1.0 ? 1.0 : 0.0;
                float lod = log2(max(spread * 4096.0, 1.0));
                s.base = textureLod(globeMaps, vec3(uv.x - east, uv.y, edge.z + east), lod).rgb;
                float water = smoothstep(0.02, 0.08, s.base.b - s.base.r);
                s.alpha = mix(0.5, 0.17, water);
                s.f0 = 0.02;
                s.n = shading;
            }
            else if (material >= 8u && t.options.x > 0.5)
            {
                vec4 scan = scans[min(material - 8u, 5u)];
                vec2 uv;
                vec3 tt, bb;
                project(s.p, shading, scan.y, uv, tt, bb);
                float lod = log2(max(spread * 2048.0 / scan.y, 1.0));
                vec3 c = textureLod(albedoMaps, vec3(uv, scan.x), lod).rgb;
                vec3 tn = textureLod(normalMaps, vec3(uv, scan.x), lod).xyz * 2.0 - 1.0;
                vec3 arm = textureLod(armMaps, vec3(uv, scan.x), lod).xyz;
                s.n = normalize(tt * tn.x + bb * tn.y + shading * max(tn.z, 0.05));
                s.base = (scan.w > 0.5 ? colour * clamp(luma(c) * 3.0, 0.55, 1.45) : c * colour) * mix(0.6, 1.0, arm.x);
                float r = clamp(arm.y, 0.05, 1.0);
                s.alpha = r * r;
                s.metal = arm.z;
            }
            else
            {
                // The Low lighting's rounded edges, 8 mm wide at most.
                vec3 side = normalize(tangent - shading * dot(tangent, shading));
                vec2 toEdge = (1.0 - abs(edge.xy)) * edge.zw;
                vec2 width = max(min(vec2(0.008), 0.35 * edge.zw), vec2(1e-4));
                vec2 lean = material == 6u || material == 7u ? vec2(0.0) : (1.0 - smoothstep(vec2(0.0), width, toEdge)) * sign(edge.xy); // a rope or post: its Edge is no box's
                s.n = normalize(shading + lean.x * side + lean.y * cross(shading, side));
                vec4 m = materials[material >= 8u ? 0u : min(material, 7u)];
                s.alpha = sqrt(2.0 / (m.y + 2.0));
                s.metal = m.z;
                s.f0 = m.x;
            }

            s.alpha = max(s.alpha, 0.03);
            return true;
        }

        bool blocked(vec3 o, vec3 d, float tmax)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, hallRays, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT, 0xFFu, o, 0.0, d, tmax);
            while (rayQueryProceedEXT(q))
            {
            }

            return rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT;
        }

        // pow of a negative number is undefined: a cosine a hair over 1 gave NaN in the ray generation shader's build
        // (black blots spread by the filter) though not in the compute pass's.
        vec3 fresnel(vec3 f0, float c) { return f0 + (1.0 - f0) * pow(clamp(1.0 - c, 0.0, 1.0), 5.0); }

        float ggx(float nh, float a) { float a2 = a * a; float d = nh * nh * (a2 - 1.0) + 1.0; return a2 / (PI * d * d); }

        // Smith's height-correlated visibility for GGX.
        float visibility(float nl, float nv, float a)
        {
            float a2 = a * a;
            return 0.5 / max(nl * sqrt(nv * nv * (1.0 - a2) + a2) + nv * sqrt(nl * nl * (1.0 - a2) + a2), 1e-5);
        }

        // The surface's reflection of light from l towards v, times the cosine: Lambert's diffuse under GGX's specular.
        vec3 reflection(Surface s, vec3 v, vec3 l)
        {
            float nl = dot(s.n, l), nv = max(dot(s.n, v), 1e-4);
            if (nl <= 0.0 || dot(s.face, l) <= 0.0)
            {
                return vec3(0.0);
            }

            vec3 h = normalize(v + l);
            vec3 f0 = mix(vec3(s.f0), s.base, s.metal);
            vec3 f = fresnel(f0, max(dot(v, h), 0.0));
            vec3 specular = f * ggx(max(dot(s.n, h), 0.0), s.alpha) * visibility(nl, nv, s.alpha);
            vec3 diffuse = (1.0 - f) * (1.0 - s.metal) * s.base / PI;
            return (diffuse + specular) * nl;
        }

        float specularChance(Surface s, vec3 v)
        {
            vec3 f0 = mix(vec3(s.f0), s.base, s.metal);
            float spec = luma(fresnel(f0, max(dot(s.n, v), 0.0)));
            float diff = luma(s.base) * (1.0 - s.metal);
            return clamp(spec / max(spec + diff, 1e-4), 0.1, 0.9);
        }

        // A direction to go on from s, seen from v, and the throughput's factor (reflection over the pdf), and whether
        // it was the specular lobe that chose it; false to stop.
        bool bounce(Surface s, vec3 v, out vec3 l, out vec3 weight, out bool specular)
        {
            float chance = specularChance(s, v);
            vec3 tt, bb;
            across(s.n, tt, bb);
            float u1 = rnd(), u2 = rnd();
            specular = rnd() < chance;
            if (specular)
            {
                float a2 = s.alpha * s.alpha;
                float c = sqrt((1.0 - u2) / (1.0 + (a2 - 1.0) * u2));
                float sn = sqrt(max(1.0 - c * c, 0.0));
                vec3 h = normalize(tt * cos(2.0 * PI * u1) * sn + bb * sin(2.0 * PI * u1) * sn + s.n * c);
                l = reflect(-v, h);
            }
            else
            {
                float r = sqrt(u1), a = 2.0 * PI * u2;
                l = normalize(tt * r * cos(a) + bb * r * sin(a) + s.n * sqrt(max(1.0 - u1, 0.0)));
            }

            float nl = dot(s.n, l);
            if (nl <= 0.0 || dot(s.face, l) <= 0.0)
            {
                return false;
            }

            vec3 h = normalize(v + l);
            float nh = max(dot(s.n, h), 0.0);
            float pdf = chance * ggx(nh, s.alpha) * nh / (4.0 * max(dot(v, h), 1e-4)) + (1.0 - chance) * nl / PI;
            weight = reflection(s, v, l) / max(pdf, 1e-5);
            return true;
        }

        // The key's and the fill's light reaching s and reflected towards v, each through a traced shadow; the key
        // about 4° across, as the Low lighting's.
        vec3 direct(Surface s, vec3 v)
        {
            vec3 o = s.p + s.face * 0.002;
            float reach = beam(s.p);
            vec3 key = normalize(vec3(-0.45, 0.8, 0.55));
            vec3 tt, bb;
            across(key, tt, bb);
            float a = 2.0 * PI * rnd(), r = sqrt(rnd()) * 0.035;
            vec3 l = normalize(key + (cos(a) * tt + sin(a) * bb) * r);
            vec3 light = vec3(0.0);
            vec3 f = reflection(s, v, l);
            if (f != vec3(0.0) && !blocked(o, l, 40.0))
            {
                light += f * vec3(1.0, 0.86, 0.68) * PI * reach;
            }

            vec3 fill = normalize(vec3(0.7, 0.25, 0.3));
            f = reflection(s, v, fill);
            if (f != vec3(0.0) && !blocked(o, fill, 40.0))
            {
                light += f * vec3(0.35, 0.45, 0.6) * 0.35 * PI * reach;
            }

            return light;
        }

        // How far the first path's first bounce went from the eye's surface when it was a reflection, for Ray
        // Reconstruction (0 when it was diffuse, 1000 when it left the hall); below 0 until a path sets it.
        float specularHit = -1.0;

        // One path from s, met by the ray d from the eye: the light it carries back.
        vec3 path(Surface s, vec3 d, int bounces)
        {
            vec3 light = vec3(0.0);
            vec3 through = vec3(1.0);
            Surface at = s;
            vec3 v = -d;
            float spread = s.dist * t.background.w;
            for (int b = 0; b < bounces; b++)
            {
                light += through * direct(at, v);
                vec3 l, weight;
                bool specular;
                if (!bounce(at, v, l, weight, specular))
                {
                    specularHit = specularHit < 0.0 && b == 0 ? 0.0 : specularHit;
                    break;
                }

                bool first = specularHit < 0.0 && b == 0;
                if (first && !specular)
                {
                    specularHit = 0.0;
                }

                through *= weight;
                if (b >= 1)
                {
                    float keep = clamp(max(through.r, max(through.g, through.b)), 0.05, 0.95);
                    if (rnd() > keep)
                    {
                        break;
                    }

                    through /= keep;
                }

                Surface next;
                spread += 0.02 + at.alpha * 0.2;
                if (!hit(at.p + at.face * 0.002, l, 60.0, spread, true, next))
                {
                    specularHit = first && specular ? 1000.0 : specularHit;
                    light += through * hall(l);
                    break;
                }

                specularHit = first && specular ? next.dist : specularHit;
                light += through * next.glow;
                spread += next.dist * at.alpha;
                at = next;
                v = -l;
            }

            return light;
        }

        // The light the surroundings reflect off a surface of reflectance f0 and GGX alpha, seen at nv: the specular
        // colour Ray Reconstruction is given (its guide's EnvBRDFApprox2, Ray Tracing Gems chapter 32). HLSL's
        // mul(M, x) is GLSL's x * M for the same list of numbers.
        vec3 specularAlbedo(vec3 f0, float alpha, float nv)
        {
            vec4 x = vec4(1.0, nv, nv * nv, nv * nv * nv);
            vec4 y = vec4(1.0, alpha, alpha * alpha, alpha * alpha * alpha);
            float bias = dot(x.xy * mat2(0.99044, -1.28514, 1.29678, -0.755907), y.xy)
                / dot(x.xyw * mat3(1.0, 2.92338, 59.4188, 20.3225, -27.0302, 222.592, 121.563, 626.13, 316.627), y.xyw);
            float scale = dot(x.xy * mat2(0.0365463, 3.32707, 9.0632, -9.04756), y.xy)
                / dot(x.xzw * mat3(1.0, 3.59685, -1.36772, 9.04401, -16.3174, 9.22949, 5.56589, 19.7886, -20.2123), y.xyw);
            bias *= clamp(f0.g * 50.0, 0.0, 1.0);
            return f0 * max(0.0, scale) + max(0.0, bias);
        }

        // Ray Reconstruction's guides for a pixel: its light, where its surface (before, last frame's place; w 0 for a
        // direction, the surroundings at infinity) was a frame ago, in pixels from where the jittered ray met it now,
        // its depth along the view, its colours, its normal and roughness, and its first reflection's length.
        void guides(ivec2 px, vec2 jitter, vec3 light, vec4 before, float depth, vec3 diffuse, vec3 specular, vec3 n, float roughness, float hitDistance)
        {
            vec4 clip = t.prevViewProj * before;
            vec2 motion = clip.w > 0.0 ? (clip.xy / clip.w * 0.5 + 0.5) * t.size.xy - (vec2(px) + jitter) : vec2(0.0);
            imageStore(rrMotion, px, vec4(motion, 0.0, 0.0));
            imageStore(rrDepth, px, vec4(depth));
            imageStore(rrDiffuse, px, vec4(diffuse, 1.0));
            imageStore(rrSpecular, px, vec4(specular, 1.0));
            imageStore(rrNormal, px, vec4(n, roughness));
            imageStore(rrColour, px, vec4(light, 1.0));
            imageStore(rrSpecularHit, px, vec4(hitDistance));
            // The projection's depth, from the depth along the view: -M33 + M43 / depth (upscale.w, forward.w).
            imageStore(fgDepth, px, vec4(clamp(t.upscale.w + t.forward.w / max(depth, 1e-4), 0.0, 1.0)));
        }

        void main()
        {
            #ifdef RAYGEN
            ivec2 px = ivec2(gl_LaunchIDEXT.xy);
            #else
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            #endif
            ivec2 size = ivec2(t.size.xy);
            if (px.x >= size.x || px.y >= size.y)
            {
                return;
            }

            seed = uint(px.x) * 1973u + uint(px.y) * 9277u + uint(t.size.z) * 26699u;
            rnd();
            vec2 jitter = vec2(rnd(), rnd());
            bool upscaled = t.upscale.x > 0.5;
            if (upscaled)
            {
                jitter = t.upscale.yz; // the same for every pixel, as Ray Reconstruction is told
            }

            vec2 ndc = (vec2(px) + jitter) / vec2(size) * 2.0 - 1.0;
            vec4 far = t.invViewProj * vec4(ndc, 0.5, 1.0);
            vec3 eye = t.eye.xyz;
            vec3 d = normalize(far.xyz / far.w - eye);

            Surface s;
            if (!hit(eye, d, 60.0, t.background.w, false, s))
            {
                if (upscaled)
                {
                    // As NVIDIA's sample: the surroundings move as a point at infinity along the ray, and their colour,
                    // tone mapped, stands as their albedo, so that Ray Reconstruction keeps them as they are.
                    vec3 sky = t.background.rgb;
                    guides(px, jitter, sky, vec4(d, 0.0), 1000.0, sky / (1.0 + max(sky.r, max(sky.g, sky.b))), vec3(0.0), vec3(0.0), 0.0, 0.0);
                    return;
                }

                // Nothing: the surroundings, kept as nothing.
                store(px, vec4(0.0), vec4(0.0), vec4(0.0), vec4(0.0), vec4(t.background.rgb, 0.0));
                return;
            }

            // The path: the surface's own light, then up to the bounces' count of surfaces further, each lit; Max out
            // takes several paths from the same first surface and averages them.
            vec3 light = vec3(0.0);
            int paths = max(int(t.size.w), 1);
            for (int k = 0; k < paths; k++)
            {
                light += path(s, d, int(t.options.y));
            }

            light /= float(paths);

            if (upscaled)
            {
                // To Ray Reconstruction: the light as it is, its glow and haze, bright outliers cut; nothing kept or
                // filtered here.
                vec3 seen = light + s.glow;
                float l = luma(seen);
                seen *= l > 24.0 ? 24.0 / l : 1.0;
                vec3 f0 = mix(vec3(s.f0), s.base, s.metal);
                guides(px, jitter, haze(seen, s.p), vec4(s.before, 1.0), s.dist * dot(d, t.forward.xyz),
                    s.base * (1.0 - s.metal), specularAlbedo(f0, s.alpha, max(dot(s.n, -d), 1e-4)), s.n, sqrt(s.alpha), max(specularHit, 0.0));
                return;
            }

            // The light over the surface's colour, so that the filter keeps the scans' detail; bright outliers cut.
            vec3 albedo = max(s.base, vec3(0.03));
            vec3 sample0 = light / albedo;
            float bright = luma(sample0);
            sample0 *= bright > 24.0 ? 24.0 / bright : 1.0;

            // Where this surface was a frame ago: of the four pixels around that place, those that showed the same
            // surface, weighed by how near; what they kept is this pixel's past.
            vec4 clip = t.prevViewProj * vec4(s.before, 1.0);
            // The sample was jittered off the pixel's centre; the pixel stands for its centre, so the place it was is
            // taken back by the jitter, or each frame's blend would blur it a little more.
            vec2 was = (clip.xy / clip.w * 0.5 + 0.5) * vec2(size) - jitter;
            vec4 kept = vec4(0.0);
            vec3 keptAlbedo = vec3(0.0), keptGlow = vec3(0.0);
            float found = 0.0;
            if (t.options.w < 0.5 && clip.w > 0.0)
            {
                ivec2 corner = ivec2(floor(was));
                vec2 f = was - vec2(corner);
                float tolerance = 0.02 + s.dist * t.background.w * 4.0 / max(dot(s.face, -d), 0.1);
                for (int k = 0; k < 4; k++)
                {
                    ivec2 o = ivec2(k & 1, k >> 1);
                    ivec2 q = corner + o;
                    if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, size)))
                    {
                        continue;
                    }

                    vec4 lastPos = loadPos(q, false);
                    if (lastPos.w <= 0.0 || distance(lastPos.xyz, s.before) >= tolerance || dot(loadNrm(q, false).xyz, s.face) <= 0.8)
                    {
                        continue;
                    }

                    float w = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y) + 1e-3;
                    kept += loadHist(q, false) * w;
                    keptAlbedo += loadAlbedo(q, false).rgb * w;
                    keptGlow += loadGlow(q, false).rgb * w;
                    found += w;
                }
            }

            if (found > 0.0)
            {
                kept /= found;
                keptAlbedo /= found;
                keptGlow /= found;
                // A glow that changed (a clock's digit, a screen) starts the pixel over rather than fading.
                if (abs(luma(keptGlow) - luma(s.glow)) > 0.25 * max(luma(keptGlow), luma(s.glow)) + 0.02)
                {
                    kept.w = 0.0;
                }
            }

            float count = min(kept.w + 1.0, t.options.z);
            float a = 1.0 / count;
            store(px, vec4(s.p, s.dist), vec4(s.face, s.alpha), vec4(mix(kept.rgb, sample0, a), count),
                vec4(mix(keptAlbedo, albedo, a), 1.0), vec4(mix(keptGlow, s.glow, a), 1.0));
        }
        """;

    private const string FilterShader = """
        // The shoulder, as the Low lighting's.
        vec3 shoulder(vec3 x)
        {
            const float a = 0.6;
            return mix(x, a + (1.0 - a) * (1.0 - exp(-(x - a) / (1.0 - a))), step(a, x));
        }

        vec3 source(ivec2 p)
        {
            return pc.pass == 0 ? loadHist(p, true).rgb : pc.pass == 1 ? imageLoad(filtA, p).rgb : imageLoad(filtB, p).rgb;
        }

        // One à-trous pass, 5 × 5 taps 1, 2 or 4 pixels apart, each weighed by how alike its surface is (normal, plane)
        // and its light, the light's bar narrowing as frames are kept; the last pass gives the picture. With Ray
        // Reconstruction (final 2), the one pass puts its picture, at the window's size, through the shoulder.
        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (pc.final == 2)
            {
                if (all(lessThan(px, imageSize(rrUpscaled))))
                {
                    imageStore(rrUpscaled, px, vec4(shoulder(max(imageLoad(rrUpscaled, px).rgb, vec3(0.0))), 1.0));
                }

                return;
            }

            ivec2 size = ivec2(t.size.xy);
            if (px.x >= size.x || px.y >= size.y)
            {
                return;
            }

            vec4 pos = loadPos(px, true);
            vec3 result = vec3(0.0);
            if (pos.w > 0.0)
            {
                vec4 nrm = loadNrm(px, true);
                vec3 centre = source(px);
                float count = max(loadHist(px, true).w, 1.0);
                float l0 = luma(centre);
                float bar = 3.0 / sqrt(count) * max(l0, 0.05) + 0.01;
                int step = 1 << pc.pass;
                const float kernel[5] = float[](0.0625, 0.25, 0.375, 0.25, 0.0625);
                float plane = 0.01 + 0.004 * pos.w * float(step);
                vec3 sum = vec3(0.0);
                float total = 0.0;
                for (int y = -2; y <= 2; y++)
                {
                    for (int x = -2; x <= 2; x++)
                    {
                        ivec2 q = px + ivec2(x, y) * step;
                        if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, size)))
                        {
                            continue;
                        }

                        vec4 qp = loadPos(q, true);
                        if (qp.w <= 0.0)
                        {
                            continue;
                        }

                        vec3 c = source(q);
                        float wn = pow(max(dot(loadNrm(q, true).xyz, nrm.xyz), 0.0), 32.0);
                        float wp = exp(-abs(dot(nrm.xyz, qp.xyz - pos.xyz)) / plane);
                        float wl = exp(-abs(luma(c) - l0) / bar);
                        float w = kernel[x + 2] * kernel[y + 2] * wn * wp * wl;
                        sum += c * w;
                        total += w;
                    }
                }

                result = total > 0.0 ? sum / total : centre;
            }

            if (pc.final == 0)
            {
                if (pc.pass == 0) imageStore(filtA, px, vec4(result, 1.0)); else imageStore(filtB, px, vec4(result, 1.0));
                return;
            }

            vec4 glow = loadGlow(px, true);
            vec3 c = pos.w > 0.0
                ? haze(shoulder(result * loadAlbedo(px, true).rgb) + glow.rgb, pos.xyz)
                : glow.rgb;
            imageStore(picture, px, vec4(c, 1.0));
        }
        """;
}
