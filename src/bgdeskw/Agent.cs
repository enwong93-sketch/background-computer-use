using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace BgDesk;

/// <summary>Runs inside the child session and performs real input and capture on its desktop.</summary>
static class Agent
{
    static int _parentSession = -1;

    static bool InAgentSession()
    {
        var st = Common.ReadState();
        // The autostart entry can also fire at an ordinary logon; only serve a session the host did not start in.
        if (st is null || !Common.HostAlive(st) || (int)st["parentSession"]! == Process.GetCurrentProcess().SessionId) return false;
        _parentSession = (int)st["parentSession"]!;
        return true;
    }

    public static int Run()
    {
        if (!InAgentSession()) return 0;

        using var single = new Mutex(true, @"Local\bgdesk-agent", out var first);
        if (!first) return 0;

        WaitForDesktop();
        Containment.Start();
        while (true)
        {
            var pipe = new NamedPipeServerStream(Common.AgentPipe, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            // A client that connects and leaves at once (a reachability probe) makes this throw.
            try { pipe.WaitForConnection(); }
            catch (IOException) { pipe.Dispose(); continue; }
            new Thread(() => Serve(pipe)) { IsBackground = true }.Start();
        }
    }

    static void Serve(NamedPipeServerStream pipe)
    {
        // Layers claimed over this connection live as long as it does, so an agent that goes away
        // (cleanly or not) gives its layer back.
        var owned = new HashSet<string>();
        try
        {
            using (pipe)
            {
                var utf8 = new UTF8Encoding(false);
                using var reader = new StreamReader(pipe, utf8);
                using var writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" };
                while (reader.ReadLine() is { } line)
                {
                    JsonObject res;
                    try
                    {
                        var q = (JsonObject)JsonNode.Parse(line)!;
                        if ((bool?)q["owner"] == true && (string?)q["layer"] is { } claimed)
                        {
                            owned.Add(claimed);
                            Layers.SetOwner(claimed, owned);
                        }
                        res = Handle(q);
                        res["ok"] = true;
                    }
                    catch (Exception ex) { res = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                    writer.WriteLine(res.ToJsonString());
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            // An agent that reconnected owns its layer through the newer connection; leave that one alone.
            foreach (var key in owned)
                try { Layers.Release(key, owned); } catch { }
        }
    }

    const string MenuClosed = "your open menu or list was closed by another agent's turn (it had been open for more than 20 seconds)";
    static readonly HashSet<string> MenuKeys = new(StringComparer.OrdinalIgnoreCase) { "enter", "return", "space", "up", "down", "left", "right", "tab", "home", "end", "pageup", "pagedown" };

    static JsonObject Handle(JsonObject q)
    {
        var op = (string)q["op"]!;
        var key = (string?)q["layer"] ?? "main";
        // An agent whose menu was closed under it is told so with its next action. Only what would now act on
        // whatever was behind the menu (a click where it was, a key that chooses from it) is held back.
        if (op is "click" or "mouse_move" or "scroll" or "drag" or "key" or "type" && Layers.TakeLostMenu(key) is { } lost)
        {
            var tile = Layers.TileOf(key);
            var at = q["x"] is not null && q["y"] is not null ? new Point(X(q) + tile.X, Y(q) + tile.Y) : (Point?)null;
            var choosing = op == "click" && at is { } p && (lost.IsEmpty || lost.Contains(p))
                || op == "key" && ((string)q["keys"]!).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(MenuKeys.Contains);
            if (choosing) throw new InvalidOperationException($"Nothing was done: {MenuClosed}. Take a screenshot and open it again.");
            var answer = HandleCore(q);
            answer["notice"] = $"Note: {MenuClosed}.";
            return answer;
        }
        return HandleCore(q);
    }

    static JsonObject HandleCore(JsonObject q)
    {
        var op = (string)q["op"]!;
        var key = (string?)q["layer"] ?? "main";
        if (op is "click" or "mouse_move" or "scroll" or "drag" or "screenshot" or "key" or "type")
        {
            // A tile is the agent's whole screen: its coordinates start at the tile's corner, and nothing outside
            // the tile can be reached.
            var tile = Layers.TileOf(key);
            if (q["x"] is not null) q["x"] = ToCanvas(X(q), tile.X, tile.Width, "x");
            if (q["y"] is not null) q["y"] = ToCanvas(Y(q), tile.Y, tile.Height, "y");
            if (q["path"] is JsonArray points)
                q["path"] = new JsonArray(points.Select(p => (JsonNode)new JsonArray(ToCanvas((int)p![0]!, tile.X, tile.Width, "x"), ToCanvas((int)p[1]!, tile.Y, tile.Height, "y"))).ToArray());
            if (op == "screenshot") return Layers.RunInPlace(key, TileShot);
            if (op == "key" && WholeScreenChord((string)q["keys"]!) is { } chord)
                throw new ArgumentException($"{chord} acts on the whole screen, which other agents share, so it is not available. Use the window's own controls instead.");
        }
        switch (op)
        {
            case "ping":
                var vs = SystemInformation.VirtualScreen;
                return new JsonObject { ["session"] = Process.GetCurrentProcess().SessionId, ["width"] = vs.Width, ["height"] = vs.Height, ["layers"] = Layers.List() };
            case "release":
                Layers.Release(key);
                return new JsonObject();
            case "front":
                return new JsonObject { ["front"] = Layers.FrontLayer() };
            // These never need a turn.
            case "windows":
                return Layers.RunInPlace(key, _ => new JsonObject { ["windows"] = ListWindows(), ["route"] = "in-place" });
            case "launch":
                return Layers.RunInPlace(key, layer =>
                {
                    var (pid, note) = Layers.Launch(layer, (string)q["command"]!, (string?)q["args"] ?? "", (string?)q["cwd"]);
                    return new JsonObject { ["pid"] = pid, ["note"] = note, ["route"] = "in-place" };
                });
            case "clipboard_read":
                return new JsonObject { ["text"] = Layers.LayerClipboardText(key), ["route"] = "in-place" };
            case "clipboard_write":
                Layers.SetLayerClipboardText(key, (string)q["text"]!);
                return new JsonObject { ["route"] = "in-place" };
        }

        // A layer that has the keyboard is simply used with real input. One that does not is worked on in place
        // where that is known to behave the same, so that it does not have to take the keyboard from another.
        // "input": "real" lets the agent insist on real input, for an app that ignored an in-place action.
        if (Layers.FrontLayer() != key && (string?)q["input"] != "real")
        {
            switch (op)
            {
                case "click" when ((string?)q["button"] ?? "left") == "left" && ((int?)q["count"] ?? 1) is >= 1 and <= 3 && PlainModifiers((string?)q["modifiers"], out var ctrl, out var shift):
                    var at = new Point(X(q), Y(q));
                    var clicks = (int?)q["count"] ?? 1;
                    switch (Layers.RunInPlace(key, layer => Fusion.Click(layer, at, clicks, ctrl, shift), sendsInput: true))
                    {
                        case Fusion.Outcome.Done:
                            return InPlace(key);
                        case Fusion.Outcome.OpenedPopup:
                            // The click opened a list or menu, which only real input can go on to use. Taking the
                            // keyboard may have closed it again; if so, open it once more for real.
                            return Layers.Run(key, _ =>
                            {
                                if (!Fusion.PopupStillOpen()) Input.Click(at.X, at.Y, "left", 1, null);
                                return new JsonObject { ["route"] = "in-place, then real" };
                            });
                    }
                    break;
                case "mouse_move":
                    var hoverAt = new Point(X(q), Y(q));
                    if (Layers.RunInPlace(key, layer => Fusion.Move(layer, hoverAt), sendsInput: true) == Fusion.Outcome.Done) return new JsonObject { ["route"] = "in-place" };
                    break;
                case "type":
                    if (Layers.RunInPlace(key, layer => Fusion.Type(layer, (string)q["text"]!), sendsInput: true) == Fusion.Outcome.Done) return InPlace(key);
                    break;
                case "key":
                    if (Layers.RunInPlace(key, layer => Fusion.Key(layer, (string)q["keys"]!), sendsInput: true) == Fusion.Outcome.Done) return InPlace(key);
                    break;
                case "scroll":
                    var where = new Point(X(q), Y(q));
                    if (Layers.RunInPlace(key, layer => Fusion.Scroll(layer, where, (string?)q["direction"] ?? "down", (int?)q["amount"] ?? 3), sendsInput: true) == Fusion.Outcome.Done)
                        return InPlace(key);
                    break;
            }
        }

        switch (op)
        {
            case "logoff":
                if (Process.GetCurrentProcess().SessionId == _parentSession) throw new InvalidOperationException("not in the agent session");
                new Thread(() => { Thread.Sleep(200); ExitWindowsEx(0, 0); }).Start();
                return new JsonObject();
        }

        // Everything below needs the one pointer and keyboard, so it takes a turn.
        return Layers.Run(key, layer =>
        {
            switch (op)
            {
                case "clipboard_formats":
                    return new JsonObject { ["formats"] = Layers.ClipboardFormats() };
                case "mouse_move":
                    Input.Move(X(q), Y(q));
                    break;
                case "click":
                    Input.Click(X(q), Y(q), (string?)q["button"] ?? "left", (int?)q["count"] ?? 1, (string?)q["modifiers"]);
                    RememberTarget(layer, new Point(X(q), Y(q)));
                    break;
                case "drag":
                    var path = q["path"]!.AsArray().Select(p => new Point((int)p![0]!, (int)p[1]!)).ToList();
                    if (path.Count < 2) throw new ArgumentException("path needs at least two points");
                    Input.Drag(path, (string?)q["button"] ?? "left", (string?)q["modifiers"]);
                    layer.LastTarget = IntPtr.Zero;
                    break;
                case "scroll":
                    Input.Scroll(X(q), Y(q), (string?)q["direction"] ?? "down", (int?)q["amount"] ?? 3);
                    break;
                case "key":
                    Input.Keys((string)q["keys"]!);
                    break;
                case "type":
                    Input.Type((string)q["text"]!);
                    break;
                default:
                    throw new ArgumentException("unknown op " + op);
            }
            return new JsonObject { ["route"] = "real" };
        }, hover: op == "mouse_move");
    }

    /// <summary>The answer to an action done in place, with whether the window visibly changed when that could be told.</summary>
    static JsonObject InPlace(string key)
    {
        var answer = new JsonObject { ["route"] = "in-place" };
        if (Layers.LastChanged(key) is { } changed) answer["changed"] = changed;
        return answer;
    }

    /// <summary>After a real click: the control under it is the one that has the focus, which in-place typing needs to know.</summary>
    static void RememberTarget(Layer layer, Point at)
    {
        var top = Virtual.TopLevelAt(at);
        layer.LastTarget = top == IntPtr.Zero ? IntPtr.Zero : Virtual.Deepest(top, at);
    }

    /// <summary>True when the modifiers asked for are only Ctrl and/or Shift (or none); those can be shown as held in a message.</summary>
    static bool PlainModifiers(string? modifiers, out bool ctrl, out bool shift)
    {
        ctrl = shift = false;
        foreach (var m in (modifiers ?? "").ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (m is "ctrl" or "control") ctrl = true;
            else if (m == "shift") shift = true;
            else return false;
        }
        return true;
    }

    static int ToCanvas(int value, int origin, int size, string name) =>
        value >= 0 && value < size ? origin + value : throw new ArgumentException($"{name} must be from 0 to {size - 1}");

    /// <summary>
    /// The first chord that would act on the whole session rather than on a window: switching between all
    /// windows, minimizing everything, snapping to screen halves, the Start menu, locking. On a canvas these
    /// would reach other agents' tiles. Win+R and Win+E only open a window, which comes to the tile.
    /// </summary>
    static string? WholeScreenChord(string keys)
    {
        foreach (var chord in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = chord.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries);
            var win = parts.Any(p => p is "win" or "cmd" or "super");
            if (win && !(parts.Length == 2 && (parts.Contains("r") || parts.Contains("e")))) return chord;
            var alt = parts.Contains("alt");
            var ctrl = parts.Any(p => p is "ctrl" or "control");
            if (alt && parts.Any(p => p is "tab" or "esc" or "escape")) return chord;
            if (ctrl && alt && parts.Any(p => p is "delete" or "del")) return chord;
            if (ctrl && !alt && parts.Length == 2 && parts.Any(p => p is "esc" or "escape")) return chord;
        }
        return null;
    }

    /// <summary>A picture of the layer's tile, straight from the screen: every tile is on screen all the time.</summary>
    static JsonObject TileShot(Layer layer)
    {
        var tile = layer.Tile;
        using var bmp = new Bitmap(tile.Width, tile.Height, PixelFormat.Format24bppRgb);
        var cursor = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        var at = new Point(-1, -1);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(tile.Location, Point.Empty, tile.Size);
            if (GetCursorInfo(ref cursor) && (cursor.flags & 1) != 0 && tile.Contains(cursor.pt) && GetIconInfo(cursor.hCursor, out var info))
            {
                // The pointer is here: draw it as it really is (a text cursor, a hand over a link).
                var hdc = g.GetHdc();
                DrawIconEx(hdc, cursor.pt.X - tile.X - info.xHotspot, cursor.pt.Y - tile.Y - info.yHotspot, cursor.hCursor, 0, 0, 0, IntPtr.Zero, 3);
                g.ReleaseHdc(hdc);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                at = new Point(cursor.pt.X - tile.X, cursor.pt.Y - tile.Y);
            }
            else if (layer.Pointer is { } left && tile.Contains(left))
            {
                // The one pointer is in another tile; show this layer's where it left it.
                at = new Point(left.X - tile.X, left.Y - tile.Y);
                Cursors.Arrow.Draw(g, new Rectangle(at, Cursors.Arrow.Size));
            }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new JsonObject
        {
            ["route"] = "in-place",
            ["png"] = Convert.ToBase64String(ms.ToArray()),
            ["width"] = tile.Width,
            ["height"] = tile.Height,
            ["cursor"] = new JsonArray(Math.Max(0, at.X), Math.Max(0, at.Y)),
        };
    }

    static int X(JsonObject q) => (int)q["x"]!;
    static int Y(JsonObject q) => (int)q["y"]!;

    /// <summary>
    /// The logon script runs before the session's desktop is usable. Hold back the pipe until the screen
    /// can be captured and the taskbar exists, so "reachable" means "ready to work".
    /// </summary>
    static void WaitForDesktop()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new Bitmap(1, 1);
                using var g = Graphics.FromImage(probe);
                g.CopyFromScreen(0, 0, 0, 0, new Size(1, 1));
                if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return;
            }
            catch (System.ComponentModel.Win32Exception) { }
            Thread.Sleep(250);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);

    static JsonArray ListWindows()
    {
        var list = new JsonArray();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || !Virtual.InScope(h)) return true;
            // The desktop background and screen-wide overlays span every tile; they are nobody's window.
            if (!Rectangle.Inflate(Virtual.Scope, Common.TileGap, Common.TileGap).Contains(Virtual.FrameOf(h))) return true;
            var sb = new StringBuilder(512);
            if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
            GetWindowRect(h, out var r);
            GetWindowThreadProcessId(h, out var pid);
            string proc = "";
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
            // The language bar and the input-method host float over every tile; they are not the agent's windows.
            if (proc is "ctfmon" or "TextInputHost") return true;
            list.Add(new JsonObject
            {
                ["title"] = sb.ToString(),
                ["process"] = proc,
                ["rect"] = new JsonArray(r.Left - Virtual.Scope.X, r.Top - Virtual.Scope.Y, r.Right - Virtual.Scope.X, r.Bottom - Virtual.Scope.Y),
                ["minimized"] = IsIconic(h),
                ["hwnd"] = h.ToInt64(),
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public Point pt; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool ExitWindowsEx(uint flags, uint reason);

}
