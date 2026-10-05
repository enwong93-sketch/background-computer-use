using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace BgDesk;

/// <summary>
/// Canvas mode keeps every layer's windows inside its tile. Windows are watched as they appear, move, get
/// maximized or minimized, and are put back into the tile they belong to. A window belongs to the layer whose
/// job its process is in, to the layer of the window that owns it, to a layer that has just asked for a window
/// that another process makes (a folder, a document, a Store app), or else to the tile it is in.
/// Menus, lists and tooltips are left alone: they belong to a window and open next to it.
/// </summary>
static class Containment
{
    static readonly ConcurrentDictionary<IntPtr, string> Owner = new();
    static readonly ConcurrentDictionary<IntPtr, (long Since, int Moves)> Moves = new();
    static WinEventProc? _callback;

    public static void Start()
    {
        new Thread(Loop) { IsBackground = true, Name = "containment" }.Start();
        new Thread(SweepSessionStart) { IsBackground = true, Name = "start sweep" }.Start();
    }

    /// <summary>
    /// Windows starts its registered accessibility helpers when a session begins; the Java Access Bridge's one
    /// fails in a session like this and puts an error box on the canvas, over whichever agent's tile it lands in.
    /// Nothing here needs it. In the first minutes, end it and close error boxes that nobody on the canvas owns.
    /// </summary>
    static void SweepSessionStart()
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        while (Environment.TickCount64 - Started < 180_000)
        {
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("jabswitch"))
                {
                    try { if (process.SessionId == session) process.Kill(); } catch { }
                    process.Dispose();
                }
                EnumWindows((window, _) => { try { CloseIfStrayError(window); } catch { } return true; }, IntPtr.Zero);
            }
            catch { }
            Thread.Sleep(500);
        }
    }

    /// <summary>A system error box ("... - Application Error") that no layer's program put up.</summary>
    static void CloseIfStrayError(IntPtr window)
    {
        if (!IsWindowVisible(window) || Owner.ContainsKey(window) || Virtual.ClassOf(window) != "#32770") return;
        var title = new System.Text.StringBuilder(160);
        GetWindowText(window, title, title.Capacity);
        if (!title.ToString().EndsWith("- Application Error", StringComparison.OrdinalIgnoreCase)) return;
        if (InAnyJob(window, out _)) return;
        PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);                     // WM_CLOSE
    }

    /// <summary>A window that the layer's launch brought up, made by a process that is not the layer's own.</summary>
    public static void Claim(IntPtr window, string key)
    {
        Owner[window] = key;
        try { Check(window, EventShow); } catch { }
    }

    /// <summary>True when the window's process was started on some layer.</summary>
    public static bool InAnyJob(IntPtr window, out string? key)
    {
        key = null;
        GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(0x1000, false, pid);                           // query limited information
        if (process == IntPtr.Zero) return false;
        try
        {
            foreach (var (k, entry) in Layers.Tiles)
                if (IsProcessInJob(process, entry.Job, out var inJob) && inJob) { key = k; return true; }
        }
        finally { CloseHandle(process); }
        return false;
    }

    static readonly long Started = Environment.TickCount64;
    static readonly ConcurrentQueue<(string Key, long Until)> Expected = new();

    /// <summary>A launch whose window did not come in time: the next window nobody owns is this layer's, for a while.</summary>
    public static void ExpectOne(string key) => Expected.Enqueue((key, Environment.TickCount64 + 30_000));

    public static void Forget(string key)
    {
        foreach (var pair in Owner) if (pair.Value == key) Owner.TryRemove(pair.Key, out _);
    }

    static void Loop()
    {
        _callback = OnEvent;
        // Programs that Windows starts at every logon (scheduled tasks) put up consoles on the canvas before
        // anyone works there. They are second copies of things the user already runs; close them.
        EnumWindows((window, _) => { try { CloseIfStrayConsole(window); } catch { } return true; }, IntPtr.Zero);
        const uint outOfContext = 0x0000, skipOwnProcess = 0x0002;
        SetWinEventHook(EventShow, EventShow, IntPtr.Zero, _callback, 0, 0, outOfContext | skipOwnProcess);
        SetWinEventHook(EventLocationChange, EventLocationChange, IntPtr.Zero, _callback, 0, 0, outOfContext | skipOwnProcess);
        SetWinEventHook(EventMinimizeStart, EventMinimizeStart, IntPtr.Zero, _callback, 0, 0, outOfContext | skipOwnProcess);
        SetWinEventHook(EventDestroy, EventDestroy, IntPtr.Zero, _callback, 0, 0, outOfContext | skipOwnProcess);
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    const uint EventShow = 0x8002, EventLocationChange = 0x800B, EventMinimizeStart = 0x0016, EventDestroy = 0x8001;

    static void OnEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != 0 || childId != 0 || window == IntPtr.Zero) return;
        // Windows reuses the handle of a window that is gone; what was known about it must not stick to the next one.
        if (eventType == EventDestroy) { Owner.TryRemove(window, out _); Moves.TryRemove(window, out _); return; }
        try { Check(window, eventType); } catch { }
    }

    static void Check(IntPtr window, uint eventType)
    {
        if (GetAncestor(window, 2) != window || !IsWindowVisible(window)) return;
        if (Virtual.ClassOf(window) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "tooltips_class32" or "#32768"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow"
            or "ForegroundStaging" or "MultitaskingViewFrame" or "TaskListThumbnailWnd" or "Xaml_WindowedPopupClass") return;
        var style = GetWindowLongPtr(window, -16).ToInt64();
        var ex = GetWindowLongPtr(window, -20).ToInt64();
        var captioned = (style & 0x00C00000) == 0x00C00000;
        var owned = GetWindow(window, 4) != IntPtr.Zero;
        if ((ex & 0x08000000) != 0) return;                                        // never takes the focus: overlays, indicators
        if (!captioned && owned && Virtual.ClassOf(window) != "#32770") return;     // menus, lists, bubbles: they follow their window
        if (!captioned && (ex & 0x80) != 0) return;                                 // title-less tool windows
        if (CloseIfStrayConsole(window)) return;
        if (OwnerOf(window) is not { } key || !Layers.Tiles.TryGetValue(key, out var entry)) return;

        // There is no taskbar in a tile to bring a minimized window back from.
        if (eventType == EventMinimizeStart || IsIconic(window)) { ShowWindowAsync(window, 4); return; }
        Contain(window, entry.Tile);
    }

    static string? OwnerOf(IntPtr window)
    {
        if (Owner.TryGetValue(window, out var known) && Layers.Tiles.ContainsKey(known)) return known;
        var owner = GetWindow(window, 4);
        if (owner != IntPtr.Zero && Owner.TryGetValue(owner, out var ownersLayer)) return Owner[window] = ownersLayer;

        if (InAnyJob(window, out var jobLayer)) return Owner[window] = jobLayer!;

        // A window a launch was still waiting for.
        while (Expected.TryDequeue(out var expected))
            if (expected.Until > Environment.TickCount64 && Layers.Tiles.ContainsKey(expected.Key)) return Owner[window] = expected.Key;

        // A window that comes up by itself mostly answers what an agent just did (a folder opened from a folder
        // window, a dialog of the shell): it belongs to the layer that last used real input, if that was just now.
        if (Layers.LastInput is { } recent && Environment.TickCount64 - recent.At < 10_000 && Layers.Tiles.ContainsKey(recent.Key))
            return Owner[window] = recent.Key;

        // Otherwise the tile it is in, if any.
        var frame = Virtual.FrameOf(window);
        var middle = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        foreach (var (key, entry) in Layers.Tiles)
            if (entry.Tile.Contains(middle)) return Owner[window] = key;
        return null;
    }

    /// <summary>A console nobody on the canvas started, in the first minutes of the session: closed.</summary>
    static bool CloseIfStrayConsole(IntPtr window)
    {
        if (Environment.TickCount64 - Started > 180_000 || !IsWindowVisible(window) || Owner.ContainsKey(window)) return false;
        if (Virtual.ClassOf(window) is not ("ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS")) return false;
        if (InAnyJob(window, out _)) return false;
        PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);                     // WM_CLOSE
        return true;
    }

    /// <summary>Moves (and if need be shrinks) a window so that it lies inside the tile. A maximized window fills the tile.</summary>
    static void Contain(IntPtr window, Rectangle tile)
    {
        var frame = Virtual.FrameOf(window);
        var inside = Rectangle.Inflate(tile, 2, 2);
        if (!IsZoomed(window) && inside.Contains(frame)) return;

        // A program that keeps putting its window back would make this run without end.
        var now = Environment.TickCount64;
        var record = Moves.AddOrUpdate(window, _ => (now, 1), (_, r) => now - r.Since > 10_000 ? (now, 1) : (r.Since, r.Moves + 1));
        if (record.Moves > 20) return;

        // The window rectangle includes invisible borders around the visible frame.
        GetWindowRect(window, out var outer);
        int left = frame.Left - outer.Left, top = frame.Top - outer.Top, right = outer.Right - frame.Right, bottom = outer.Bottom - frame.Bottom;

        if (IsZoomed(window))
        {
            // Maximized means the whole screen, which is the whole canvas. Make it fill the tile instead: give it the
            // tile as its normal place and show it there, without activating it (that would take another tile's keyboard).
            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(window, ref placement)) return;
            placement.flags = 0;
            placement.showCmd = 4;                                                     // SW_SHOWNOACTIVATE
            placement.rcNormalPosition = new RECT { Left = tile.Left - left, Top = tile.Top - top, Right = tile.Right + right, Bottom = tile.Bottom + bottom };
            SetWindowPlacement(window, ref placement);
            return;
        }

        Rectangle target;
        {
            // A position given for the whole screen (a program told to open at 100,60) is taken as one inside the tile.
            var shifted = new Rectangle(frame.X + tile.X, frame.Y + tile.Y, frame.Width, frame.Height);
            if (tile.Contains(shifted)) target = shifted;
            else
            {
                var width = Math.Min(frame.Width, tile.Width);
                var height = Math.Min(frame.Height, tile.Height);
                target = new Rectangle(Math.Clamp(frame.X, tile.Left, tile.Right - width), Math.Clamp(frame.Y, tile.Top, tile.Bottom - height), width, height);
            }
        }

        var sameSize = target.Width == frame.Width && target.Height == frame.Height;
        SetWindowPos(window, IntPtr.Zero, target.X - left, target.Y - top, target.Width + left + right, target.Height + top + bottom,
            0x0004 | 0x0010 | (sameSize ? 0x0001u : 0));                            // no z-order change, no activation
    }

    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Point pt; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int length, flags, showCmd; public Point ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }

    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

    delegate bool EnumProc(IntPtr window, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
