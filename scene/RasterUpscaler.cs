using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

// DLSS Super Resolution on the raster pass (the hall as the window draws it without path tracing): the meshes drawn
// at the size DLSS asks for, the projection jittered within a pixel (Halton's 2 and 3, as the path tracer's), then
// Streamline's DLSS upscales the picture to the window's size, and the cards and buttons are drawn over it at full
// size (the cards' pass). The motion vectors are an image of zeros with the camera's motion left out
// (cameraMotionIncluded false): Streamline then computes the camera's motion from the depth and the matrices
// (sl_consts.h), as the hall stands still; what moves on its own (the robots' arms, the tower's slow turn) has no
// vectors of its own and may trail a little.
//
// The pass is compatible with the window's (the same formats, one sample, the same subpass and dependency), so every
// pipeline made for the window's pass draws into it. It needs no MSAA (Low quality keeps its own way).
internal sealed unsafe class RasterUpscaler : IDisposable
{
    private const VkFormat OutputFormat = VkFormat.R16G16B16A16SFloat;
    private const VkFormat MotionFormat = VkFormat.R16G16SFloat;

    private readonly Device device;
    private readonly PhysicalDevice physical;
    private readonly VkFormat colourFormat;
    private readonly VkFormat depthFormat;
    private readonly VkSampler sampler;
    private readonly DescriptorPoolEx pool;
    private readonly VkDescriptorSet composite;
    private readonly VkRenderPass motionPass;
    private Picture? colour;
    private Picture? depth;
    private Picture? motion;
    private Picture? output;
    private VkFramebuffer framebuffer;
    private VkFramebuffer motionFramebuffer;
    private bool fresh = true;
    private uint frame;
    private SN.Matrix4x4 previousViewProj;
    private (int Width, int Height, uint Mode) sizedFor;

    public RasterUpscaler(Device device, PhysicalDevice physical, VkFormat colourFormat, VkFormat depthFormat, VkSubpassDependency dependency, DescriptorSetLayoutEx splashSetLayout)
    {
        this.device = device;
        this.physical = physical;
        this.colourFormat = colourFormat;
        this.depthFormat = depthFormat;

        // The window's pass, but its colour and depth kept for DLSS, in the General layout where Streamline reads them.
        Span<VkAttachmentDescription> attachments =
        [
            Attachment(colourFormat, VkAttachmentStoreOp.Store, VkImageLayout.General),
            Attachment(depthFormat, VkAttachmentStoreOp.Store, VkImageLayout.General),
        ];
        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var depthRef = new VkAttachmentReference { Attachment = 1, Layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorRef,
            DepthStencilAttachment = &depthRef,
        };
        fixed (VkAttachmentDescription* a = attachments)
        {
            Pass = device.CreateRenderPass(new VkRenderPassCreateInfo
            {
                AttachmentCount = 2,
                Attachments = a,
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 1,
                Dependencies = &dependency,
            }, null);
        }

        // The motion vectors' image is cleared once, by a pass that draws nothing.
        var motionAttachment = Attachment(MotionFormat, VkAttachmentStoreOp.Store, VkImageLayout.General);
        var motionRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var motionSubpass = new VkSubpassDescription { PipelineBindPoint = VkPipelineBindPoint.Graphics, ColorAttachmentCount = 1, ColorAttachments = &motionRef };
        motionPass = device.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = 1,
            Attachments = &motionAttachment,
            SubpassCount = 1,
            Subpasses = &motionSubpass,
        }, null);

        sampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Nearest,
            MinFilter = VkFilter.Nearest,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1f,
        }, null);
        pool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
        {
            MaxSets = 1,
            PoolSizes = [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 }],
        }, null);
        composite = device.AllocateDescriptorSet(pool, splashSetLayout);
    }

    // sl_dlss.h's DLSSMode, by Streamline.Modes' names; quality unless --dlss-sr-mode says otherwise.
    public uint Mode { get; set; } = 3;

    public VkRenderPass Pass { get; }

    public VkFramebuffer Framebuffer => framebuffer;

    // The size drawn at, and the window's.
    public int Width { get; private set; }

    public int Height { get; private set; }

    public int OutputWidth { get; private set; }

    public int OutputHeight { get; private set; }

    public long Evaluations { get; private set; }

    // Frames whose evaluation waited for the next one (Streamline.Skipped).
    public long Skips { get; private set; }

    private const int GivesUp = 120;
    private int failing;

    // The last failure, from DLSS's size or its evaluation; the window then draws as before.
    public string? Error { get; private set; }

    public bool Ready => output is not null && Error is null;

    // Made again for the window's size or a new mode; false when DLSS gives no size for it.
    public bool Resize(int width, int height)
    {
        if (sizedFor == (width, height, Mode) && output is not null)
        {
            return true;
        }

        device.WaitIdle();
        DestroyTargets();
        sizedFor = (width, height, Mode);
        if (Streamline.SuperResolutionSize(Mode, width, height) is not { } size)
        {
            Error = $"no render size for DLSS {Streamline.Modes[Mode]} at {width}x{height}";
            Console.WriteLine($"SCENE super resolution: {Error}; drawn without it");
            return false;
        }

        Error = null;
        failing = 0;
        (Width, Height, OutputWidth, OutputHeight) = (size.Width, size.Height, width, height);
        colour = new Picture(device, physical, colourFormat, Width, Height, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        depth = new Picture(device, physical, depthFormat, Width, Height, VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.DepthBit);
        motion = new Picture(device, physical, MotionFormat, Width, Height, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        output = new Picture(device, physical, OutputFormat, width, height, VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        var views = stackalloc VkImageView[] { colour.View, depth.View };
        framebuffer = device.CreateFramebuffer(new VkFramebufferCreateInfo { RenderPass = Pass, AttachmentCount = 2, Attachments = views, Width = Width, Height = Height, Layers = 1 }, null);
        var motionView = motion.View;
        motionFramebuffer = device.CreateFramebuffer(new VkFramebufferCreateInfo { RenderPass = motionPass, AttachmentCount = 1, Attachments = &motionView, Width = Width, Height = Height, Layers = 1 }, null);
        var picture = new VkDescriptorImageInfo { Sampler = sampler, ImageView = output.View, ImageLayout = VkImageLayout.General };
        device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = composite, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &picture }], []);
        fresh = true;
        Console.WriteLine($"SCENE super resolution: drawn at {Width}x{Height}, DLSS ({Streamline.Modes[Mode]}) to {width}x{height}");
        return true;
    }

    // Starts again from nothing kept, as after a cut.
    public void Forget() => fresh = true;

    // Before the pass: this frame's jitter in pixels, and the matrix that moves the drawn picture by it in clip space
    // (a row vector times viewProj times it). The jitter as sl::Constants takes it is the same offset.
    public (SN.Vector2 Pixels, SN.Matrix4x4 Clip) Jitter(float sign)
    {
        var scale = OutputWidth / (float)Width;
        var phases = Math.Max(8, (int)MathF.Ceiling(8f * scale * scale));
        var index = (int)(frame % phases) + 1;
        var pixels = new SN.Vector2(0.5f - Halton(index, 2), 0.5f - Halton(index, 3)) * sign;
        var clip = SN.Matrix4x4.Identity;
        clip.M41 = 2f * pixels.X / Width;
        clip.M42 = 2f * pixels.Y / Height;
        return (pixels, clip);
    }

    // After the pass: DLSS upscales the picture drawn into the output, which the composite then reads.
    public void Evaluate(CommandBuffer cb, SN.Matrix4x4 view, SN.Matrix4x4 projection, SN.Vector3 eye, float fovDegrees, SN.Vector2 jitter)
    {
        if (output is null || colour is null || depth is null || motion is null)
        {
            return;
        }

        var viewProj = view * projection;
        var reset = fresh;
        if (fresh)
        {
            // The motion vectors cleared to nothing, once; the output from nothing to General, where it stays.
            var zero = new VkClearValue { Color = new VkClearColorValue { Float32 = default } };
            cb.BeginRenderPass(new VkRenderPassBeginInfo
            {
                RenderPass = motionPass,
                Framebuffer = motionFramebuffer,
                RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = Width, Height = Height } },
                ClearValueCount = 1,
                ClearValues = &zero,
            }, VkSubpassContents.Inline);
            cb.EndRenderPass();
            cb.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.AllCommandsBit, VkDependencyFlags.None, [], [],
                [Stage.Barrier(output.Image, VkImageLayout.Undefined, VkImageLayout.General, VkAccessFlags.None, VkAccessFlags.ShaderWriteBit)]);
            previousViewProj = viewProj;
            fresh = false;
        }

        // The pass's colour and depth written, and last frame's composite done reading the output.
        var written = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit | VkAccessFlags.ShaderReadBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
        };
        cb.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit,
            VkPipelineStageFlags.AllCommandsBit, VkDependencyFlags.None, [written], [], []);

        SN.Matrix4x4.Invert(viewProj, out var inverse);
        SN.Matrix4x4.Invert(projection, out var toView);
        SN.Matrix4x4.Invert(previousViewProj, out var fromPrior);
        SN.Matrix4x4.Invert(view, out var toWorld);
        var constants = new Streamline.Constants
        {
            CameraViewToClip = projection,
            ClipToCameraView = toView,
            ClipToLensClip = SN.Matrix4x4.Identity,
            ClipToPrevClip = inverse * previousViewProj,
            PrevClipToClip = fromPrior * viewProj,
            JitterOffset = jitter,
            MvecScale = new(1f / Width, 1f / Height),
            CameraPos = eye,
            CameraUp = new(toWorld.M21, toWorld.M22, toWorld.M23),
            CameraRight = new(toWorld.M11, toWorld.M12, toWorld.M13),
            CameraFwd = -new SN.Vector3(toWorld.M31, toWorld.M32, toWorld.M33),
            CameraNear = projection.M43 / projection.M33,
            CameraFar = projection.M43 / (projection.M33 + 1f),
            CameraFov = fovDegrees * MathF.PI / 180f,
            CameraAspectRatio = OutputWidth / (float)OutputHeight,
            MotionVectorsInvalidValue = float.MaxValue,
            CameraMotionIncluded = 0, // Streamline adds the camera's motion to the zeros
            Reset = (byte)(reset ? 1 : 0),
            MinRelativeLinearDepthObjectSeparation = 40f,
        };
        previousViewProj = viewProj;
        var options = new Streamline.DLSSOptions
        {
            Mode = Mode,
            OutputWidth = (uint)OutputWidth,
            OutputHeight = (uint)OutputHeight,
            PreExposure = 1f,
            ExposureScale = 1f,
            ColorBuffersHDR = 0, // the window's colours, shaded and tone mapped already
        };
        var resources = stackalloc Streamline.Resource[4];
        Picture[] tagged = [colour, output, depth, motion];
        uint[] types = [Streamline.BufferScalingInputColor, Streamline.BufferScalingOutputColor, Streamline.BufferDepth, Streamline.BufferMotionVectors];
        var tags = new Streamline.ResourceTag[tagged.Length];
        for (var i = 0; i < tagged.Length; i++)
        {
            resources[i] = tagged[i].Resource();
            tags[i] = Streamline.Tag(&resources[i], types[i], tagged[i].Width, tagged[i].Height);
        }

        // A frame skipped or failed shows the last picture upscaled; only failures that last turn DLSS off.
        var failed = Streamline.EvaluateSuperResolution(cb.VkHandle, frame, ref constants, ref options, tags);
        if (failed == Streamline.Skipped)
        {
            Skips++;
        }
        else if (failed is not null)
        {
            if (failing++ == 0)
            {
                Console.WriteLine($"SCENE super resolution failed: {failed}; the last picture kept");
            }

            if (failing >= GivesUp)
            {
                Console.WriteLine($"SCENE super resolution failed {failing} frames in a row: {failed}; drawn without it");
                Error = failed;
            }
        }
        else
        {
            failing = 0;
            Evaluations++;
        }

        frame++;
        var upscaled = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cb.PipelineBarrier(VkPipelineStageFlags.AllCommandsBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [upscaled], [], []);
    }

    // In the cards' pass, before the cards: the upscaled picture over the whole window, through the splash's pipeline.
    public void Composite(CommandBuffer cb, VkPipeline splashPipeline, VkPipelineLayout splashLayout)
    {
        cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
        cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [composite], []);
        var push = new SplashPush(new SN.Vector4(-1f, -1f, 1f, 1f), SN.Vector4.One);
        cb.PushConstants(splashLayout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
        cb.Draw(6, 1, 0, 0);
    }

    public string Measured() => Width == 0
        ? $"DLSS Super Resolution off ({Error ?? "not sized"})"
        : $"DLSS Super Resolution ({Streamline.Modes[Mode]}) drawn at {Width}x{Height} to {OutputWidth}x{OutputHeight}, {Evaluations} evaluations, {Skips} skipped{(Error is { } e ? $", last error: {e}" : "")}";

    public void Dispose()
    {
        DestroyTargets();
        device.DestroyDescriptorPool(pool, null);
        device.DestroySampler(sampler, null);
        device.DestroyRenderPass(motionPass, null);
        device.DestroyRenderPass(Pass, null);
    }

    private void DestroyTargets()
    {
        if (output is not null)
        {
            device.DestroyFramebuffer(framebuffer, null);
            device.DestroyFramebuffer(motionFramebuffer, null);
        }

        colour?.Dispose();
        depth?.Dispose();
        motion?.Dispose();
        output?.Dispose();
        (colour, depth, motion, output) = (null, null, null, null);
    }

    private static VkAttachmentDescription Attachment(VkFormat format, VkAttachmentStoreOp store, VkImageLayout final) => new()
    {
        Format = format,
        Samples = VkSampleCountFlags._1Bit,
        LoadOp = VkAttachmentLoadOp.Clear,
        StoreOp = store,
        StencilLoadOp = VkAttachmentLoadOp.DontCare,
        StencilStoreOp = VkAttachmentStoreOp.DontCare,
        InitialLayout = VkImageLayout.Undefined,
        FinalLayout = final,
    };

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

    // An image of the drawn or upscaled picture, its memory and view, as Streamline takes it (General layout).
    private sealed class Picture : IDisposable
    {
        private readonly Device device;
        private readonly VkDeviceMemory memory;
        private readonly VkImageUsageFlags usage;

        public Picture(Device device, PhysicalDevice physical, VkFormat format, int width, int height, VkImageUsageFlags usage, VkImageAspectFlags aspect)
        {
            this.device = device;
            this.usage = usage;
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
                Usage = usage,
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
                SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
            }, null);
        }

        public VkImage Image { get; }

        public VkImageView View { get; }

        public VkFormat Format { get; }

        public int Width { get; }

        public int Height { get; }

        public Streamline.Resource Resource() =>
            Streamline.Image(Image.VkHandle, memory.VkHandle, View.VkHandle, Width, Height, (uint)Format, (uint)usage);

        public void Dispose()
        {
            device.DestroyImageView(View, null);
            device.DestroyImage(Image, null);
            device.FreeMemory(memory, null);
        }
    }
}
