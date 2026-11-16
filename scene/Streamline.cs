using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

// NVIDIA Streamline 2.10 (DLSS, DLSS Ray Reconstruction, DLSS Frame Generation, Reflex), loaded from a folder of
// NVIDIA's signed DLLs (--streamline DIR, else %LOCALAPPDATA%\delta\streamline) when --dlss asks for it (--dlss-g
// adds frame generation). Optional over-the-air updates and downloaded plugins are off, and every DLL is checked for
// NVIDIA's Authenticode signature before sl.interposer.dll is loaded from that folder by its full path. But slInit
// still runs the driver's NGX updater (nvngx_update.exe) for each feature loaded, whatever the flags (Streamline's
// pluginManager.cpp, SL_PRODUCTION builds), and that updater reached the network (port 443) on 2026-11-15; the author
// accepted that contact for --dlss the same day ("2 - c'est acceptable"). BRUTAL keeps its own vulkan-1.dll:
// Streamline runs in its manual hooking mode, told of the instance, the device and the queues (slSetVulkanInfo).
// Ray Reconstruction (PathTracer) is evaluated into the path tracer's command buffer, which binds again all it uses
// after it.
//
// The structures below are Streamline's own (sl_core_types.h, sl_helpers_vk.h, ...), laid out as the SDK's compiler
// lays them out; SelfTest holds them to the sizes and offsets a probe built against the SDK's headers printed.
internal static unsafe class Streamline
{
    public const ulong SdkVersion = 0x2000a0000fedcUL; // 2.10.0, sl_version.h's kSDKVersion

    public const uint FeatureDlss = 0;
    public const uint FeatureReflex = 3;
    public const uint FeaturePcl = 4;
    public const uint FeatureDlssG = 1000;
    public const uint FeatureDlssRR = 1001;

    // The features loaded: DLSS and Ray Reconstruction, and frame generation with the Reflex and PCL it runs with when
    // asked. Each feature loaded costs a run of the driver's NGX updater at slInit (about 1.4 s each here).
    public static readonly uint[] Upscaling = [FeatureDlss, FeatureDlssRR];
    public static readonly uint[] FrameGeneration = [FeatureDlss, FeatureDlssRR, FeatureDlssG, FeatureReflex, FeaturePcl];

    public static uint[] Features { get; private set; } = [];

    public static string FeatureName(uint feature) => feature switch
    {
        FeatureDlss => "DLSS",
        FeatureDlssRR => "DLSS-RR",
        FeatureDlssG => "DLSS-G",
        FeatureReflex => "Reflex",
        FeaturePcl => "PCL",
        _ => $"feature {feature}",
    };

    // The DLLs that run: the interposer, the plugins asked for and NGX's networks.
    public static readonly string[] Dlls =
    [
        "sl.interposer.dll", "sl.common.dll", "sl.dlss.dll", "sl.dlss_d.dll", "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll",
        "nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll",
    ];

    private static nint module;
    private static string folder = "";
    private static readonly List<string> log = [];
    private static bool verbose;

    // sl_result.h's Result, by its value.
    public static string ResultName(int r) => r switch
    {
        0 => "ok",
        1 => "IO", 2 => "driver out of date", 3 => "OS out of date", 4 => "OS hardware scheduling off",
        5 => "device not created", 6 => "no supported adapter", 7 => "adapter not supported", 8 => "no plugins",
        9 => "Vulkan API", 16 => "NGX failed", 20 => "invalid integration", 21 => "missing input", 22 => "not initialized",
        24 => "init not called", 26 => "invalid parameter", 27 => "missing constants", 28 => "duplicated constants",
        32 => "feature missing", 33 => "feature not supported", 34 => "feature missing hooks", 35 => "feature failed to load",
        37 => "feature missing dependency", 39 => "invalid state", 40 => "out of VRAM",
        _ => $"result {r}",
    };

    public static bool Loaded => module != 0;

    public static string Folder => folder;

    // What Streamline logged, warnings and errors, for the exit line and the RTX card.
    public static IReadOnlyList<string> Log => log;

    // The folder's DLLs all signed by NVIDIA Corporation, else why not.
    public static string? Unsigned(string dir)
    {
        foreach (var name in Dlls)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                return $"{name} is missing";
            }

            if (!Signed(path))
            {
                return $"{name} is not signed by NVIDIA";
            }
        }

        return null;
    }

    // Authenticode: the file's signature verifies to a trusted root (WinVerifyTrust), and its signer is NVIDIA.
    private static bool Signed(string path)
    {
        if (WinTrust.Verify(path) != 0)
        {
            return false;
        }

#pragma warning disable SYSLIB0057 // the signer of a signed file; no loader replaces this one
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return signer.GetNameInfo(X509NameType.SimpleName, false) == "NVIDIA Corporation";
    }

    // Checks the folder, loads the interposer and initialises it for Vulkan, manual hooking, the five features. Null
    // when it is ready, else why it is not.
    public static string? Init(string dir, uint[] features, bool verbose = false)
    {
        Features = features;
        Streamline.verbose = verbose;
        if (Unsigned(dir) is { } why)
        {
            return why;
        }

        folder = dir;
        module = NativeLibrary.Load(Path.Combine(dir, "sl.interposer.dll"));
        var paths = stackalloc nint[1];
        var dirW = Marshal.StringToHGlobalUni(dir);
        var version = Marshal.StringToHGlobalAnsi("delta");
        var project = Marshal.StringToHGlobalAnsi("4e0a7d39-6f0b-4c1e-9a52-5d3e2b8f9c11");
        paths[0] = dirW;
        fixed (uint* loaded = Features)
        {
            var preferences = new Preferences
            {
                Base = new BaseStructure(PreferencesType, 1),
                LogLevel = verbose ? 2u : 1u,
                PathsToPlugins = paths,
                NumPathsToPlugins = 1,
                LogMessageCallback = &OnLog,
                // No over-the-air updates or downloaded plugins; the scene restores its own command buffer state;
                // tags are given per frame; the hooks are the scene's.
                Flags = DisableCLStateTracking | UseManualHooking | UseFrameBasedResourceTagging,
                FeaturesToLoad = loaded,
                NumFeaturesToLoad = (uint)Features.Length,
                Engine = 0,
                EngineVersion = (byte*)version,
                ProjectId = (byte*)project,
                RenderApi = 2,
            };
            var init = (delegate* unmanaged<Preferences*, ulong, int>)Export("slInit");
            var result = init(&preferences, SdkVersion);
            return result == 0 ? null : $"slInit: {ResultName(result)}";
        }
    }

    [UnmanagedCallersOnly]
    private static void OnLog(uint type, byte* message)
    {
        var text = Marshal.PtrToStringUTF8((nint)message)?.Trim() ?? "";
        if ((type >= 1 || verbose) && log.Count < 400)
        {
            log.Add(text);
            Console.WriteLine($"STREAMLINE {(type == 2 ? "error" : type == 1 ? "warning" : "info")}: {text}");
        }
    }

    private static nint Export(string name) => NativeLibrary.GetExport(module, name);

    // Whether the GPU can run the feature, as Streamline sees it: ok, or why not.
    public static int Supported(uint feature, nint physicalDevice)
    {
        var adapter = new AdapterInfo { Base = new BaseStructure(AdapterInfoType, 1), VkPhysicalDevice = physicalDevice };
        return ((delegate* unmanaged<uint, AdapterInfo*, int>)Export("slIsFeatureSupported"))(feature, &adapter);
    }

    // What a feature needs of the instance and the device.
    public sealed record Needs(string[] InstanceExtensions, string[] DeviceExtensions, string[] Features12, string[] Features13,
        int ComputeQueues, int GraphicsQueues, int OpticalFlowQueues);

    public static Needs? Requirements(uint feature)
    {
        var r = new FeatureRequirements { Base = new BaseStructure(FeatureRequirementsType, 2) };
        if (((delegate* unmanaged<uint, FeatureRequirements*, int>)Export("slGetFeatureRequirements"))(feature, &r) != 0)
        {
            return null;
        }

        static string[] Strings(uint count, byte** items) =>
            [.. Enumerable.Range(0, (int)count).Select(i => Marshal.PtrToStringUTF8((nint)items[i]) ?? "")];
        return new Needs(Strings(r.VkNumInstanceExtensions, r.VkInstanceExtensions), Strings(r.VkNumDeviceExtensions, r.VkDeviceExtensions),
            Strings(r.VkNumFeatures12, r.VkFeatures12), Strings(r.VkNumFeatures13, r.VkFeatures13),
            (int)r.VkNumComputeQueuesRequired, (int)r.VkNumGraphicsQueuesRequired, (int)r.VkNumOpticalFlowQueuesRequired);
    }

    public static int SetVulkanInfo(nint device, nint instance, nint physical, uint graphicsFamily, uint graphicsIndex,
        uint computeFamily, uint computeIndex, uint opticalFlowFamily, uint opticalFlowIndex, bool nativeOpticalFlow)
    {
        var info = new VulkanInfo
        {
            Base = new BaseStructure(VulkanInfoType, 3),
            Device = device,
            Instance = instance,
            PhysicalDevice = physical,
            ComputeQueueIndex = computeIndex,
            ComputeQueueFamily = computeFamily,
            GraphicsQueueIndex = graphicsIndex,
            GraphicsQueueFamily = graphicsFamily,
            OpticalFlowQueueIndex = opticalFlowIndex,
            OpticalFlowQueueFamily = opticalFlowFamily,
            UseNativeOpticalFlowMode = nativeOpticalFlow,
        };
        return ((delegate* unmanaged<VulkanInfo*, int>)Export("slSetVulkanInfo"))(&info);
    }

    public static void Shutdown()
    {
        if (module != 0)
        {
            ((delegate* unmanaged<int>)Export("slShutdown"))();
        }
    }

    private static delegate* unmanaged<nint, void*, int> present;

    public static bool Presents => present != null;

    // The interposer's vkQueuePresentKHR (its vkGetDeviceProcAddr's, the device told): Streamline's common plugin
    // does its once-a-frame bookkeeping and collection there (presentCommon), then presents through the device's own.
    // In manual hooking, the host presents through it, or Streamline's resources grow frame after frame.
    public static void BindPresent(nint device)
    {
        var getDeviceProcAddr = (delegate* unmanaged<nint, byte*, nint>)Export("vkGetDeviceProcAddr");
        fixed (byte* name = "vkQueuePresentKHR\0"u8)
        {
            present = (delegate* unmanaged<nint, void*, int>)getDeviceProcAddr(device, name);
        }
    }

    public static int Present(nint queue, void* info) => present(queue, info);

    // DLSS's modes (sl_dlss.h's DLSSMode), by the names --dlss-mode takes.
    public static readonly string[] Modes = ["off", "performance", "balanced", "quality", "ultra-performance", "ultra-quality", "dlaa"];

    private static delegate* unmanaged<DLSSDOptions*, DLSSDOptimalSettings*, int> rrOptimal;
    private static delegate* unmanaged<ViewportHandle*, DLSSDOptions*, int> rrSetOptions;

    // Ray Reconstruction's own functions, once the device is told: null when they are there, else why not.
    public static string? BindRayReconstruction()
    {
        var get = (delegate* unmanaged<uint, byte*, nint*, int>)Export("slGetFeatureFunction");
        nint optimal = 0, set = 0;
        fixed (byte* a = "slDLSSDGetOptimalSettings\0"u8, b = "slDLSSDSetOptions\0"u8)
        {
            var r = get(FeatureDlssRR, a, &optimal);
            if (r != 0 || optimal == 0)
            {
                return $"slDLSSDGetOptimalSettings: {ResultName(r)}";
            }

            r = get(FeatureDlssRR, b, &set);
            if (r != 0 || set == 0)
            {
                return $"slDLSSDSetOptions: {ResultName(r)}";
            }
        }

        rrOptimal = (delegate* unmanaged<DLSSDOptions*, DLSSDOptimalSettings*, int>)optimal;
        rrSetOptions = (delegate* unmanaged<ViewportHandle*, DLSSDOptions*, int>)set;
        return null;
    }

    // The size Ray Reconstruction traces at for a mode and an output size, as DLSS recommends it.
    public static (int Width, int Height)? RenderSize(uint mode, int outputWidth, int outputHeight)
    {
        var options = new DLSSDOptions { Base = new BaseStructure(DLSSDOptionsType, 3), Mode = mode, OutputWidth = (uint)outputWidth, OutputHeight = (uint)outputHeight, PreExposure = 1f, ExposureScale = 1f, ColorBuffersHDR = 1, NormalRoughnessMode = 1 };
        var settings = new DLSSDOptimalSettings { Base = new BaseStructure(DLSSDOptimalSettingsType, 1) };
        return rrOptimal(&options, &settings) == 0 && settings.OptimalRenderWidth > 0 && settings.OptimalRenderHeight > 0
            ? ((int)settings.OptimalRenderWidth, (int)settings.OptimalRenderHeight)
            : null;
    }

    // One frame of Ray Reconstruction, recorded into the command buffer: a new frame token for the frame's number,
    // the camera's constants, the options, the images tagged and the feature evaluated. Ok, or the first call that
    // failed and why.
    public static string? EvaluateRayReconstruction(nint commandBuffer, uint frameIndex, ref Constants constants, ref DLSSDOptions options, ResourceTag[] tags)
    {
        var viewport = new ViewportHandle { Base = new BaseStructure(ViewportHandleType, 1), Value = 0 };
        nint token = 0;
        var r = ((delegate* unmanaged<nint*, uint*, int>)Export("slGetNewFrameToken"))(&token, &frameIndex);
        if (r != 0)
        {
            return $"slGetNewFrameToken: {ResultName(r)}";
        }

        constants.Base = new BaseStructure(ConstantsType, 2);
        fixed (Constants* c = &constants)
        {
            r = ((delegate* unmanaged<Constants*, nint, ViewportHandle*, int>)Export("slSetConstants"))(c, token, &viewport);
        }

        if (r != 0)
        {
            return $"slSetConstants: {ResultName(r)}";
        }

        options.Base = new BaseStructure(DLSSDOptionsType, 3);
        fixed (DLSSDOptions* o = &options)
        {
            r = rrSetOptions(&viewport, o);
        }

        if (r != 0)
        {
            return $"slDLSSDSetOptions: {ResultName(r)}";
        }

        fixed (ResourceTag* t = tags)
        {
            r = ((delegate* unmanaged<nint, ViewportHandle*, ResourceTag*, uint, nint, int>)Export("slSetTagForFrame"))(token, &viewport, t, (uint)tags.Length, commandBuffer);
        }

        if (r != 0)
        {
            return $"slSetTagForFrame: {ResultName(r)}";
        }

        var inputs = stackalloc BaseStructure*[1];
        inputs[0] = &viewport.Base;
        r = ((delegate* unmanaged<uint, nint, BaseStructure**, uint, nint, int>)Export("slEvaluateFeature"))(FeatureDlssRR, token, inputs, 1, commandBuffer);
        return r == 0 ? null : $"slEvaluateFeature: {ResultName(r)}";
    }

    // A Vulkan image as Streamline takes it, in the General layout, where the path tracer keeps its images.
    public static Resource Image(nint image, nint memory, nint view, int width, int height, uint format, uint usage) => new()
    {
        Base = new BaseStructure(ResourceType, 1),
        Type = 0, // eTex2d
        Native = image,
        Memory = memory,
        View = view,
        State = 1, // VK_IMAGE_LAYOUT_GENERAL
        Width = (uint)width,
        Height = (uint)height,
        NativeFormat = format,
        MipLevels = 1,
        ArrayLayers = 1,
        Usage = usage,
    };

    public static ResourceTag Tag(Resource* resource, uint bufferType, int width, int height) => new()
    {
        Base = new BaseStructure(ResourceTagType, 1),
        Resource = resource,
        Type = bufferType,
        Lifecycle = 1, // eValidUntilPresent: the images stay as they are until the next frame's trace
        Extent = new Extent { Width = (uint)width, Height = (uint)height },
    };

    // sl_core_types.h's buffer types, those Ray Reconstruction is given.
    public const uint BufferMotionVectors = 1;
    public const uint BufferScalingInputColor = 3;
    public const uint BufferScalingOutputColor = 4;
    public const uint BufferAlbedo = 7;
    public const uint BufferSpecularAlbedo = 8;
    public const uint BufferNormalRoughness = 14;
    public const uint BufferSpecularHitDistance = 42;
    public const uint BufferLinearDepth = 49;

    // The flags of Preferences (sl_core_types.h's PreferenceFlags).
    private const ulong DisableCLStateTracking = 1 << 0;
    private const ulong UseManualHooking = 1 << 2;
    private const ulong UseFrameBasedResourceTagging = 1 << 7;

    private static readonly Guid PreferencesType = new(0x1ca10965, 0xbf8e, 0x432b, 0x8d, 0xa1, 0x67, 0x16, 0xd8, 0x79, 0xfb, 0x14);
    private static readonly Guid FeatureRequirementsType = new(0x66714097, 0xac6d, 0x4bc6, 0x89, 0x15, 0x1e, 0x0f, 0x55, 0xa6, 0xb6, 0x1f);
    private static readonly Guid AdapterInfoType = new(0x0677315f, 0xa746, 0x4492, 0x9f, 0x42, 0xcb, 0x61, 0x42, 0xc9, 0xc3, 0xd4);
    private static readonly Guid VulkanInfoType = new(0x0eed6fd5, 0x82cd, 0x43a9, 0xbd, 0xb5, 0x47, 0xa5, 0xba, 0x2f, 0x45, 0xd6);
    private static readonly Guid ViewportHandleType = new(0x171b6435, 0x9b3c, 0x4fc8, 0x99, 0x94, 0xfb, 0xe5, 0x25, 0x69, 0xaa, 0xa4);
    private static readonly Guid ConstantsType = new(0xdcd35ad7, 0x4e4a, 0x4bad, 0xa9, 0x0c, 0xe0, 0xc4, 0x9e, 0xb2, 0x3a, 0xfe);
    private static readonly Guid ResourceType = new(0x3a9d70cf, 0x2418, 0x4b72, 0x83, 0x91, 0x13, 0xf8, 0x72, 0x1c, 0x72, 0x61);
    private static readonly Guid ResourceTagType = new(0x4c6a5aad, 0xb445, 0x496c, 0x87, 0xff, 0x1a, 0xf3, 0x84, 0x5b, 0xe6, 0x53);
    private static readonly Guid DLSSDOptionsType = new(0x0ad87504, 0x774e, 0x4bf3, 0x96, 0x33, 0xa4, 0x4d, 0x1f, 0x7f, 0x9c, 0xb8);
    private static readonly Guid DLSSDOptimalSettingsType = new(0xfbd0c637, 0xa28f, 0x41f2, 0xbc, 0x91, 0xb4, 0x21, 0xfa, 0xee, 0x8e, 0x1e);

    [StructLayout(LayoutKind.Sequential)]
    public struct ViewportHandle
    {
        public BaseStructure Base;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Extent
    {
        public uint Top;
        public uint Left;
        public uint Width;
        public uint Height;
    }

    // sl::Resource: a Vulkan image, its memory and view, its layout, size, format and usage.
    [StructLayout(LayoutKind.Sequential)]
    public struct Resource
    {
        public BaseStructure Base;
        public byte Type;
        public nint Native;
        public nint Memory;
        public nint View;
        public uint State;
        public uint Width;
        public uint Height;
        public uint NativeFormat;
        public uint MipLevels;
        public uint ArrayLayers;
        public ulong GpuVirtualAddress;
        public uint Flags;
        public uint Usage;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceTag
    {
        public BaseStructure Base;
        public Resource* Resource;
        public uint Type;
        public uint Lifecycle;
        public Extent Extent;
    }

    // sl::Constants: the camera, row major as System.Numerics' matrices (a row vector times the matrix), without the
    // jitter, which comes apart in pixels.
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public BaseStructure Base;
        public System.Numerics.Matrix4x4 CameraViewToClip;
        public System.Numerics.Matrix4x4 ClipToCameraView;
        public System.Numerics.Matrix4x4 ClipToLensClip;
        public System.Numerics.Matrix4x4 ClipToPrevClip;
        public System.Numerics.Matrix4x4 PrevClipToClip;
        public System.Numerics.Vector2 JitterOffset;
        public System.Numerics.Vector2 MvecScale;
        public System.Numerics.Vector2 CameraPinholeOffset;
        public System.Numerics.Vector3 CameraPos;
        public System.Numerics.Vector3 CameraUp;
        public System.Numerics.Vector3 CameraRight;
        public System.Numerics.Vector3 CameraFwd;
        public float CameraNear;
        public float CameraFar;
        public float CameraFov;
        public float CameraAspectRatio;
        public float MotionVectorsInvalidValue;
        public byte DepthInverted;
        public byte CameraMotionIncluded;
        public byte MotionVectors3D;
        public byte Reset;
        public byte OrthographicProjection;
        public byte MotionVectorsDilated;
        public byte MotionVectorsJittered;
        public float MinRelativeLinearDepthObjectSeparation;
    }

    // sl::DLSSDOptions (sl_dlss_d.h, version 3).
    [StructLayout(LayoutKind.Sequential)]
    public struct DLSSDOptions
    {
        public BaseStructure Base;
        public uint Mode;
        public uint OutputWidth;
        public uint OutputHeight;
        public float Sharpness;
        public float PreExposure;
        public float ExposureScale;
        public byte ColorBuffersHDR;
        public byte IndicatorInvertAxisX;
        public byte IndicatorInvertAxisY;
        public uint NormalRoughnessMode;
        public System.Numerics.Matrix4x4 WorldToCameraView;
        public System.Numerics.Matrix4x4 CameraViewToWorld;
        public byte AlphaUpscalingEnabled;
        public uint DlaaPreset;
        public uint QualityPreset;
        public uint BalancedPreset;
        public uint PerformancePreset;
        public uint UltraPerformancePreset;
        public uint UltraQualityPreset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DLSSDOptimalSettings
    {
        public BaseStructure Base;
        public uint OptimalRenderWidth;
        public uint OptimalRenderHeight;
        public float OptimalSharpness;
        public uint RenderWidthMin;
        public uint RenderHeightMin;
        public uint RenderWidthMax;
        public uint RenderHeightMax;
    }

    // sl::BaseStructure: the chain, the structure's GUID (laid out as Windows' GUID, which System.Guid matches) and
    // its version.
    [StructLayout(LayoutKind.Sequential)]
    public struct BaseStructure(Guid type, ulong version)
    {
        public nint Next;
        public Guid StructType = type;
        public ulong StructVersion = version;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Preferences
    {
        public BaseStructure Base;
        public byte ShowConsole;
        public uint LogLevel;
        public nint* PathsToPlugins;
        public uint NumPathsToPlugins;
        public nint PathToLogsAndData;
        public nint AllocateCallback;
        public nint ReleaseCallback;
        public delegate* unmanaged<uint, byte*, void> LogMessageCallback;
        public ulong Flags;
        public uint* FeaturesToLoad;
        public uint NumFeaturesToLoad;
        public uint ApplicationId;
        public uint Engine;
        public byte* EngineVersion;
        public byte* ProjectId;
        public uint RenderApi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SlVersion
    {
        public uint Major;
        public uint Minor;
        public uint Build;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FeatureRequirements
    {
        public BaseStructure Base;
        public uint Flags;
        public uint MaxNumCPUThreads;
        public uint MaxNumViewports;
        public uint NumRequiredTags;
        public uint* RequiredTags;
        public SlVersion OsVersionDetected;
        public SlVersion OsVersionRequired;
        public SlVersion DriverVersionDetected;
        public SlVersion DriverVersionRequired;
        public uint VkNumComputeQueuesRequired;
        public uint VkNumGraphicsQueuesRequired;
        public uint VkNumDeviceExtensions;
        public byte** VkDeviceExtensions;
        public uint VkNumInstanceExtensions;
        public byte** VkInstanceExtensions;
        public uint VkNumFeatures12;
        public byte** VkFeatures12;
        public uint VkNumFeatures13;
        public byte** VkFeatures13;
        public uint VkNumOpticalFlowQueuesRequired;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterInfo
    {
        public BaseStructure Base;
        public byte* DeviceLuid;
        public uint DeviceLuidSizeInBytes;
        public nint VkPhysicalDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VulkanInfo
    {
        public BaseStructure Base;
        public nint Device;
        public nint Instance;
        public nint PhysicalDevice;
        public uint ComputeQueueIndex;
        public uint ComputeQueueFamily;
        public uint GraphicsQueueIndex;
        public uint GraphicsQueueFamily;
        public uint OpticalFlowQueueIndex;
        public uint OpticalFlowQueueFamily;
        public bool UseNativeOpticalFlowMode;
        public uint ComputeQueueCreateFlags;
        public uint GraphicsQueueCreateFlags;
        public uint OpticalFlowQueueCreateFlags;
    }

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInfo
        {
            public uint Size;
            public nint FilePath;
            public nint File;
            public nint KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint Size;
            public nint PolicyCallbackData;
            public nint SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public FileInfo* File;
            public uint StateAction;
            public nint StateData;
            public nint UrlReference;
            public uint ProvFlags;
            public uint UiContext;
            public nint SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(nint hwnd, Guid* action, TrustData* data);

        // 0 when the file's embedded signature verifies, the chain checked offline from the cache (no network).
        public static int Verify(string path)
        {
            var name = Marshal.StringToHGlobalUni(path);
            try
            {
                var file = new FileInfo { Size = (uint)sizeof(FileInfo), FilePath = name };
                var data = new TrustData
                {
                    Size = (uint)sizeof(TrustData),
                    UiChoice = 2, // WTD_UI_NONE
                    RevocationChecks = 0, // WTD_REVOKE_NONE
                    UnionChoice = 1, // WTD_CHOICE_FILE
                    File = &file,
                    StateAction = 0,
                    ProvFlags = 0x800, // WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                var action = GenericVerifyV2;
                return WinVerifyTrust(-1, &action, &data);
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }
    }

    // The layouts, against what the probe built with the SDK's headers printed (sllayout/probe.cpp, 2026-11-15).
    internal static void SelfTest(Action<bool, string> Check)
    {
        static long At<T>(ref T s, ref byte field) where T : unmanaged
        {
            fixed (T* p = &s)
            fixed (byte* f = &field)
            {
                return f - (byte*)p;
            }
        }

        var b = default(BaseStructure);
        Check(sizeof(BaseStructure) == 32 && At(ref b, ref Unsafe.As<ulong, byte>(ref b.StructVersion)) == 24, "Streamline's base structure is laid out as the SDK's");
        var p = default(Preferences);
        Check(sizeof(Preferences) == 144 && At(ref p, ref Unsafe.As<uint, byte>(ref p.LogLevel)) == 36
            && At(ref p, ref Unsafe.As<ulong, byte>(ref p.Flags)) == 88 && At(ref p, ref Unsafe.As<uint, byte>(ref p.Engine)) == 112
            && At(ref p, ref Unsafe.As<uint, byte>(ref p.RenderApi)) == 136, "Streamline's preferences are laid out as the SDK's");
        var r = default(FeatureRequirements);
        Check(sizeof(FeatureRequirements) == 184 && At(ref r, ref Unsafe.As<SlVersion, byte>(ref r.OsVersionDetected)) == 56
            && At(ref r, ref Unsafe.As<uint, byte>(ref r.VkNumDeviceExtensions)) == 112
            && At(ref r, ref Unsafe.As<uint, byte>(ref r.VkNumOpticalFlowQueuesRequired)) == 176, "Streamline's feature requirements are laid out as the SDK's");
        var a = default(AdapterInfo);
        Check(sizeof(AdapterInfo) == 56 && At(ref a, ref Unsafe.As<nint, byte>(ref a.VkPhysicalDevice)) == 48, "Streamline's adapter info is laid out as the SDK's");
        var v = default(VulkanInfo);
        Check(sizeof(VulkanInfo) == 96 && At(ref v, ref Unsafe.As<bool, byte>(ref v.UseNativeOpticalFlowMode)) == 80
            && At(ref v, ref Unsafe.As<uint, byte>(ref v.OpticalFlowQueueCreateFlags)) == 92, "Streamline's Vulkan info is laid out as the SDK's");
        Check(sizeof(ViewportHandle) == 40, "Streamline's viewport handle is laid out as the SDK's");
        var res = default(Resource);
        Check(sizeof(Resource) == 112 && At(ref res, ref Unsafe.As<nint, byte>(ref res.Native)) == 40
            && At(ref res, ref Unsafe.As<uint, byte>(ref res.State)) == 64 && At(ref res, ref Unsafe.As<ulong, byte>(ref res.GpuVirtualAddress)) == 88
            && At(ref res, ref Unsafe.As<uint, byte>(ref res.Usage)) == 100, "Streamline's resource is laid out as the SDK's");
        var tag = default(ResourceTag);
        Check(sizeof(ResourceTag) == 64 && At(ref tag, ref Unsafe.As<uint, byte>(ref tag.Type)) == 40
            && At(ref tag, ref Unsafe.As<Extent, byte>(ref tag.Extent)) == 48, "Streamline's resource tag is laid out as the SDK's");
        var c = default(Constants);
        Check(sizeof(Constants) == 456 && At(ref c, ref Unsafe.As<System.Numerics.Vector2, byte>(ref c.JitterOffset)) == 352
            && At(ref c, ref Unsafe.As<System.Numerics.Vector3, byte>(ref c.CameraPos)) == 376 && At(ref c, ref Unsafe.As<float, byte>(ref c.CameraNear)) == 424
            && At(ref c, ref c.DepthInverted) == 444 && At(ref c, ref c.MotionVectorsJittered) == 450
            && At(ref c, ref Unsafe.As<float, byte>(ref c.MinRelativeLinearDepthObjectSeparation)) == 452, "Streamline's constants are laid out as the SDK's");
        // DLSSDOptions: its size as the probe printed it; its offsets from sl_dlss_d.h, as DLSSOptions' are.
        var o = default(DLSSDOptions);
        Check(sizeof(DLSSDOptions) == 224 && At(ref o, ref Unsafe.As<float, byte>(ref o.PreExposure)) == 48 && At(ref o, ref o.ColorBuffersHDR) == 56
            && At(ref o, ref Unsafe.As<uint, byte>(ref o.NormalRoughnessMode)) == 60
            && At(ref o, ref Unsafe.As<System.Numerics.Matrix4x4, byte>(ref o.WorldToCameraView)) == 64
            && At(ref o, ref o.AlphaUpscalingEnabled) == 192 && At(ref o, ref Unsafe.As<uint, byte>(ref o.UltraQualityPreset)) == 216, "Streamline's Ray Reconstruction options are laid out as the SDK's");
        Check(sizeof(DLSSDOptimalSettings) == 64, "Streamline's Ray Reconstruction settings are laid out as the SDK's");
        var guid = PreferencesType.ToByteArray();
        Check(BitConverter.ToUInt32(guid, 0) == 0x1ca10965 && guid[8] == 0x8d && guid[15] == 0x14, "a System.Guid has sl::StructType's bytes");
    }
}
