// Brickyard P0 (GuitarAlchemist/gaia#260): the robot you talk to, standing in a brick hall, drawn with Vulkan through
// BRUTAL's bindings. The brain (../brain) tells it what it is doing, one JSON line per change on a local TCP port:
// {"state":"idle"}, "listening", "thinking", or {"state":"speaking","seconds":S,"envelope":[...]} with the voice's
// loudness every 20 ms, which opens the mouth as the sentence plays.
//
//	scene [--listen 8805] [--seconds N] [--demo] [--shots DIR] [--hidden] [--immediate] [--cap N] | --selftest
//
// --demo plays idle, thinking and speaking in a loop without the brain, and --shots DIR saves a PNG of each. The scene
// waits for vsync unless --immediate; --cap N limits it to N frames a second, 60 by default when hidden (nothing paces
// a hidden window), so that it never takes the GPU from a live view beside it. Esc closes it, as does --seconds.

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
        (double At, string Name)[] shots = shotsDir is null ? [] : [(1.0, "idle"), (2.6, "listening"), (4.2, "thinking"), (6.2, "speaking")];
        if (shots.Length > 0 && !demo)
        {
            Console.Error.WriteLine("SCENE refused: --shots needs --demo, whose states come at known times");
            return 2;
        }

        var link = new Link(() => clock.Elapsed.TotalSeconds);
        if (!demo)
        {
            var error = link.Listen(port);
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
        Glfw.WindowHint(GlfwWindowHint.Resizable, 0);
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

        var caps = physical.GetSurfaceCapabilitiesKHR(surface);
        var extent = caps.CurrentExtent.Width == -1 ? new VkExtent2D { Width = Width, Height = Height } : caps.CurrentExtent;
        var imageCount = Math.Max(caps.MinImageCount, 3);
        if (caps.MaxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, caps.MaxImageCount);
        }

        var swapchain = device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR
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
        }, null);
        var images = new VkImage[device.GetSwapchainImagesKHR_Count(swapchain)];
        device.GetSwapchainImagesKHR_Write(swapchain, images);

        // Depth buffer.
        const VkFormat depthFormat = VkFormat.D32SFloat;
        var depthImage = device.CreateImage(new VkImageCreateInfo
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
        var depthMemory = device.AllocateMemory(physical, depthImage, VkMemoryPropertyFlags.DeviceLocalBit, null);
        device.BindImageMemory(depthImage, depthMemory, ByteSize.Zero);
        var depthView = device.CreateImageView(View(depthImage, depthFormat, VkImageAspectFlags.DepthBit), null);

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

        var views = new VkImageView[images.Length];
        var framebuffers = new VkFramebuffer[images.Length];
        var renderDone = new VkSemaphore[images.Length];
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

        var reason = "window closed";
        window.OnKey += (w, key, scancode, action, mods) =>
        {
            if (action != GlfwKeyAction.Release && key == GlfwKey.Escape)
            {
                reason = "Esc";
                w.ShouldClose = true;
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
            var now = clock.Elapsed.TotalSeconds;
            if (now >= limit)
            {
                reason = "time limit";
                break;
            }

            var dt = (float)(now - last);
            last = now;
            if (demo)
            {
                link.Demo(now);
            }

            var (state, level) = link.Read(now);
            var time = (float)now;
            robot.Pose(time, dt, state, level, worlds, glows);

            // The camera drifts a little from side to side in front of the robot, at the height of its face.
            var eye = new SN.Vector3(0.45f * MathF.Sin(time * 0.11f), 1.08f + (0.04f * MathF.Sin(time * 0.07f)), 2.55f);
            var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(0.75f, extent.Width / (float)extent.Height, 0.1f, 40f);
            projection.M22 = -projection.M22; // Vulkan's clip space has y pointing down
            var viewProj = SN.Matrix4x4.CreateLookAt(eye, new SN.Vector3(0f, 0.7f, 0f), SN.Vector3.UnitY) * projection;

            var slot = frame % FramesInFlight;
            device.WaitForFences([fences[slot]], true, -1);
            var result = device.AcquireNextImageKHR(swapchain, -1, acquired[slot], default, out var imageIndex);
            if (result == VkResult.ErrorOutOfDateKHR)
            {
                reason = "swapchain out of date (the window is fixed-size; rebuilding is out of scope)";
                break;
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
            cb.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
            cb.SetViewport(firstViewport: 0, [new VkViewport { Width = extent.Width, Height = extent.Height, MinDepth = 0f, MaxDepth = 1f }]);
            cb.SetScissor(firstScissor: 0, [new VkRect2D { Extent = extent }]);
            Draw(cb, layout, hall, new Push(SN.Matrix4x4.Identity, viewProj, default));
            for (var i = 0; i < parts.Length; i++)
            {
                Draw(cb, layout, parts[i], new Push(worlds[i], viewProj, glows[i]));
            }

            cb.EndRenderPass();
            var capturing = shot < shots.Length && now >= shots[shot].At;
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
            result = queue.PresentKHR(new VkPresentInfoKHR
            {
                WaitSemaphoreCount = 1,
                WaitSemaphores = &signalSemaphore,
                SwapchainCount = 1,
                Swapchains = &swapchain,
                ImageIndices = &presentIndex,
            });
            if (result == VkResult.ErrorOutOfDateKHR)
            {
                reason = "swapchain out of date (the window is fixed-size; rebuilding is out of scope)";
                break;
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
                Console.WriteLine($"SCENE shot {path} at {now:F2} s ({state}, level {level:F2})");
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
        device.DestroyPipeline(pipeline, null);
        device.DestroyPipelineLayout(layout, null);
        device.DestroyShaderModule(vertexModule, null);
        device.DestroyShaderModule(fragmentModule, null);
        for (var i = 0; i < images.Length; i++)
        {
            device.DestroySemaphore(renderDone[i], null);
            device.DestroyFramebuffer(framebuffers[i], null);
            device.DestroyImageView(views[i], null);
        }

        device.DestroyRenderPass(renderPass, null);
        device.DestroyImageView(depthView, null);
        device.DestroyImage(depthImage, null);
        device.FreeMemory(depthMemory, null);
        device.DestroySwapchainKHR(swapchain, null);
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
        return 0;
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
    private static VkImageMemoryBarrier Barrier(VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlags src, VkAccessFlags dst) => new()
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

    private static VkPipeline Pipeline(Device device, VkPipelineLayout layout, VkRenderPass renderPass, VkShaderModule vertex, VkShaderModule fragment)
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
            var vertexInput = new VkPipelineVertexInputStateCreateInfo
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
            var depth = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = VkCompareOp.Less };
            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
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
