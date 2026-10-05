using System.Runtime.InteropServices;

namespace BgDesk;

/// <summary>Real SendInput on the agent desktop, so every app sees ordinary hardware-style input.</summary>
static class Input
{
    const uint MOVE = 0x1, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000, WHEEL = 0x800, HWHEEL = 0x1000;
    const uint KEYUP = 0x2, EXTENDED = 0x1, UNICODE = 0x4;

    static readonly Dictionary<string, (uint down, uint up)> Buttons = new()
    {
        ["left"] = (0x2, 0x4), ["right"] = (0x8, 0x10), ["middle"] = (0x20, 0x40),
    };

    static readonly Dictionary<string, ushort> Vk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B, ["cmd"] = 0x5B, ["super"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["capslock"] = 0x14, ["printscreen"] = 0x2C, ["menu"] = 0x5D, ["plus"] = 0xBB, ["minus"] = 0xBD,
    };

    /// <summary>Checked before every key press of the current operation (canvas mode: the keyboard must be in the tile).</summary>
    [ThreadStatic] public static Action? KeyGuard;

    static readonly HashSet<ushort> ExtendedKeys = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x2C];

    public static void Move(int x, int y)
    {
        var vs = SystemInformation.VirtualScreen;
        var nx = (int)Math.Round((x) * 65535.0 / Math.Max(1, vs.Width - 1));
        var ny = (int)Math.Round((y) * 65535.0 / Math.Max(1, vs.Height - 1));
        Mouse(nx, ny, 0, MOVE | ABSOLUTE | VIRTUALDESK);
    }

    public static void Click(int x, int y, string button, int count, string? modifiers)
    {
        var b = Buttons[button];
        Move(x, y);
        Thread.Sleep(30);
        WithModifiers(modifiers, () =>
        {
            for (var i = 0; i < count; i++)
            {
                Mouse(0, 0, 0, b.down);
                Thread.Sleep(25);
                Mouse(0, 0, 0, b.up);
                if (i + 1 < count) Thread.Sleep(60);
            }
        });
    }

    /// <summary>Press at the first point, glide through every waypoint, release at the last. Works across windows.</summary>
    public static void Drag(List<Point> path, string button, string? modifiers)
    {
        var b = Buttons[button];
        Move(path[0].X, path[0].Y);
        Thread.Sleep(60);
        WithModifiers(modifiers, () =>
        {
            Mouse(0, 0, 0, b.down);
            Thread.Sleep(120);
            for (var i = 1; i < path.Count; i++)
            {
                Point from = path[i - 1], to = path[i];
                var steps = Math.Clamp((int)(Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)) / 12), 8, 80);
                for (var s = 1; s <= steps; s++)
                {
                    Move(from.X + (to.X - from.X) * s / steps, from.Y + (to.Y - from.Y) * s / steps);
                    Thread.Sleep(10);
                }
            }
            // Drop targets need a moment of hover before the release.
            Thread.Sleep(250);
            Mouse(0, 0, 0, b.up);
        });
    }

    public static void Scroll(int x, int y, string direction, int amount)
    {
        Move(x, y);
        Thread.Sleep(30);
        for (var i = 0; i < amount; i++)
        {
            switch (direction)
            {
                case "up": Mouse(0, 0, 120, WHEEL); break;
                case "down": Mouse(0, 0, -120, WHEEL); break;
                case "left": Mouse(0, 0, -120, HWHEEL); break;
                case "right": Mouse(0, 0, 120, HWHEEL); break;
                default: throw new ArgumentException("direction must be up, down, left or right");
            }
            Thread.Sleep(15);
        }
    }

    /// <summary>Chords separated by spaces, e.g. "ctrl+a ctrl+c" or "alt+f4".</summary>
    public static void Keys(string keys)
    {
        foreach (var chord in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var vks = chord.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(Resolve).ToList();
            foreach (var vk in vks) { Key(vk, false); Thread.Sleep(8); }
            Thread.Sleep(25);
            foreach (var vk in Enumerable.Reverse(vks)) { Key(vk, true); Thread.Sleep(8); }
            Thread.Sleep(30);
        }
    }

    public static void Type(string text)
    {
        foreach (var ch in text.ReplaceLineEndings("\n"))
        {
            if (ch == '\n') { Key(0x0D, false); Key(0x0D, true); }
            else if (ch == '\t') { Key(0x09, false); Key(0x09, true); }
            else { Unicode(ch, false); Unicode(ch, true); }
            Thread.Sleep(4);
        }
    }

    /// <summary>An input event with no effect in any app (an unassigned virtual key).</summary>
    public static void Nudge()
    {
        Key(0xE8, false);
        Key(0xE8, true);
    }

    static void WithModifiers(string? modifiers, Action action)
    {
        var vks = (modifiers ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries).Select(Resolve).ToList();
        foreach (var vk in vks) Key(vk, false);
        try { action(); }
        finally { foreach (var vk in Enumerable.Reverse(vks)) Key(vk, true); }
    }

    /// <summary>The virtual-key code for a key name (a letter, a digit, F1 to F24, enter, esc, and so on); false when unknown.</summary>
    public static bool TryResolve(string name, out ushort vk)
    {
        try { vk = Resolve(name); return true; }
        catch (ArgumentException) { vk = 0; return false; }
    }

    static ushort Resolve(string name)
    {
        if (Vk.TryGetValue(name, out var vk)) return vk;
        if (name.Length > 1 && (name[0] == 'f' || name[0] == 'F') && int.TryParse(name[1..], out var n) && n is >= 1 and <= 24)
            return (ushort)(0x70 + n - 1);
        if (name.Length == 1)
        {
            var scan = VkKeyScan(char.ToLowerInvariant(name[0]));
            if (scan != -1) return (ushort)(scan & 0xFF);
        }
        throw new ArgumentException("unknown key " + name);
    }

    static void Key(ushort vk, bool up)
    {
        if (!up && vk != 0xE8) KeyGuard?.Invoke();
        var flags = (up ? KEYUP : 0) | (ExtendedKeys.Contains(vk) ? EXTENDED : 0);
        Send(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags } });
    }

    static void Unicode(char ch, bool up)
    {
        if (!up) KeyGuard?.Invoke();
        Send(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = ch, dwFlags = UNICODE | (up ? KEYUP : 0) } });
    }

    static void Mouse(int dx, int dy, int data, uint flags) =>
        Send(new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } });

    static void Send(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("SendInput was blocked (error " + Marshal.GetLastWin32Error() + ")");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScan(char ch);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
}
