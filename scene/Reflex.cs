using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

// NVIDIA Reflex through VK_NV_low_latency2 (--reflex, when the GPU offers it with present ids and timeline semaphores):
// the driver holds each frame back before its keys and mouse are read, just long enough that the GPU takes it as soon
// as it is submitted, with its clocks kept up (boost), and it reports each frame's times from the markers the scene
// sets. It paces the frames in place of the present wait (paceByScreen).
internal sealed unsafe class Reflex : IDisposable
{
    public const string Extension = "VK_NV_low_latency2";

    private readonly Device device;
    private readonly VkSemaphore woken;
    private ulong sleeps;
    private ulong reported;
    private int frames;

    // lowLatency false (--reflex-measure) sets the markers and reads the times with the mode off, to compare.
    public Reflex(Device device, bool lowLatency)
    {
        this.device = device;
        LowLatency = lowLatency;
        var timeline = new VkSemaphoreTypeCreateInfo { SemaphoreType = VkSemaphoreType.Timeline, InitialValue = 0 };
        woken = device.CreateSemaphore(new VkSemaphoreCreateInfo { Next = &timeline }, null);
    }

    public bool LowLatency { get; }

    // The cap's period when there is one (--cap, 60 when hidden), microseconds: Reflex paces to it in place of the
    // scene's own sleep after the present, which held each frame's input a frame longer (720p hidden: keys read to GPU
    // done 44 ms against 25 ms without Reflex).
    public int IntervalUs { get; set; }

    // From the keys and the mouse read to the GPU done with the frame, ms, as the driver timed it.
    public FrameStats InputToGpuDone { get; } = new();

    // From the frame's start to its present call returning, ms.
    public FrameStats StartToPresented { get; } = new();

    // How long the driver held the frames back, ms.
    public FrameStats Slept { get; } = new();

    // Each new swapchain: the low latency mode on, with boost, paced to the cap's interval if any, else by FIFO.
    public void Mode(VkSwapchainKHR swapchain)
    {
        var mode = new VkLatencySleepModeInfoNV { LowLatencyMode = LowLatency, LowLatencyBoost = LowLatency, MinimumIntervalUs = LowLatency ? IntervalUs : 0 };
        device.SetLatencySleepModeNV(swapchain, ref mode);
    }

    // Before the keys and the mouse are read: held back as long as the driver asks, a tenth of a second at most.
    public void Sleep(VkSwapchainKHR swapchain)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var value = ++sleeps;
        var info = new VkLatencySleepInfoNV { SignalSemaphore = woken, Value = (nint)value };
        device.LatencySleepNV(swapchain, ref info);
        var semaphore = woken;
        var wait = new VkSemaphoreWaitInfo { SemaphoreCount = 1, Semaphores = &semaphore, Values = (nint*)&value };
        try
        {
            device.WaitSemaphores(ref wait, 100_000_000);
        }
        catch (VkResultError)
        {
            // A sleep that does not end is no reason to stop drawing.
        }

        Slept.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public void Mark(VkSwapchainKHR swapchain, long presentId, VkLatencyMarkerNV marker)
    {
        var info = new VkSetLatencyMarkerInfoNV { PresentID = (nint)presentId, Marker = marker };
        device.SetLatencyMarkerNV(swapchain, ref info);
    }

    // Every 30 frames, the driver's report of the frames since the last one read (it keeps the last 64).
    public void Read(VkSwapchainKHR swapchain)
    {
        if (++frames % 30 != 0)
        {
            return;
        }

        var count = new VkGetLatencyMarkerInfoNV();
        device.GetLatencyTimingsNV(swapchain, ref count);
        if (count.TimingCount <= 0)
        {
            return;
        }

        var timings = new VkLatencyTimingsFrameReportNV[count.TimingCount];
        fixed (VkLatencyTimingsFrameReportNV* first = timings)
        {
            var info = new VkGetLatencyMarkerInfoNV { TimingCount = timings.Length, Timings = first };
            device.GetLatencyTimingsNV(swapchain, ref info);
            for (var i = 0; i < info.TimingCount; i++)
            {
                var t = first[i];
                var id = (ulong)t.PresentID;
                if (id <= reported)
                {
                    continue;
                }

                reported = id;
                if (t.InputSampleTimeUs > 0 && t.GpuRenderEndTimeUs > t.InputSampleTimeUs)
                {
                    InputToGpuDone.Add((t.GpuRenderEndTimeUs - t.InputSampleTimeUs) / 1000.0);
                }

                if (t.SimStartTimeUs > 0 && t.PresentEndTimeUs > t.SimStartTimeUs)
                {
                    StartToPresented.Add((t.PresentEndTimeUs - t.SimStartTimeUs) / 1000.0);
                }
            }
        }
    }

    public string Measured()
    {
        static string Of(FrameStats s) => s.Count == 0 ? "none" : $"p50 {s.Percentile(0.5):F2} p95 {s.Percentile(0.95):F2}";
        return $"{(LowLatency ? "low latency mode" : "markers only, low latency mode off")}: keys read to GPU done ms {Of(InputToGpuDone)} over {InputToGpuDone.Count} frames; start to presented ms {Of(StartToPresented)}; slept ms {Of(Slept)}";
    }

    public void Dispose() => device.DestroySemaphore(woken, null);
}
