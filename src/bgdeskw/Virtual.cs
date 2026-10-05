using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace BgDesk;

/// <summary>
/// Input that works on a layer without its turn at the one pointer and keyboard of the session: messages
/// sent to windows directly. Several layers can therefore be worked on at the same moment. Not every program
/// honours this kind of input, so callers only use it where it is known to work. Also the window queries that
/// the layer code shares; they only see the calling thread's tile. Everything here runs on a layer's own thread.
/// </summary>
static class Virtual
{
    const uint WM_NCHITTEST = 0x0084, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;
    const int HTCLIENT = 1;

    /// <summary>
    /// In canvas mode, the tile of the layer this thread works for. Everything here that looks at windows
    /// then only sees the ones in that tile, as if the tile were the whole screen. Empty otherwise.
    /// </summary>
    [ThreadStatic] public static Rectangle Scope;

    /// <summary>True when the window lies (at least partly) in this thread's tile, or when there is no tile.</summary>
    public static bool InScope(IntPtr window) => Scope.IsEmpty || Frame(window).IntersectsWith(Scope);
    /// <summary>The visible top-level window at a point of the layer, topmost first, as a person would hit it.</summary>
    public static IntPtr TopLevelAt(Point p)
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!Shown(window) || !Frame(window).Contains(p)) return true;
            // click-through overlays are not what the user would hit
            if ((GetWindowLongPtr(window, -20).ToInt64() & 0x20) != 0) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The innermost child window at a point, which is where a mouse message has to be delivered.</summary>
    public static IntPtr Deepest(IntPtr top, Point screen)
    {
        var current = top;
        while (true)
        {
            var client = screen;
            ScreenToClient(current, ref client);
            var child = ChildWindowFromPointEx(current, client, 0x0001 | 0x0002 | 0x0004);
            if (child == IntPtr.Zero || child == current) return current;
            current = child;
        }
    }

    public static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    /// <summary>True when the point is in the window's client area, where plain mouse messages apply.</summary>
    public static bool InClientArea(IntPtr window, Point screen) =>
        SendMessageTimeout(window, WM_NCHITTEST, IntPtr.Zero, Pack(screen.X, screen.Y), 0x0002, 300, out var hit) != IntPtr.Zero && hit.ToInt64() == HTCLIENT;

    /// <summary>
    /// Makes a window the active one of its own thread, the way a first click on it would, without the
    /// desktop being in front. Programs decide where a click or key press goes by whether they are active,
    /// so without this a click lands but focus and later real key presses do not follow it.
    /// </summary>
    public static void Activate(IntPtr top)
    {
        var theirs = GetWindowThreadProcessId(top, out _);
        var mine = GetCurrentThreadId();
        if (theirs == 0 || theirs == mine) return;
        // AttachThreadInput needs this thread to have a message queue; asking for a message creates one.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        if (!AttachThreadInput(mine, theirs, true)) return;
        try
        {
            BringWindowToTop(top);
            SetActiveWindow(top);
        }
        finally { AttachThreadInput(mine, theirs, false); }
    }

    /// <summary>
    /// The application window that is on top of the layer: the one real keyboard input would go to if the
    /// layer were in front. Shell surfaces, overlays and title-less pop-ups do not count.
    /// </summary>
    public static IntPtr TopOfZOrder()
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!Shown(window)) return true;
            var ex = GetWindowLongPtr(window, -20).ToInt64();
            if ((ex & 0x08000000) != 0) return true;                                   // never takes the focus
            if (ClassOf(window) is "Shell_TrayWnd" or "Progman" or "WorkerW" or "tooltips_class32") return true;
            if (GetWindow(window, 4) != IntPtr.Zero && (GetWindowLongPtr(window, -16).ToInt64() & 0x00C00000) != 0x00C00000) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The window that should have the focus now, given that this one was the last one worked in: a
    /// dialog it owns, another window of the same program that has come up on top of it, or itself.
    /// </summary>
    public static IntPtr Successor(IntPtr window, Func<IntPtr, bool>? startedByLayer = null)
    {
        if (!IsWindow(window)) return TopOfZOrder();
        var dialog = GetLastActivePopup(window);
        if (dialog != IntPtr.Zero && dialog != window && IsWindowVisible(dialog)) return dialog;
        var onTop = TopOfZOrder();
        if (onTop != IntPtr.Zero && onTop != window)
        {
            GetWindowThreadProcessId(onTop, out var theirs);
            GetWindowThreadProcessId(window, out var ours);
            if (theirs == ours) return onTop;
            // A program the agent started after working in this window opened above it; that one is the newest
            // work. Windows nobody on the layer started (a task's console, say) do not take the front over.
            if (startedByLayer?.Invoke(onTop) == true) return onTop;
        }
        return window;
    }

    /// <summary>The windows of the tile that have a taskbar button, topmost first. Always-on-top windows are not part of the ordinary stacking and are left out.</summary>
    public static List<IntPtr> AppWindows()
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (!Shown(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(window, -20).ToInt64() & (0x80 | 0x08000000 | 0x8)) != 0) return true;   // tool window, never takes the focus, always on top
            if (ClassOf(window) is "Shell_TrayWnd" or "Progman" or "WorkerW" or "tooltips_class32") return true;
            windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>
    /// Sets a window's place in the stacking order (after <paramref name="after"/>, or on top for zero) without
    /// activating it. Done from the thread that owns the window, as far as that can be arranged, since Windows
    /// is stricter with a window whose thread has the active state than with one it can reach directly.
    /// </summary>
    public static bool Place(IntPtr window, IntPtr after)
    {
        const uint flags = 0x0001 | 0x0002 | 0x0010;            // no size, no move, no activation
        var theirs = GetWindowThreadProcessId(window, out _);
        var mine = GetCurrentThreadId();
        var attached = theirs != 0 && theirs != mine && (PeekMessage(out _, IntPtr.Zero, 0, 0, 0) || true) && AttachThreadInput(mine, theirs, true);
        try
        {
            var ok = SetWindowPos(window, after, 0, 0, 0, 0, flags);
            // Some windows (the one that is active, a shell window) do not give way to a plain raise. Making a window
            // topmost and then ordinary again puts it above every ordinary window, whatever their state.
            if (after == IntPtr.Zero) { SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, flags); SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, flags); }
            return ok;
        }
        finally { if (attached) AttachThreadInput(mine, theirs, false); }
    }

    /// <summary>A left click, double click or triple click as messages, with Ctrl and Shift held when asked.</summary>
    public static void Click(IntPtr target, Point screen, int count = 1, bool ctrl = false, bool shift = false)
    {
        var client = screen;
        ScreenToClient(target, ref client);
        var where = Pack(client.X, client.Y);
        var held = (ctrl ? 0x08 : 0) | (shift ? 0x04 : 0);               // MK_CONTROL, MK_SHIFT
        WithModifierState(target, ctrl, shift, () =>
        {
            PostMessage(target, WM_MOUSEMOVE, new IntPtr(held), where);
            for (var i = 0; i < count; i++)
            {
                // the second press of a double click arrives as WM_LBUTTONDBLCLK
                PostMessage(target, i == 1 ? 0x0203u : WM_LBUTTONDOWN, new IntPtr(1 | held), where);
                Thread.Sleep(30);
                PostMessage(target, WM_LBUTTONUP, new IntPtr(held), where);
                if (i + 1 < count) Thread.Sleep(40);
            }
        });
    }

    /// <summary>Moves the pointer over a window as a message, so that hover effects happen, without the real pointer.</summary>
    public static void Hover(IntPtr target, Point screen)
    {
        var client = screen;
        ScreenToClient(target, ref client);
        PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, Pack(client.X, client.Y));
    }

    /// <summary>
    /// Runs something that posts messages to a window with Ctrl and/or Shift shown as held in that window's input
    /// state (see <see cref="Chord"/>), and puts the state back once the window has handled the messages.
    /// </summary>
    static void WithModifierState(IntPtr dest, bool ctrl, bool shift, Action post)
    {
        var theirs = GetWindowThreadProcessId(dest, out _);
        var mine = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        var attached = (ctrl || shift) && theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
        var saved = new byte[256];
        try
        {
            if (attached)
            {
                GetKeyboardState(saved);
                var state = (byte[])saved.Clone();
                if (ctrl) state[0x11] = 0x80;
                if (shift) state[0x10] = 0x80;
                SetKeyboardState(state);
            }
            post();
            SendMessageTimeout(dest, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 800, out _);
        }
        finally
        {
            if (attached) { SetKeyboardState(saved); AttachThreadInput(mine, theirs, false); }
        }
    }

    public static void Scroll(IntPtr target, Point screen, string direction, int amount)
    {
        var (message, delta) = direction switch
        {
            "up" => (WM_MOUSEWHEEL, 120), "down" => (WM_MOUSEWHEEL, -120), "left" => (WM_MOUSEHWHEEL, -120), "right" => (WM_MOUSEHWHEEL, 120),
            _ => throw new ArgumentException("direction must be up, down, left or right"),
        };
        for (var i = 0; i < amount; i++)
        {
            // wheel messages carry screen coordinates
            PostMessage(target, message, new IntPtr((long)delta << 16), Pack(screen.X, screen.Y));
            Thread.Sleep(15);
        }
    }

    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);

    /// <summary>
    /// Presses one key, with modifiers, as messages to a window, without the real keyboard. A program looks at the
    /// state of Ctrl, Shift and Alt when it handles a key; that state belongs to its thread's input queue, so this
    /// thread joins the queue for the moment, sets the modifiers in it, and puts it back after the program has had the
    /// messages (a message sent to the window comes back only when everything posted before it has been handled).
    /// </summary>
    public static void Chord(IntPtr dest, ushort vk, bool ctrl, bool shift, bool alt)
    {
        var theirs = GetWindowThreadProcessId(dest, out _);
        var mine = GetCurrentThreadId();
        var modifiers = new List<ushort>();
        if (ctrl) modifiers.Add(0x11);
        if (shift) modifiers.Add(0x10);
        if (alt) modifiers.Add(0x12);
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        var attached = modifiers.Count > 0 && theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
        var saved = new byte[256];
        var changed = false;
        try
        {
            if (attached)
            {
                GetKeyboardState(saved);
                var state = (byte[])saved.Clone();
                foreach (var m in modifiers) state[m] = 0x80;
                SetKeyboardState(state);
                changed = true;
            }
            var extended = vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x21 or 0x22 or 0x23 or 0x24 or 0x2D or 0x2E ? 1L << 24 : 0;
            var context = alt ? 1L << 29 : 0;                          // "Alt is held"
            var down = 1L | ((long)MapVirtualKey(vk, 0) << 16) | extended | context;
            var up = down | (1L << 30) | (1L << 31);
            uint keyDown = alt ? 0x0104u : WM_KEYDOWN, keyUp = alt ? 0x0105u : WM_KEYUP;
            foreach (var m in modifiers) PostMessage(dest, WM_KEYDOWN, new IntPtr(m), new IntPtr(1L | ((long)MapVirtualKey(m, 0) << 16)));
            PostMessage(dest, keyDown, new IntPtr(vk), new IntPtr(down));
            Thread.Sleep(20);
            PostMessage(dest, keyUp, new IntPtr(vk), new IntPtr(up));
            foreach (var m in Enumerable.Reverse(modifiers)) PostMessage(dest, WM_KEYUP, new IntPtr(m), new IntPtr(1L | ((long)MapVirtualKey(m, 0) << 16) | (1L << 30) | (1L << 31)));
            // Everything posted so far has been handled when this comes back.
            SendMessageTimeout(dest, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 800, out _);
        }
        finally
        {
            if (changed) SetKeyboardState(saved);
            if (attached) AttachThreadInput(mine, theirs, false);
        }
    }
    /// <summary>The window that has the keyboard focus in the thread owning the given window, or the window itself.</summary>
    public static IntPtr FocusOf(IntPtr window)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) || info.hwndFocus == IntPtr.Zero) return window;
        // A program's windows share one thread and so one focus. If it is in another window of the program than
        // the one asked about, it is not where typing for this window belongs.
        return GetAncestor(info.hwndFocus, 2 /* GA_ROOT */) == GetAncestor(window, 2) ? info.hwndFocus : window;
    }

    public static void Type(IntPtr focus, string text)
    {
        foreach (var ch in text.ReplaceLineEndings("\n"))
        {
            if (ch == '\n') { Key(focus, 0x0D); continue; }
            if (ch == '\t') { Key(focus, 0x09); continue; }
            PostMessage(focus, WM_CHAR, new IntPtr(ch), new IntPtr(1));
            Thread.Sleep(4);
        }
    }

    public static void Key(IntPtr focus, ushort vk)
    {
        var scan = MapVirtualKey(vk, 0);
        var down = 1L | ((long)scan << 16);
        PostMessage(focus, WM_KEYDOWN, new IntPtr(vk), new IntPtr(down));
        // Enter, Tab and the like also arrive as characters when a real keyboard is used
        // Programs turn the key into a character themselves (TranslateMessage); a second one here would be typed twice.
        Thread.Sleep(15);
        PostMessage(focus, WM_KEYUP, new IntPtr(vk), new IntPtr(down | (1L << 30) | (1L << 31)));
    }

    /// <summary>The pixels of one window as drawn right now, or null when it cannot be printed. For telling whether an action showed.</summary>
    public static byte[]? Snapshot(IntPtr window)
    {
        try { return SnapshotCore(window); }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or ArgumentException or InvalidOperationException) { return null; }
    }

    static byte[]? SnapshotCore(IntPtr window)
    {
        if (!GetWindowRect(window, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return null;
        using var shot = new Bitmap(r.Right - r.Left, r.Bottom - r.Top, PixelFormat.Format32bppArgb);
        bool ok;
        using (var g = Graphics.FromImage(shot)) { var dc = g.GetHdc(); ok = PrintWindow(window, dc, 2); g.ReleaseHdc(dc); }
        if (!ok) return null;
        var data = shot.LockBits(new Rectangle(0, 0, shot.Width, shot.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { shot.UnlockBits(data); }
    }

    /// <summary>True when two snapshots of the same window differ in more than a few pixels; null when they cannot be compared.</summary>
    public static bool? Differs(byte[]? before, byte[]? after)
    {
        if (before is null || after is null || before.Length != after.Length) return null;
        var pixels = 0;
        for (var i = 0; i + 3 < before.Length; i += 4)
            if (Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1]) + Math.Abs(before[i + 2] - after[i + 2]) > 24 && ++pixels >= 4) return true;
        return false;
    }

    static bool Shown(IntPtr window)
    {
        if (!IsWindowVisible(window) || IsIconic(window)) return false;
        // "cloaked" windows are kept by Windows but not shown (other virtual desktops, suspended apps)
        if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return GetWindowRect(window, out var r) && r.Right > r.Left && r.Bottom > r.Top && InScope(window);
    }

    public static Rectangle FrameOf(IntPtr window) => Frame(window);

    /// <summary>The rectangle a window visibly occupies.</summary>
    static Rectangle Frame(IntPtr window)
    {
        if (DwmGetWindowAttribute(window, 9, out RECT f, Marshal.SizeOf<RECT>()) == 0 && f.Right > f.Left)
            return Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom);
        GetWindowRect(window, out var r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    static IntPtr Pack(int x, int y) => new((long)(((uint)(ushort)(short)y << 16) | (ushort)(short)x));

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int left, top, right, bottom;
    }

    delegate bool EnumProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Point pt; }
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent, Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RECT value, int size);
}
