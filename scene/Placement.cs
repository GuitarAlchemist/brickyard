using System.Collections.Concurrent;
using Brutal.GlfwApi;
using Brutal.Numerics;

// What the brain asks of Delta's window: a corner or a side of its screen, maximised, full screen, minimised, hidden in
// the notification area, kept on top, smaller or larger. The link's threads queue the actions; the render loop runs
// them, since GLFW is called from its thread only.
internal sealed class Placement
{
    private const int Margin = 12;
    private readonly ConcurrentQueue<string> pending = new();
    private int2 windowedPosition, windowedSize;
    private bool fullscreen;

    public static readonly string[] Actions =
    [
        "maximize", "fullscreen", "minimize", "restore", "tray", "show", "on-top", "not-on-top", "smaller", "larger", "center",
        "top-left", "top-right", "bottom-left", "bottom-right", "left", "right", "top", "bottom",
    ];

    // Whether the window is hidden by the clock, where the scene draws nothing.
    public bool Hidden { get; private set; }

    public void Ask(string action) => pending.Enqueue(action);

    // Runs what was asked since the last frame.
    public void Apply(GlfwWindow window, Tray? tray)
    {
        while (pending.TryDequeue(out var action))
        {
            Console.WriteLine($"SCENE window {action}");
            Run(window, tray, action);
        }
    }

    private void Run(GlfwWindow window, Tray? tray, string action)
    {
        switch (action)
        {
            case "maximize":
                Windowed(window);
                window.Maximize();
                return;
            case "fullscreen":
                if (!fullscreen)
                {
                    windowedPosition = window.Position;
                    windowedSize = window.Size;
                    var monitor = MonitorOf(window);
                    var mode = monitor.VideoMode;
                    window.SetFullscreen(monitor, new int2(mode.Width, mode.Height), mode.RefreshRate);
                    fullscreen = true;
                }

                return;
            case "minimize":
                window.Iconify();
                return;
            case "restore":
                Windowed(window);
                window.Restore();
                return;
            case "tray":
                tray?.Show("Delta : clique pour me revoir");
                window.Visible = false;
                Hidden = true;
                return;
            case "show":
                tray?.Hide();
                window.Visible = true;
                Hidden = false;
                if (window.IsIconified)
                {
                    window.Restore();
                }

                window.Focus();
                return;
            case "on-top":
                window.SetAttribute(GlfwWindowAttribute.Floating, 1);
                return;
            case "not-on-top":
                window.SetAttribute(GlfwWindowAttribute.Floating, 0);
                return;
            case "smaller" or "larger":
                Windowed(window);
                window.Restore();
                var scale = action == "smaller" ? 0.8f : 1.25f;
                MonitorOf(window).GetMonitorWorkArea(out _, out var area);
                var size = window.Size;
                var width = Math.Clamp((int)(size.X * scale), 320, area.X);
                window.Size = new int2(width, Math.Clamp(width * size.Y / Math.Max(1, size.X), 180, area.Y));
                return;
        }

        // A place: a corner, a side or the centre of the screen the window is on.
        Windowed(window);
        window.Restore();
        var screen = MonitorOf(window);
        screen.GetMonitorWorkArea(out var at, out var room);
        window.GetWindowFrameSize(out var left, out var top, out var right, out var bottom);
        var outer = new int2(window.Width + left + right, window.Height + top + bottom);
        var x = action.Contains("left") ? at.X + Margin : action.Contains("right") ? at.X + room.X - outer.X - Margin : at.X + ((room.X - outer.X) / 2);
        var y = action.StartsWith("top") ? at.Y + Margin : action.StartsWith("bottom") ? at.Y + room.Y - outer.Y - Margin : at.Y + ((room.Y - outer.Y) / 2);
        window.Position = new int2(x + left, y + top);
    }

    // Leaves full screen for the window it was in.
    private void Windowed(GlfwWindow window)
    {
        if (fullscreen)
        {
            window.SetWindowed(windowedPosition, windowedSize);
            fullscreen = false;
        }
    }

    // The screen that holds the window's centre, or the primary one.
    private static GlfwMonitor MonitorOf(GlfwWindow window)
    {
        var centre = window.Position + (window.Size / 2);
        foreach (var monitor in Glfw.GetMonitors())
        {
            monitor.GetMonitorWorkArea(out var at, out var size);
            if (centre.X >= at.X && centre.X < at.X + size.X && centre.Y >= at.Y && centre.Y < at.Y + size.Y)
            {
                return monitor;
            }
        }

        return Glfw.PrimaryMonitor;
    }
}
