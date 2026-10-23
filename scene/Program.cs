// Brickyard P0 (GuitarAlchemist/gaia#260): the robot you talk to, standing in a brick hall, drawn with Vulkan through
// BRUTAL's bindings. The brain (../brain) tells it what it is doing, one JSON line per change on a local TCP port:
// {"state":"idle"}, "listening", "thinking", or {"state":"speaking","seconds":S,"envelope":[...]} with the voice's
// loudness every 20 ms, which opens the mouth as the sentence plays.
//
//	scene [--listen 8805] [--seconds N] [--demo] [--shots DIR] [--shelf SECTION] [--hidden] [--immediate] [--cap N] | --selftest
//
// --demo plays idle, listening, thinking, speaking, a compaction, each expression, the caption of a sentence let pass
// and the pairing card in a loop without the brain, while the context gauge fills, and --shots DIR saves a PNG of each. The scene
// waits for vsync unless --immediate; --cap N limits it to N frames a second, 60 by default when hidden (nothing paces
// a hidden window), so that it never takes the GPU from a live view beside it. Esc closes it, as does --seconds.
//
// It opens on BRUTAL's logo and copyright notice for 2.6 s, as BRUTAL's licence asks (Splash.cs); --demo's states and
// --shots' times start after it, and --shots also saves the splash.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

return Stage.Run(args);

[StructLayout(LayoutKind.Sequential)]
internal struct Vertex(SN.Vector3 pos, SN.Vector3 normal, SN.Vector3 color)
{
    public SN.Vector3 Pos = pos;
    public SN.Vector3 Normal = normal;
    public SN.Vector3 Color = color;
}

// What each draw pushes, 128 bytes: the part's clip transform, the three rows of its world transform (for the light)
// and its glow, a colour and a strength.
[StructLayout(LayoutKind.Sequential)]
internal struct Push(SN.Matrix4x4 world, SN.Matrix4x4 viewProj, SN.Vector4 glow)
{
    public SN.Matrix4x4 Mvp = world * viewProj;
    public SN.Vector4 Row0 = new(world.M11, world.M21, world.M31, world.M41);
    public SN.Vector4 Row1 = new(world.M12, world.M22, world.M32, world.M42);
    public SN.Vector4 Row2 = new(world.M13, world.M23, world.M33, world.M43);
    public SN.Vector4 Glow = glow;
}

// What each splash draw pushes: its rectangle in clip space (left, top, right, bottom) and a tint whose alpha fades it.
[StructLayout(LayoutKind.Sequential)]
internal struct SplashPush(SN.Vector4 rect, SN.Vector4 tint)
{
    public SN.Vector4 Rect = rect;
    public SN.Vector4 Tint = tint;
}

internal static unsafe class Stage
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int FramesInFlight = 2;
    private const int ApiVersion13 = (1 << 22) | (3 << 12);
    private const VkShaderStageFlags PushStages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private const string VertexShader = """
        #version 450
        layout(location = 0) in vec3 inPos;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec3 inColor;
        layout(push_constant) uniform Push { mat4 mvp; vec4 row0; vec4 row1; vec4 row2; vec4 glow; } pc;
        layout(location = 0) out vec3 vNormal;
        layout(location = 1) out vec3 vColor;
        layout(location = 2) out vec3 vWorld;
        void main()
        {
            vec4 p = vec4(inPos, 1.0);
            gl_Position = pc.mvp * p;
            vWorld = vec3(dot(pc.row0, p), dot(pc.row1, p), dot(pc.row2, p));
            vNormal = vec3(dot(pc.row0.xyz, inNormal), dot(pc.row1.xyz, inNormal), dot(pc.row2.xyz, inNormal));
            vColor = inColor;
        }
        """;

    private const string FragmentShader = """
        #version 450
        layout(location = 0) in vec3 vNormal;
        layout(location = 1) in vec3 vColor;
        layout(location = 2) in vec3 vWorld;
        layout(push_constant) uniform Push { mat4 mvp; vec4 row0; vec4 row1; vec4 row2; vec4 glow; } pc;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec3 n = normalize(vNormal);
            // A warm key light high on the left, a cold fill from the right, the floor's bounce from below.
            float key = max(dot(n, normalize(vec3(-0.45, 0.8, 0.55))), 0.0);
            float fill = max(dot(n, normalize(vec3(0.7, 0.25, 0.3))), 0.0);
            float bounce = 0.5 - 0.5 * n.y;
            vec3 light = vec3(1.0, 0.86, 0.68) * key + vec3(0.35, 0.45, 0.6) * 0.35 * fill
                + vec3(0.12, 0.1, 0.09) * bounce + 0.03;
            // A pool of light around the robot; the hall falls into the dark beyond it and above it.
            float pool = 1.0 - smoothstep(0.8, 6.0, length(vWorld.xz - vec2(0.0, -0.3)));
            float high = 1.0 - 0.6 * smoothstep(1.6, 4.5, vWorld.y);
            outColor = vec4(vColor * light * (0.1 + 0.9 * pool) * high + pc.glow.rgb * pc.glow.a, 1.0);
        }
        """;

    // The splash: a textured rectangle with no vertex buffer, its six corners picked by the vertex's index.
    private const string SplashVertexShader = """
        #version 450
        layout(push_constant) uniform Push { vec4 rect; vec4 tint; } pc;
        layout(location = 0) out vec2 vUv;
        const vec2 corners[6] = vec2[](vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));
        void main()
        {
            vUv = corners[gl_VertexIndex];
            gl_Position = vec4(mix(pc.rect.xy, pc.rect.zw, vUv), 0.0, 1.0);
        }
        """;

    private const string SplashFragmentShader = """
        #version 450
        layout(set = 0, binding = 0) uniform sampler2D image;
        layout(push_constant) uniform Push { vec4 rect; vec4 tint; } pc;
        layout(location = 0) in vec2 vUv;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec4 texel = texture(image, vUv);
            outColor = vec4(texel.rgb * pc.tint.rgb, texel.a * pc.tint.a);
        }
        """;

    public static int Run(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            return Link.SelfTest();
        }

        var clock = Stopwatch.StartNew();
        var limit = double.TryParse(Option(args, "--seconds"), out var seconds) ? seconds : double.PositiveInfinity;
        var demo = args.Contains("--demo");
        var hidden = args.Contains("--hidden");
        var vsync = !args.Contains("--immediate");
        var cap = int.TryParse(Option(args, "--cap"), out var capValue) ? capValue : hidden ? 60 : 0;
        var port = int.TryParse(Option(args, "--listen"), out var portValue) ? portValue : 8805;
        var shotsDir = Option(args, "--shots");
        var shelfAtStart = Option(args, "--shelf");
        Words.Speak(Option(args, "--lang") ?? "en");
        const double S = Splash.Seconds;
        (double At, string Name)[] shots = shotsDir is null ? [] : [(1.3, "splash"), (S + 1.0, "idle"), (S + 2.6, "listening"), (S + 4.2, "thinking"), (S + 6.2, "speaking"), (S + 8.3, "full"), (S + 10.0, "compacting"), .. Link.Moods.Skip(1).Select((m, i) => (S + 11.8 + i, m)), (S + 19.6, "ignored"), (S + 20.5, "pairing")];
        if (shots.Length > 0 && !demo)
        {
            Console.Error.WriteLine("SCENE refused: --shots needs --demo, whose states come at known times");
            return 2;
        }

        var logoPath = Path.Combine(AppContext.BaseDirectory, "brand", Splash.LogoFile);
        if (!File.Exists(logoPath))
        {
            Console.Error.WriteLine($"SCENE FAIL: {logoPath} is missing; BRUTAL's licence asks for its logo at start-up");
            return 1;
        }

        Console.WriteLine($"SCENE {Splash.Notice}");

        var link = new Link(() => clock.Elapsed.TotalSeconds);
        if (!demo)
        {
            var error = link.Listen(port);
            if (error is null)
            {
                Closed(port, false); // a window the brain started again, or that the user asked for
            }

            if (error is not null)
            {
                Console.Error.WriteLine($"SCENE FAIL: {error}");
                return 1;
            }

            Console.WriteLine($"SCENE listening on 127.0.0.1:{port}");
        }

        if (!Glfw.Init())
        {
            Console.Error.WriteLine("SCENE FAIL: Glfw.Init returned false");
            return 1;
        }

        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        Glfw.WindowHint(GlfwWindowHint.Resizable, 1);
        if (hidden)
        {
            Glfw.WindowHint(GlfwWindowHint.Visible, 0);
        }

        var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Brickyard", Size = new int2(Width, Height) });

        // Instance with the extensions GLFW needs to present, and the window's surface.
        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var extensionCount);
        var extensions = new HashSet<string>();
        for (var i = 0; i < extensionCount; i++)
        {
            extensions.Add(Marshal.PtrToStringUTF8((nint)glfwExtensions[i])!);
        }

        var instance = Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = ApiVersion13 },
            EnabledExtensions = extensions,
            EnabledLayers = [],
        });
        if (Glfw.CreateWindowSurface(instance.VkHandle, window, out var rawSurface) != 0)
        {
            Console.Error.WriteLine("SCENE FAIL: no window surface");
            return 1;
        }

        var surface = new VkSurfaceKHR((nint)rawSurface);

        // The first discrete GPU with a queue family that draws and presents (any GPU if none is discrete).
        PhysicalDevice? physical = null;
        var family = -1;
        foreach (var pd in instance.PhysicalDevices.OrderBy(p => p.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu ? 0 : 1))
        {
            var count = pd.GetQueueFamilyProperties_Count();
            var families = new VkQueueFamilyProperties[count];
            pd.GetQueueFamilyProperties_Write(families);
            for (var i = 0; i < count && family < 0; i++)
            {
                if (families[i].QueueFlags.HasFlag(VkQueueFlags.GraphicsBit) && pd.GetSurfaceSupportKHR(i, surface))
                {
                    family = i;
                }
            }

            if (family >= 0)
            {
                physical = pd;
                break;
            }
        }

        if (physical is null)
        {
            Console.Error.WriteLine("SCENE FAIL: no GPU can draw and present to this window");
            return 1;
        }

        var priority = 1.0f;
        var queueInfo = new VkDeviceQueueCreateInfo { QueueFamilyIndex = family, QueueCount = 1, QueuePriorities = &priority };
        var device = physical.CreateDevice(new Device.CreateInfo
        {
            Extensions = ["VK_KHR_swapchain"],
            QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&queueInfo, 1),
        });
        var queue = device.GetQueue(family, 0);

        // Swapchain: sRGB BGRA if offered; vsync unless --immediate.
        var formatCount = physical.GetSurfaceFormatsKHR_Count(surface);
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[formatCount];
        physical.GetSurfaceFormatsKHR_Write(surface, formats);
        var surfaceFormat = formats[0];
        foreach (var f in formats)
        {
            if (f.Format == VkFormat.B8G8R8A8SRGB && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
            {
                surfaceFormat = f;
            }
        }

        var modeCount = physical.GetSurfacePresentModesKHR_Count(surface);
        Span<VkPresentModeKHR> modes = stackalloc VkPresentModeKHR[modeCount];
        physical.GetSurfacePresentModesKHR_Write(surface, modes);
        var presentMode = VkPresentModeKHR.FifoKHR;
        if (!vsync)
        {
            foreach (var wanted in new[] { VkPresentModeKHR.ImmediateKHR, VkPresentModeKHR.MailboxKHR })
            {
                if (presentMode == VkPresentModeKHR.FifoKHR && modes.Contains(wanted))
                {
                    presentMode = wanted;
                }
            }
        }

        // The swapchain and what is as large as the window, built by Build and rebuilt when the window changes size.
        const VkFormat depthFormat = VkFormat.D32SFloat;
        var extent = default(VkExtent2D);
        var swapchain = default(VkSwapchainKHR);
        var hasSwapchain = false;
        var images = Array.Empty<VkImage>();
        var depthImage = default(VkImage);
        var depthMemory = default(VkDeviceMemory);
        var depthView = default(VkImageView);
        var views = Array.Empty<VkImageView>();
        var framebuffers = Array.Empty<VkFramebuffer>();
        var renderDone = Array.Empty<VkSemaphore>();

        // One subpass: colour to present, depth cleared and discarded.
        Span<VkAttachmentDescription> attachments =
        [
            new()
            {
                Format = surfaceFormat.Format,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.Store,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.Undefined,
                FinalLayout = VkImageLayout.PresentSrcKHR,
            },
            new()
            {
                Format = depthFormat,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.DontCare,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.Undefined,
                FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            },
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
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.LateFragmentTestsBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
        };
        var renderPass = device.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = attachments.Length,
            Attachments = (VkAttachmentDescription*)Unsafe.AsPointer(ref attachments[0]),
            SubpassCount = 1,
            Subpasses = &subpass,
            DependencyCount = 1,
            Dependencies = &dependency,
        }, null);

        // Builds the swapchain at the window's size, with its depth buffer, image views, framebuffers and semaphores. It
        // is false while the window has no size, when it is minimised.
        bool Build()
        {
            var caps = physical.GetSurfaceCapabilitiesKHR(surface);
            var size = caps.CurrentExtent.Width == -1 ? new VkExtent2D { Width = window.FramebufferSize.X, Height = window.FramebufferSize.Y } : caps.CurrentExtent;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return false;
            }

            extent = size;
            var imageCount = Math.Max(caps.MinImageCount, 3);
            if (caps.MaxImageCount != 0)
            {
                imageCount = Math.Min(imageCount, caps.MaxImageCount);
            }

            var old = swapchain;
            swapchain = device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR
            {
                Surface = surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit | (shots.Length == 0 ? default : VkImageUsageFlags.TransferSrcBit),
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = caps.CurrentTransform,
                CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
                PresentMode = presentMode,
                Clipped = true,
                OldSwapchain = old,
            }, null);
            if (hasSwapchain)
            {
                device.DestroySwapchainKHR(old, null);
            }

            hasSwapchain = true;
            images = new VkImage[device.GetSwapchainImagesKHR_Count(swapchain)];
            device.GetSwapchainImagesKHR_Write(swapchain, images);

            depthImage = device.CreateImage(new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = depthFormat,
                Extent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.DepthStencilAttachmentBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            }, null);
            depthMemory = device.AllocateMemory(physical, depthImage, VkMemoryPropertyFlags.DeviceLocalBit, null);
            device.BindImageMemory(depthImage, depthMemory, ByteSize.Zero);
            depthView = device.CreateImageView(View(depthImage, depthFormat, VkImageAspectFlags.DepthBit), null);

            views = new VkImageView[images.Length];
            framebuffers = new VkFramebuffer[images.Length];
            renderDone = new VkSemaphore[images.Length];
            var pair = stackalloc VkImageView[] { default, depthView };
            for (var i = 0; i < images.Length; i++)
            {
                views[i] = device.CreateImageView(View(images[i], surfaceFormat.Format, VkImageAspectFlags.ColorBit), null);
                pair[0] = views[i];
                framebuffers[i] = device.CreateFramebuffer(new VkFramebufferCreateInfo
                {
                    RenderPass = renderPass,
                    AttachmentCount = 2,
                    Attachments = pair,
                    Width = extent.Width,
                    Height = extent.Height,
                    Layers = 1,
                }, null);
                renderDone[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            }

            return true;
        }

        // Destroys what Build made, but the swapchain, which the next one replaces.
        void Teardown()
        {
            for (var i = 0; i < images.Length; i++)
            {
                device.DestroySemaphore(renderDone[i], null);
                device.DestroyFramebuffer(framebuffers[i], null);
                device.DestroyImageView(views[i], null);
            }

            device.DestroyImageView(depthView, null);
            device.DestroyImage(depthImage, null);
            device.FreeMemory(depthMemory, null);
            images = [];
        }

        if (!Build())
        {
            Console.Error.WriteLine("SCENE FAIL: the window has no size");
            return 1;
        }

        // Shaders, compiled from GLSL at start-up.
        var compileClock = Stopwatch.StartNew();
        var vertexModule = Shader(device, VertexShader, ShaderKind.VertexShader, "scene.vert");
        var fragmentModule = Shader(device, FragmentShader, ShaderKind.FragmentShader, "scene.frag");
        Console.WriteLine($"SCENE shaders compiled in {compileClock.Elapsed.TotalMilliseconds:F0} ms");

        var pushRange = new VkPushConstantRange { StageFlags = PushStages, Offset = ByteSize.Zero, Size = ByteSize.Of<Push>() };
        var layout = device.CreatePipelineLayout([], [pushRange], null);
        var pipeline = Pipeline(device, layout, renderPass, vertexModule, fragmentModule);

        // The hall, still, and the robot's parts, each moved by its own matrix.
        var hallVertices = Brickwork.Build();
        var hall = new Mesh(device, physical, hallVertices);
        var parts = Robot.Meshes().Select(v => new Mesh(device, physical, v)).ToArray();
        Console.WriteLine($"SCENE geometry: hall {hallVertices.Length / 36} boxes, robot {parts.Sum(p => p.Count) / 36} boxes in {parts.Length} parts");

        // --shots: a frame's pixels, copied from the swapchain image into a host-visible buffer.
        var shotExtent = extent;
        var readbackSize = new ByteSize((nuint)(extent.Width * extent.Height * 4));
        var readback = default(VkBuffer);
        var readbackMemory = default(VkDeviceMemory);
        var readbackMapped = nint.Zero;
        if (shots.Length > 0)
        {
            Directory.CreateDirectory(shotsDir!);
            readback = device.CreateBuffer(new VkBufferCreateInfo { Size = readbackSize, Usage = VkBufferUsageFlags.TransferDstBit, SharingMode = VkSharingMode.Exclusive }, null);
            readbackMemory = device.AllocateMemory(physical, readback, VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, null);
            device.BindBufferMemory(readback, readbackMemory, memoryOffset: ByteSize.Zero);
            readbackMapped = (nint)(byte*)device.MapMemory<byte>(new MapInfo(readbackMemory) { Region = new MapRegion { Size = readbackSize } });
        }

        // Per frame in flight: a command pool and buffer, a fence and an image-acquired semaphore.
        var pools = new VkCommandPool[FramesInFlight];
        var commands = new CommandBuffer[FramesInFlight];
        var fences = new VkFence[FramesInFlight];
        var acquired = new VkSemaphore[FramesInFlight];
        for (var i = 0; i < FramesInFlight; i++)
        {
            pools[i] = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
            commands[i] = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = pools[i], Level = VkCommandBufferLevel.Primary });
            fences[i] = device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null);
            acquired[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }

        // The splash: the logo at half the window's width, its notice beneath, both at one texel a pixel.
        var splashVertex = Shader(device, SplashVertexShader, ShaderKind.VertexShader, "splash.vert");
        var splashFragment = Shader(device, SplashFragmentShader, ShaderKind.FragmentShader, "splash.frag");
        var sampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Nearest,
            MinFilter = VkFilter.Nearest,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1f,
        }, null);
        var setLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
        {
            Bindings = [new VkDescriptorSetLayoutBinding { DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit }],
        }, null);
        var descriptorPool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
        {
            MaxSets = 9, // the logo, the notice, the shelf's card, the caption, the phone icon, its pairing card, the ring's card and its dot, the language button
            PoolSizes = [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 9 }], // one image each
        }, null);
        var splashRange = new VkPushConstantRange { StageFlags = PushStages, Offset = ByteSize.Zero, Size = ByteSize.Of<SplashPush>() };
        var splashLayout = device.CreatePipelineLayout([setLayout], [splashRange], null);
        var splashPipeline = Pipeline(device, splashLayout, renderPass, splashVertex, splashFragment, overlay: true);

        var logoPixels = Png.Load(logoPath, out var logoSourceWidth, out var logoSourceHeight);
        var logoWidth = extent.Width / 2;
        var logoHeight = (int)Math.Round(logoWidth * (double)logoSourceHeight / logoSourceWidth);
        logoPixels = Splash.Shrink(logoPixels, logoSourceWidth, logoSourceHeight, logoWidth, logoHeight);
        var noticePixels = Splash.Text(Splash.Notice, Math.Max(14, extent.Height / 40), out var noticeWidth, out var noticeHeight);
        var upload = commands[0];
        upload.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
        Texture[] splashTextures = noticePixels is null
            ? [new Texture(device, physical, upload, logoPixels, logoWidth, logoHeight)]
            : [new Texture(device, physical, upload, logoPixels, logoWidth, logoHeight), new Texture(device, physical, upload, noticePixels, noticeWidth, noticeHeight)];
        upload.End();
        var uploadHandle = upload.Handle;
        queue.Submit([new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &uploadHandle }], default);
        device.WaitIdle();
        var splashSets = new VkDescriptorSet[splashTextures.Length];
        for (var i = 0; i < splashTextures.Length; i++)
        {
            splashTextures[i].FreeStaging();
            splashSets[i] = device.AllocateDescriptorSet(descriptorPool, setLayout);
            var imageInfo = new VkDescriptorImageInfo { Sampler = sampler, ImageView = splashTextures[i].View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = splashSets[i], DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &imageInfo }], []);
        }

        // Each rectangle in whole pixels, so that a texel lands on a pixel; the notice 36 px under the logo.
        var logoTop = ((extent.Height - logoHeight) / 2) - 24;
        SN.Vector4 Rect(int left, int top, int width, int height) => new(
            (2f * left / extent.Width) - 1f, (2f * top / extent.Height) - 1f, (2f * (left + width) / extent.Width) - 1f, (2f * (top + height) / extent.Height) - 1f);
        SN.Vector4[] splashRects =
        [
            Rect((extent.Width - logoWidth) / 2, logoTop, logoWidth, logoHeight),
            Rect((extent.Width - noticeWidth) / 2, logoTop + logoHeight + 36, noticeWidth, noticeHeight),
        ];
        SN.Vector3[] splashTints = [SN.Vector3.One, new(0.62f)];
        Console.WriteLine($"SCENE splash: BRUTAL logo {logoWidth}x{logoHeight} px from {logoSourceWidth}x{logoSourceHeight}, notice {(noticePixels is null ? "on the console only" : $"{noticeWidth}x{noticeHeight} px")}");

        var placement = new Placement();
        link.Window = placement.Ask;
        var tray = OperatingSystem.IsWindows() ? new Tray() : null;
        if (tray is not null)
        {
            tray.Clicked += () => placement.Ask("show");
        }

        var stale = false; // the swapchain no longer matches the window
        window.OnFramebufferSize += (_, _) => stale = true;

        // The shelf (Shelf.cs): Tab or M opens it; the arrows, Enter, Backspace and the mouse move through it; Esc
        // closes it, and the window when it is closed. Its card is drawn again when what it shows changes.
        var shelf = new Shelf();
        var cardSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        var uploadPool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
        var uploader = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = uploadPool, Level = VkCommandBufferLevel.Primary });
        Texture? cardTexture = null;
        Panel.Card? card = null;
        var cardVersion = -1;
        var cardHeight = 0;
        int CardMargin() => Math.Max(12, extent.Height / 48);

        // The caption under the robot (Panel.Caption) for each sentence Delta heard, drawn again for each and for a new size.
        var captionSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? captionTexture = null;
        Panel.Card? caption = null;
        var captionId = 0;
        var captionSize = (0, 0);

        // The phone icon in the top right corner (Panel.Phone), lit while a pairing code lives, and the card a click on it
        // or P opens and closes (Panel.Pairing). The code is a credential while it lives, so it shows only when asked, as
        // the window may be shared.
        var phoneSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? phoneTexture = null;
        Panel.Card? phone = null;
        var phoneDrawn = (Live: false, Height: 0);
        var pairingSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? pairingTexture = null;
        Panel.Card? pairingCard = null;
        var pairingDrawn = (Id: -1, Left: -1, Width: 0, Height: 0, Language: "");
        var pairingShown = false;
        (int X, int Y) PhoneAt() => (extent.Width - Panel.PhoneSize(extent.Height) - CardMargin(), CardMargin());
        // The language button left of the phone icon (Panel.LanguageButton): a click, or L, picks the next setting, which
        // the brain applies and echoes back; with no brain, the window takes it at once.
        var languageSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? languageTexture = null;
        Panel.Card? languageButton = null;
        var languageDrawn = (Setting: "", Height: 0);
        (int X, int Y) LanguageAt() => (PhoneAt().X - (languageButton?.Width ?? 0) - Math.Max(6, extent.Height / 96), CardMargin());
        void NextLanguage()
        {
            var next = Words.Next(Words.Setting);
            if (!link.Tell($$"""{"language":"{{next}}"}"""))
            {
                link.Apply(new Link.Change(null, [], "", null, null, Language: next), clock.Elapsed.TotalSeconds);
            }
        }
        // The ring in the bottom left corner while Delta listens without its name: its card (Panel.Lease), drawn once for
        // the window's size, and one dot (Panel.Dot) drawn LeaseDots times over it, tinted lit or dim as it empties.
        var leaseSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? leaseTexture = null;
        Panel.Card? leaseCard = null;
        var leaseDotSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? leaseDotTexture = null;
        Panel.Card? leaseDot = null;
        var leaseDrawn = (Height: 0, Language: "");
        (int X, int Y) PairingAt() => pairingCard is null ? (0, 0) : ((extent.Width - pairingCard.Width) / 2, (extent.Height - pairingCard.Height) / 2);

        // Uploads a drawn card into a new texture that set samples, once no frame in flight reads the old one.
        Texture Upload(Panel.Card drawn, VkDescriptorSet set, Texture? old)
        {
            device.WaitIdle();
            device.ResetCommandPool(uploadPool, VkCommandPoolResetFlags.None);
            uploader.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
            var fresh = new Texture(device, physical, uploader, drawn.Rgba, drawn.Width, drawn.Height);
            uploader.End();
            var uploaderHandle = uploader.Handle;
            queue.Submit([new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &uploaderHandle }], default);
            device.WaitIdle();
            fresh.FreeStaging();
            old?.Dispose();
            var image = new VkDescriptorImageInfo { Sampler = sampler, ImageView = fresh.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = set, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image }], []);
            return fresh;
        }
        link.Shelf = command =>
        {
            var at = clock.Elapsed.TotalSeconds;
            switch (command)
            {
                case "open":
                    shelf.Show(at);
                    break;
                case "sessions" or "conversations" or "plugins" or "marketplaces":
                    shelf.Show(at, command);
                    break;
                case "close":
                    shelf.Hide();
                    break;
                case "up":
                    shelf.Move(at, -1);
                    break;
                case "down":
                    shelf.Move(at, 1);
                    break;
                case "enter":
                    shelf.Enter(at);
                    break;
                case "back":
                    shelf.Back(at);
                    break;
            }
        };

        if (shelfAtStart is not null)
        {
            shelf.Show(0, shelfAtStart);
        }

        var reason = "window closed";
        window.OnKey += (w, key, scancode, action, mods) =>
        {
            if (action == GlfwKeyAction.Release)
            {
                return;
            }

            var at = clock.Elapsed.TotalSeconds;
            if (shelf.Open)
            {
                switch (key)
                {
                    case GlfwKey.Escape or GlfwKey.Tab:
                        shelf.Hide();
                        break;
                    case GlfwKey.Up:
                        shelf.Move(at, -1);
                        break;
                    case GlfwKey.Down:
                        shelf.Move(at, 1);
                        break;
                    case GlfwKey.Enter or GlfwKey.Right:
                        shelf.Enter(at);
                        break;
                    case GlfwKey.Backspace or GlfwKey.Left:
                        shelf.Back(at);
                        break;
                }

                return;
            }

            if (key == GlfwKey.P)
            {
                pairingShown = !pairingShown;
            }
            else if (key == GlfwKey.L)
            {
                NextLanguage();
            }
            else if (key is GlfwKey.Tab or GlfwKey.M)
            {
                shelf.Show(at);
            }
            else if (key == GlfwKey.Escape && pairingShown)
            {
                pairingShown = false;
            }
            else if (key == GlfwKey.Escape)
            {
                reason = "Esc";
                w.ShouldClose = true;
            }
        };

        // A row under the cursor, in the card's pixels, from the window's coordinates.
        int RowAt(GlfwWindow w)
        {
            if (card is null || !shelf.Open)
            {
                return -1;
            }

            var x = w.CursorX * extent.Width / Math.Max(1, w.Width) - CardMargin();
            var y = w.CursorY * extent.Height / Math.Max(1, w.Height) - CardMargin();
            if (x < 0 || x >= card.Width)
            {
                return -1;
            }

            foreach (var (row, top, bottom) in card.Rows)
            {
                if (y >= top && y < bottom)
                {
                    return row;
                }
            }

            return -1;
        }

        // Whether the cursor is on a card drawn at corner, in the window's pixels.
        bool Over(GlfwWindow w, (int X, int Y) corner, int width, int height)
        {
            var x = w.CursorX * extent.Width / Math.Max(1, w.Width);
            var y = w.CursorY * extent.Height / Math.Max(1, w.Height);
            return x >= corner.X && x < corner.X + width && y >= corner.Y && y < corner.Y + height;
        }

        window.OnMouseButton += (w, button, action, mods) =>
        {
            var icon = Panel.PhoneSize(extent.Height);
            if (action == GlfwButtonAction.Press && (Over(w, PhoneAt(), icon, icon) || (pairingShown && pairingCard is not null && Over(w, PairingAt(), pairingCard.Width, pairingCard.Height))))
            {
                pairingShown = !pairingShown;
            }
            else if (action == GlfwButtonAction.Press && languageButton is not null && Over(w, LanguageAt(), languageButton.Width, languageButton.Height))
            {
                NextLanguage();
            }
            else if (action == GlfwButtonAction.Press && RowAt(w) is var row and >= 0)
            {
                var at = clock.Elapsed.TotalSeconds;
                shelf.Select(at, row);
                shelf.Enter(at);
            }
        };
        window.OnScroll += (w, offset) =>
        {
            if (shelf.Open && offset.Y != 0)
            {
                shelf.Move(clock.Elapsed.TotalSeconds, offset.Y > 0 ? -1 : 1);
            }
        };

        Console.WriteLine($"SCENE present mode {presentMode}, {images.Length} swapchain images, {extent.Width}x{extent.Height}{(cap > 0 ? $", capped at {cap} fps" : "")}");
        if (cap > 0 && OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1); // 1 ms sleeps, so the cap paces frames without spinning the whole time
        }

        var robot = new Robot();
        var worlds = new SN.Matrix4x4[Robot.Parts];
        var glows = new SN.Vector4[Robot.Parts];
        var period = cap > 0 ? 1.0 / cap : 0.0;
        var nextFrame = 0.0;
        var lastPresent = 0.0;
        var frameTimes = new List<double>(100_000);
        var frame = 0;
        var shot = 0;
        var last = clock.Elapsed.TotalSeconds;
        var firstFrame = -1.0;
        var clears = stackalloc VkClearValue[2];
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var loopStart = clock.Elapsed;
        while (!window.ShouldClose)
        {
            Glfw.PollEvents();
            placement.Apply(window, tray);
            var now = clock.Elapsed.TotalSeconds;
            if (now >= limit)
            {
                reason = "time limit";
                break;
            }

            var dt = (float)(now - last);
            last = now;
            // The splash is timed from the first frame on screen, not from start-up, which takes about a second; --demo's
            // timeline and --shots start with it, while the brain's lines are timed by the link's own clock.
            var shown = firstFrame < 0 ? 0.0 : now - firstFrame;
            var splashing = shown < S;
            var linkNow = demo ? Math.Max(0.0, shown - S) : now;
            if (demo)
            {
                link.Demo(linkNow);
            }

            var moment = link.Read(linkNow);
            if (demo)
            {
                pairingShown = linkNow % Link.DemoSeconds >= 20.0; // the demo opens the pairing card for its last second
            }
            var time = (float)now;
            shelf.Tick(now);
            robot.Presenting = shelf.Open;
            robot.Pose(time, dt, moment, worlds, glows);

            // The camera drifts a little from side to side in front of the robot, at the height of its face.
            var eye = new SN.Vector3(0.45f * MathF.Sin(time * 0.11f), 1.08f + (0.04f * MathF.Sin(time * 0.07f)), 2.55f);
            var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(0.75f, extent.Width / (float)extent.Height, 0.1f, 40f);
            projection.M22 = -projection.M22; // Vulkan's clip space has y pointing down
            var viewProj = SN.Matrix4x4.CreateLookAt(eye, new SN.Vector3(0f, 0.7f, 0f), SN.Vector3.UnitY) * projection;

            // A window hidden by the clock or minimised draws nothing; one whose size changed gets a new swapchain first.
            if (placement.Hidden || window.IsIconified)
            {
                Thread.Sleep(50);
                continue;
            }

            if (stale)
            {
                device.WaitIdle();
                Teardown();
                if (!Build())
                {
                    Thread.Sleep(50);
                    continue;
                }

                stale = false;
                Console.WriteLine($"SCENE swapchain rebuilt at {extent.Width}x{extent.Height}");
                continue; // the projection follows the new size
            }

            if (shelf.Open && !splashing && (shelf.Version != cardVersion || extent.Height != cardHeight))
            {
                cardVersion = shelf.Version;
                cardHeight = extent.Height;
                card = Panel.Draw(shelf.Levels, extent.Height);
                cardTexture = Upload(card, cardSet, cardTexture);
            }

            if (moment.HeardAge >= 0f && !splashing && (moment.HeardId != captionId || (extent.Width, extent.Height) != captionSize))
            {
                captionId = moment.HeardId;
                captionSize = (extent.Width, extent.Height);
                caption = Panel.Caption(moment.Heard, moment.HeardTo, extent.Width, extent.Height);
                captionTexture = Upload(caption, captionSet, captionTexture);
            }

            if (!splashing && phoneDrawn != (moment.Pairing is not null, extent.Height))
            {
                phoneDrawn = (moment.Pairing is not null, extent.Height);
                phone = Panel.Phone(extent.Height, phoneDrawn.Live);
                phoneTexture = Upload(phone, phoneSet, phoneTexture);
            }

            if (!splashing && languageDrawn != (Words.Setting, extent.Height))
            {
                languageDrawn = (Words.Setting, extent.Height);
                languageButton = Panel.LanguageButton(Words.Setting, extent.Height);
                languageTexture = Upload(languageButton, languageSet, languageTexture);
            }

            if (moment.Lease > 0f && !splashing && leaseDrawn != (extent.Height, Words.Language))
            {
                leaseDrawn = (extent.Height, Words.Language);
                leaseCard = Panel.Lease(extent.Height);
                leaseTexture = Upload(leaseCard, leaseSet, leaseTexture);
                leaseDot = Panel.Dot(extent.Height);
                leaseDotTexture = Upload(leaseDot, leaseDotSet, leaseDotTexture);
            }

            var secondsLeft = (int)Math.Ceiling(moment.PairingLeft);
            if (pairingShown && !splashing && pairingDrawn != (moment.PairingId, secondsLeft, extent.Width, extent.Height, Words.Language))
            {
                pairingDrawn = (moment.PairingId, secondsLeft, extent.Width, extent.Height, Words.Language);
                pairingCard = Panel.Pairing(moment.Pairing, secondsLeft, moment.PairingId > 0 && moment.Pairing is null, extent.Width, extent.Height);
                pairingTexture = Upload(pairingCard, pairingSet, pairingTexture);
            }

            var slot = frame % FramesInFlight;
            device.WaitForFences([fences[slot]], true, -1);
            VkResult result;
            int imageIndex;
            try
            {
                result = device.AcquireNextImageKHR(swapchain, -1, acquired[slot], default, out imageIndex);
            }
            catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
            {
                result = e.Result; // BRUTAL throws Vulkan's errors; this one only says the window changed
                imageIndex = 0;
            }

            if (result == VkResult.ErrorOutOfDateKHR)
            {
                stale = true;
                continue;
            }

            device.ResetFences([fences[slot]]);
            device.ResetCommandPool(pools[slot], VkCommandPoolResetFlags.None);

            var cb = commands[slot];
            cb.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
            clears[0] = new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(0.006f, 0.005f, 0.005f, 1f) } };
            clears[1] = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1f } };
            cb.BeginRenderPass(new VkRenderPassBeginInfo
            {
                RenderPass = renderPass,
                Framebuffer = framebuffers[imageIndex],
                RenderArea = new VkRect2D { Extent = extent },
                ClearValueCount = 2,
                ClearValues = clears,
            }, VkSubpassContents.Inline);
            cb.SetViewport(firstViewport: 0, [new VkViewport { Width = extent.Width, Height = extent.Height, MinDepth = 0f, MaxDepth = 1f }]);
            cb.SetScissor(firstScissor: 0, [new VkRect2D { Extent = extent }]);
            if (splashing)
            {
                cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                var opacity = Splash.Opacity(shown);
                for (var i = 0; i < splashSets.Length; i++)
                {
                    var push = new SplashPush(splashRects[i], new SN.Vector4(splashTints[i], opacity));
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [splashSets[i]], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }
            }
            else
            {
                cb.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
                Draw(cb, layout, hall, new Push(SN.Matrix4x4.Identity, viewProj, default));
                for (var i = 0; i < parts.Length; i++)
                {
                    Draw(cb, layout, parts[i], new Push(worlds[i], viewProj, glows[i]));
                }

                if (shelf.Open && card is not null && cardTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var margin = CardMargin();
                    var push = new SplashPush(Rect(margin, margin, card.Width, card.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [cardSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (moment.HeardAge >= 0f && caption is not null && captionTexture is not null)
                {
                    // Centred at the bottom; it comes in over 0.2 s and fades over the last 0.8 s.
                    var fade = Math.Clamp(Math.Min(moment.HeardAge / 0.2f, ((float)Link.HeardSeconds(moment.Heard) - moment.HeardAge) / 0.8f), 0f, 1f);
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var push = new SplashPush(Rect((extent.Width - caption.Width) / 2, extent.Height - caption.Height - CardMargin(), caption.Width, caption.Height), new SN.Vector4(1f, 1f, 1f, fade));
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [captionSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (moment.Lease > 0f && leaseCard is not null && leaseTexture is not null && leaseDot is not null && leaseDotTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (left, top) = (CardMargin(), extent.Height - leaseCard.Height - CardMargin());
                    var push = new SplashPush(Rect(left, top, leaseCard.Width, leaseCard.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [leaseSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                    var lit = (int)MathF.Ceiling(Panel.LeaseDots * Math.Clamp(moment.LeaseLength > 0f ? moment.Lease / moment.LeaseLength : 0f, 0f, 1f));
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [leaseDotSet], []);
                    for (var i = 0; i < Panel.LeaseDots; i++)
                    {
                        var (x, y) = Panel.LeaseDot(i, extent.Height);
                        var tint = i >= Panel.LeaseDots - lit ? new SN.Vector4(0.353f, 0.863f, 0.51f, 1f) : new SN.Vector4(0.235f, 0.259f, 0.282f, 1f);
                        var dot = new SplashPush(Rect(left + (int)MathF.Round(x - (leaseDot.Width / 2f)), top + (int)MathF.Round(y - (leaseDot.Height / 2f)), leaseDot.Width, leaseDot.Height), tint);
                        cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&dot, sizeof(SplashPush)));
                        cb.Draw(6, 1, 0, 0);
                    }
                }

                if (languageButton is not null && languageTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (languageX, languageY) = LanguageAt();
                    var push = new SplashPush(Rect(languageX, languageY, languageButton.Width, languageButton.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [languageSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (phone is not null && phoneTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (phoneX, phoneY) = PhoneAt();
                    var push = new SplashPush(Rect(phoneX, phoneY, phone.Width, phone.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [phoneSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (pairingShown && pairingCard is not null && pairingTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (cardX, cardY) = PairingAt();
                    var push = new SplashPush(Rect(cardX, cardY, pairingCard.Width, pairingCard.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [pairingSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }
            }

            cb.EndRenderPass();
            var capturing = shot < shots.Length && shown >= shots[shot].At && extent.Width == shotExtent.Width && extent.Height == shotExtent.Height;
            if (capturing)
            {
                cb.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [],
                    [Barrier(images[imageIndex], VkImageLayout.PresentSrcKHR, VkImageLayout.TransferSrcOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit)]);
                cb.CopyImageToBuffer(images[imageIndex], VkImageLayout.TransferSrcOptimal, readback,
                    [new VkBufferImageCopy { ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 }, ImageExtent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 } }]);
                cb.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.BottomOfPipeBit, VkDependencyFlags.None, [], [],
                    [Barrier(images[imageIndex], VkImageLayout.TransferSrcOptimal, VkImageLayout.PresentSrcKHR, default, default)]);
            }

            cb.End();

            var waitStage = VkPipelineStageFlags.ColorAttachmentOutputBit;
            var commandHandle = cb.Handle;
            var waitSemaphore = acquired[slot];
            var signalSemaphore = renderDone[imageIndex];
            queue.Submit(
                [
                    new VkSubmitInfo
                    {
                        WaitSemaphoreCount = 1,
                        WaitSemaphores = &waitSemaphore,
                        WaitDstStageMask = &waitStage,
                        CommandBufferCount = 1,
                        CommandBuffers = &commandHandle,
                        SignalSemaphoreCount = 1,
                        SignalSemaphores = &signalSemaphore,
                    },
                ],
                fences[slot]);
            var presentIndex = imageIndex;
            var presentSwapchain = swapchain; // Build reassigns swapchain, whose address cannot be taken
            try
            {
                result = queue.PresentKHR(new VkPresentInfoKHR
                {
                    WaitSemaphoreCount = 1,
                    WaitSemaphores = &signalSemaphore,
                    SwapchainCount = 1,
                    Swapchains = &presentSwapchain,
                    ImageIndices = &presentIndex,
                });
            }
            catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
            {
                result = e.Result; // the window changed while the frame was drawn, as on 2026-10-22 at 16:35
            }

            if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            {
                stale = true;
            }

            var presented = clock.Elapsed.TotalSeconds;
            if (firstFrame < 0)
            {
                firstFrame = presented;
                nextFrame = presented;
                Console.WriteLine($"SCENE first frame presented after {presented * 1000:F0} ms");
            }
            else
            {
                frameTimes.Add((presented - lastPresent) * 1000.0); // present to present
            }

            lastPresent = presented;
            frame++;
            if (capturing)
            {
                device.WaitIdle();
                var path = Path.Combine(shotsDir!, shots[shot].Name + ".png");
                var bgra = surfaceFormat.Format.ToString().StartsWith("B8G8R8", StringComparison.Ordinal);
                Png.Save(path, new ReadOnlySpan<byte>((byte*)readbackMapped, (int)(extent.Width * extent.Height * 4)), extent.Width, extent.Height, bgra);
                Console.WriteLine($"SCENE shot {path} at {now:F2} s ({moment.State}, level {moment.Level:F2}, context {moment.Context:F2}{(moment.Compacting ? ", compacting" : "")})");
                shot++;
                lastPresent = clock.Elapsed.TotalSeconds; // the capture's wait is not a frame time
            }

            if (cap > 0)
            {
                // Pace to the cap: sleep while more than 1.5 ms is left, then spin. A frame that falls more than one
                // period behind resets the schedule instead of bursting to catch up.
                nextFrame += period;
                if (clock.Elapsed.TotalSeconds - nextFrame > period)
                {
                    nextFrame = clock.Elapsed.TotalSeconds;
                }

                while (nextFrame - clock.Elapsed.TotalSeconds > 0.0015)
                {
                    Thread.Sleep(1);
                }

                while (clock.Elapsed.TotalSeconds < nextFrame)
                {
                    Thread.SpinWait(20);
                }
            }
        }

        var loopSeconds = (clock.Elapsed - loopStart).TotalSeconds;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuStart;
        if (cap > 0 && OperatingSystem.IsWindows())
        {
            _ = TimeEndPeriod(1);
        }

        if (shot < shots.Length)
        {
            Console.WriteLine($"SCENE missing shots: the run ended at {loopSeconds:F1} s, before {shots[shot].Name} at {shots[shot].At} s");
        }

        // Clean shutdown, in reverse order of creation.
        device.WaitIdle();
        for (var i = 0; i < FramesInFlight; i++)
        {
            device.DestroySemaphore(acquired[i], null);
            device.DestroyFence(fences[i], null);
            device.DestroyCommandPool(pools[i], null);
        }

        if (shots.Length > 0)
        {
            device.UnmapMemory(readbackMemory);
            device.DestroyBuffer(readback, null);
            device.FreeMemory(readbackMemory, null);
        }

        foreach (var part in parts)
        {
            part.Dispose();
        }

        hall.Dispose();
        foreach (var texture in splashTextures)
        {
            texture.Dispose();
        }

        cardTexture?.Dispose();
        captionTexture?.Dispose();
        phoneTexture?.Dispose();
        languageTexture?.Dispose();
        pairingTexture?.Dispose();
        leaseTexture?.Dispose();
        leaseDotTexture?.Dispose();
        device.DestroyCommandPool(uploadPool, null);

        device.DestroyDescriptorPool(descriptorPool, null);
        device.DestroyPipeline(splashPipeline, null);
        device.DestroyPipelineLayout(splashLayout, null);
        device.DestroyDescriptorSetLayout(setLayout, null);
        device.DestroySampler(sampler, null);
        device.DestroyShaderModule(splashVertex, null);
        device.DestroyShaderModule(splashFragment, null);
        device.DestroyPipeline(pipeline, null);
        device.DestroyPipelineLayout(layout, null);
        device.DestroyShaderModule(vertexModule, null);
        device.DestroyShaderModule(fragmentModule, null);
        Teardown();
        device.DestroyRenderPass(renderPass, null);
        device.DestroySwapchainKHR(swapchain, null);
        tray?.Dispose();
        device.Dispose();
        instance.DestroySurfaceKHR(surface, null);
        instance.Dispose();
        window.Dispose();
        Glfw.Terminate();

        frameTimes.Sort();
        double P(double q) => frameTimes.Count == 0 ? double.NaN : frameTimes[Math.Min(frameTimes.Count - 1, (int)(q * frameTimes.Count))];
        Console.WriteLine($"SCENE frames {frame} in {loopSeconds:F2} s ({frame / loopSeconds:F0} fps); frame ms p50 {P(0.5):F3} p95 {P(0.95):F3} p99 {P(0.99):F3} max {(frameTimes.Count > 0 ? frameTimes[^1] : double.NaN):F3}");
        Console.WriteLine($"SCENE CPU {cpu.TotalSeconds:F2} s over {loopSeconds:F2} s ({100 * cpu.TotalSeconds / loopSeconds / Environment.ProcessorCount:F1} % of {Environment.ProcessorCount} logical CPUs), peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB");
        Console.WriteLine($"SCENE closed by {reason} after {clock.Elapsed.TotalSeconds:F2} s");
        if (reason is "window closed" or "Esc")
        {
            Closed(port, true);
        }

        return 0;
    }

    // %LOCALAPPDATA%\delta\scene-<port>.closed says that the user closed the window listening on that port, so that a
    // brain, which may not have started it, leaves it closed until something asks for it (brain/scene.go).
    private static void Closed(int port, bool closed)
    {
        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", $"scene-{port}.closed");
        try
        {
            if (closed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                File.Delete(marker);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"SCENE {marker}: {e.Message}");
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    private static void Draw(CommandBuffer cb, VkPipelineLayout layout, Mesh mesh, Push push)
    {
        cb.PushConstants(layout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(Push)));
        cb.BindVertexBuffer(0, mesh.Buffer);
        cb.Draw(mesh.Count, 1, 0, 0);
    }

    // The value after the option name, if both are there.
    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    // A colour image's layout change, as HallSlice writes it.
    internal static VkImageMemoryBarrier Barrier(VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlags src, VkAccessFlags dst) => new()
    {
        SrcAccessMask = src,
        DstAccessMask = dst,
        OldLayout = from,
        NewLayout = to,
        SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        Image = image,
        SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
    };

    private static VkImageViewCreateInfo View(VkImage image, VkFormat format, VkImageAspectFlags aspect) => new()
    {
        Image = image,
        ViewType = VkImageViewType._2D,
        Format = format,
        SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
    };

    private static VkShaderModule Shader(Device device, string source, ShaderKind kind, string name)
    {
        var compiler = ShaderC.CreateCompiler();
        var options = ShaderC.CreateCompilerOptions();
        using var result = compiler.Compile(Encoding.UTF8.GetBytes(source), kind, Utf8.GetBytes(name + "\0"), Utf8.GetBytes("main\0"), options);
        var module = device.CreateShaderModule(new VkShaderModuleCreateInfo { CodeSize = result.GetLength(), Code = (int*)result.GetBytes() }, null);
        options.Dispose();
        compiler.Dispose();
        return module;
    }

    // The scene's pipeline, or with overlay the splash's: no vertex buffer, no depth, alpha blended.
    private static VkPipeline Pipeline(Device device, VkPipelineLayout layout, VkRenderPass renderPass, VkShaderModule vertex, VkShaderModule fragment, bool overlay = false)
    {
        var entry = "main"u8;
        fixed (byte* entryName = entry)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[]
            {
                new() { Stage = VkShaderStageFlags.VertexBit, Module = vertex, Name = entryName },
                new() { Stage = VkShaderStageFlags.FragmentBit, Module = fragment, Name = entryName },
            };
            var binding = new VkVertexInputBindingDescription { Binding = 0, Stride = ByteSize.Of<Vertex>(), InputRate = VkVertexInputRate.Vertex };
            var attributes = stackalloc VkVertexInputAttributeDescription[]
            {
                new() { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Zero },
                new() { Location = 1, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Of<float>(3) },
                new() { Location = 2, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Of<float>(6) },
            };
            var vertexInput = overlay ? new VkPipelineVertexInputStateCreateInfo() : new VkPipelineVertexInputStateCreateInfo
            {
                VertexBindingDescriptionCount = 1,
                VertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                VertexAttributeDescriptions = attributes,
            };
            var assembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                PolygonMode = VkPolygonMode.Fill,
                CullMode = VkCullModeFlags.None,
                FrontFace = VkFrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
            var depth = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = !overlay, DepthWriteEnable = !overlay, DepthCompareOp = VkCompareOp.Less };
            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = overlay,
                SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
                DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = VkBlendFactor.One,
                DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
            };
            var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
            var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };

            VkPipeline pipeline;
            device.CreateGraphicsPipelines(
                default,
                [
                    new VkGraphicsPipelineCreateInfo
                    {
                        StageCount = 2,
                        Stages = stages,
                        VertexInputState = &vertexInput,
                        InputAssemblyState = &assembly,
                        ViewportState = &viewport,
                        RasterizationState = &raster,
                        MultisampleState = &multisample,
                        DepthStencilState = &depth,
                        ColorBlendState = &blend,
                        DynamicState = &dynamic,
                        Layout = layout,
                        RenderPass = renderPass,
                        Subpass = 0,
                    },
                ],
                default,
                new ReadOnlySpan<VkPipeline>(&pipeline, 1));
            return pipeline;
        }
    }
}
