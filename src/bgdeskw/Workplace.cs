using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace BgDesk;

/// <summary>
/// What the workplace page may ask the agent for: the layers and what they are doing, pictures of a layer, its
/// windows and its recent actions. All of it is looking, not working: no turn is taken, no layer is created,
/// and nothing here sends input.
///
/// Pictures are copied straight from the screen into a memory bitmap of our own. Measured in this session: a copy
/// waits for the display (about 32 ms whatever its size) but costs 1 to 2 ms of processor time when read in place,
/// against 11 to 12 ms through GetDIBits, and a small picture made by StretchBlt costs well under 1 ms.
/// </summary>
static class Workplace
{
    /// <summary>A memory bitmap that the screen is copied into, and what was there the last time.</summary>
    sealed class Surface : IDisposable
    {
        public readonly int Width, Height;
        readonly IntPtr _dc, _bitmap, _old, _bits;
        readonly byte[] _previous;

        public Surface(int width, int height)
        {
            Width = width;
            Height = height;
            var info = new BITMAPINFO { size = 40, width = width, height = -height, planes = 1, bitCount = 32 };
            _dc = CreateCompatibleDC(IntPtr.Zero);
            _bitmap = CreateDIBSection(_dc, ref info, 0, out _bits, IntPtr.Zero, 0);
            _old = SelectObject(_dc, _bitmap);
            _previous = new byte[width * height * 4];
        }

        /// <summary>Copies a part of the screen into the bitmap, shrinking it when the bitmap is smaller (nearest pixel: fast, rough).</summary>
        public void Grab(Rectangle from)
        {
            var screen = GetDC(IntPtr.Zero);
            try
            {
                const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;
                if (from.Width == Width && from.Height == Height) BitBlt(_dc, 0, 0, Width, Height, screen, from.X, from.Y, SrcCopy | CaptureBlt);
                else { SetStretchBltMode(_dc, 3 /* COLORONCOLOR */); StretchBlt(_dc, 0, 0, Width, Height, screen, from.X, from.Y, from.Width, from.Height, SrcCopy | CaptureBlt); }
                GdiFlush();
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        /// <summary>Asks a window to draw itself into this bitmap (about 32 frames a second for a Chromium window, where the screen shows about 8).</summary>
        public bool Print(IntPtr window)
        {
            var ok = PrintWindow(window, _dc, 2);
            GdiFlush();
            return ok;
        }

        /// <summary>Copies a rectangle of this bitmap onto another at the same size.</summary>
        public void BlitTo(Surface dest, int dx, int dy, int sx, int sy, int w, int h)
        {
            BitBlt(dest._dc, dx, dy, w, h, _dc, sx, sy, 0x00CC0020);
            GdiFlush();
        }

        /// <summary>A GDI+ view of the bitmap, for drawing the pointer on it and for encoding. Valid until the surface is disposed.</summary>
        public Bitmap View() => new(Width, Height, Width * 4, PixelFormat.Format32bppRgb, _bits);

        /// <summary>True when the bitmap differs from what it held the last time this was asked.</summary>
        public unsafe bool Changed()
        {
            var span = new ReadOnlySpan<byte>((void*)_bits, _previous.Length);
            if (span.SequenceEqual(_previous)) return false;
            span.CopyTo(_previous);
            return true;
        }

        public void Dispose()
        {
            SelectObject(_dc, _old);
            DeleteObject(_bitmap);
            DeleteDC(_dc);
        }
    }

    /// <summary>The newest picture of a layer in one size class, kept so that several viewers share one capture.</summary>
    sealed class Frame : IDisposable
    {
        public readonly object Lock = new();
        public Surface? Surface;
        public volatile bool Closed;
        public long Rev;
        public long CapturedAt, AskedAt;
        public int Pumping;
        public Point Cursor = new(-1, -1);
        public (long Rev, int Width, int Quality, string Base64)? Encoded;
        public void Dispose() { lock (Lock) { Closed = true; Surface?.Dispose(); Surface = null; Encoded = null; } }
    }

    // Thumbnails and full-size pictures are different pictures with their own revisions.
    static readonly ConcurrentDictionary<string, Frame> Frames = new();
    static long _nextRev;
    static long NextRev() => Interlocked.Increment(ref _nextRev);
    const int MinCaptureGapMs = 8, WatchGapMs = 55;
    // A thumbnail is shrunk to 720x450 by the copy, then to its width by a smooth resize.
    const int ThumbSource = 720;
    static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");

    public static JsonObject Handle(string op, JsonObject q)
    {
        switch (op)
        {
            case "workplace.layers":
                return new JsonObject { ["layers"] = Layers.Overview(), ["canvas"] = new JsonArray(Common.CanvasWidth, Common.CanvasHeight) };
            case "workplace.frame":
                return FrameOf(q);
            case "workplace.windows":
                return WindowsOf(q);
            case "workplace.activity":
                return new JsonObject { ["activity"] = Layers.RecentActivity(Key(q), (int?)q["count"] ?? 30) };
            case "workplace.control":
                return Control(q);
            case "workplace.input":
                return UserInput(q);
            case "workplace.thumb":
                return Layers.ThumbOf(Key(q), (long?)q["id"] ?? 0) is { } jpeg ? new JsonObject { ["jpeg"] = Convert.ToBase64String(jpeg) } : new JsonObject { ["jpeg"] = null };
            default:
                throw new ArgumentException("unknown op " + op);
        }
    }

    static string Key(JsonObject q) => (string?)q["layer"] ?? throw new ArgumentException("layer is required");

    static Layer Existing(JsonObject q) =>
        Layers.Peek(Key(q)) ?? throw new KeyNotFoundException("no such layer");

    /// <summary>
    /// A JPEG of the layer, or just its revision when nothing changed since the revision the viewer already has.
    /// A viewer asking often costs one capture per 35 ms however many are asking.
    /// </summary>
    static JsonObject FrameOf(JsonObject q)
    {
        var layer = Existing(q);
        var since = (long?)q["since"] ?? -1;
        var width = Math.Clamp((int?)q["width"] ?? layer.Tile.Width, 160, layer.Tile.Width);
        var quality = Math.Clamp((int?)q["quality"] ?? 60, 20, 95);
        var thumb = width <= ThumbSource / 2;
        var state = Frames.GetOrAdd(layer.Key + (thumb ? "/thumb" : "/full"), _ => new Frame());
        Interlocked.Exchange(ref state.AskedAt, Environment.TickCount64);
        var fast = !thumb && layer.Controller == "user" && Layers.ActiveUserLayer == layer.Key;
        if (fast) EnsurePump(layer, state);
        lock (state.Lock)
        {
            if (state.Closed) return new JsonObject { ["unchanged"] = true, ["rev"] = since };
            var pumping = fast && state.Surface is not null && Environment.TickCount64 - state.CapturedAt < 250;
            if (!pumping && (Environment.TickCount64 - state.CapturedAt >= WatchGapMs || state.Surface is null)) Capture(layer, state, thumb);
            if (state.Rev == since || state.Surface is null) return new JsonObject { ["unchanged"] = true, ["rev"] = state.Rev };

            if (state.Encoded is not { } e || e.Rev != state.Rev || e.Width != width || e.Quality != quality)
            {
                using var view = state.Surface.View();
                state.Encoded = (state.Rev, width, quality, Convert.ToBase64String(Encode(view, width, quality)));
                e = state.Encoded.Value;
            }
            var height = (int)Math.Round(layer.Tile.Height * (double)width / layer.Tile.Width);
            return new JsonObject
            {
                ["rev"] = state.Rev,
                ["width"] = width,
                ["height"] = height,
                ["cursor"] = new JsonArray(state.Cursor.X, state.Cursor.Y),
                ["jpeg"] = e.Base64,
            };
        }
    }

    /// <summary>
    /// Keeps a controlled layer's picture fresh on every composition beat in the background, without holding the frame lock
    /// during the 31 ms wait of PrintWindow or BitBlt: the viewer thread can therefore encode and send the previous frame at
    /// the same time. The scratch bitmaps belong to this thread alone and are freed when it stops.
    /// </summary>
    static void EnsurePump(Layer layer, Frame state)
    {
        if (state.Closed || Interlocked.CompareExchange(ref state.Pumping, 1, 0) != 0) return;
        new Thread(() =>
        {
            Surface? scratch = null, screen = null, window = null;
            long screenAt = 0;
            var screenWindows = "";
            try
            {
                while (!state.Closed && layer.Controller == "user" && Layers.ActiveUserLayer == layer.Key
                    && Environment.TickCount64 - Interlocked.Read(ref state.AskedAt) < 1000 && Layers.Peek(layer.Key) == layer)
                {
                    var started = Environment.TickCount64;
                    var tile = layer.Tile; int w = tile.Width, h = tile.Height;
                    if (scratch is null || scratch.Width != w || scratch.Height != h) { scratch?.Dispose(); scratch = new Surface(w, h); }
                    if (!PrintTopWindow(tile, ref screen, ref window, ref screenAt, ref screenWindows, scratch, started))
                    {
                        scratch.Grab(tile);
                        screenAt = started;
                        if (screen is null || screen.Width != w || screen.Height != h) { screen?.Dispose(); screen = new Surface(w, h); }
                        scratch.BlitTo(screen, 0, 0, 0, 0, w, h);
                    }
                    Point cursor;
                    using (var view = scratch.View()) using (var g = Graphics.FromImage(view)) cursor = Agent.DrawPointer(g, layer);
                    var changed = scratch.Changed();
                    lock (state.Lock)
                    {
                        if (state.Closed) break;
                        if (state.Surface is null || state.Surface.Width != w || state.Surface.Height != h)
                        {
                            state.Surface?.Dispose();
                            state.Surface = new Surface(w, h);
                            changed = true;
                        }
                        if (changed)
                        {
                            scratch.BlitTo(state.Surface, 0, 0, 0, 0, w, h);
                            state.Rev = NextRev();
                            state.Encoded = null;
                        }
                        state.Cursor = cursor;
                        state.CapturedAt = Environment.TickCount64;
                    }
                    var elapsed = Environment.TickCount64 - started;
                    if (elapsed < MinCaptureGapMs) Thread.Sleep((int)(MinCaptureGapMs - elapsed));
                }
            }
            catch { }
            finally
            {
                scratch?.Dispose();
                screen?.Dispose();
                window?.Dispose();
                Interlocked.Exchange(ref state.Pumping, 0);
            }
        }) { IsBackground = true, Name = "pump " + layer.Key }.Start();
    }

    static void Capture(Layer layer, Frame state, bool thumb)
    {
        if (state.Closed) return;
        var tile = layer.Tile;
        var (w, h) = thumb ? (ThumbSource, (int)Math.Round(ThumbSource * (double)tile.Height / tile.Width)) : (tile.Width, tile.Height);
        if (state.Surface is null || state.Surface.Width != w || state.Surface.Height != h)
        {
            state.Surface?.Dispose();
            state.Surface = new Surface(w, h);
            state.Rev = NextRev();
            state.Encoded = null;
        }
        state.Surface.Grab(tile);
        if (!thumb)
        {
            using var view = state.Surface.View();
            using var g = Graphics.FromImage(view);
            state.Cursor = Agent.DrawPointer(g, layer);
        }
        state.CapturedAt = Environment.TickCount64;
        if (state.Surface.Changed()) state.Rev = NextRev();
    }

    /// <summary>
    /// Prints the main Chromium or Electron window on top of the tile onto <paramref name="dest"/>, when no menu or list is
    /// open. Small title-less windows that belong to that window and sit above it (a bubble such as "Restore pages?")
    /// are put back on top from the last screen copy, so they are not covered up. False when a screen copy is needed.
    /// </summary>
    static bool PrintTopWindow(Rectangle tile, ref Surface? screen, ref Surface? window, ref long screenAt, ref string screenWindows, Surface dest, long now)
    {
        if (screen is null || now - screenAt > 1200) return false;
        var windows = new List<(IntPtr Handle, Rectangle Frame, string Class, bool OwnedPopup)>();
        var menuOpen = false;
        const long Caption = 0x00C00000;
        EnumWindows((w, _) =>
        {
            if (!IsWindowVisible(w) || IsIconic(w)) return true;
            var cls = Virtual.ClassOf(w);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "tooltips_class32") return true;
            var f = Virtual.FrameOf(w);
            if (!f.IntersectsWith(tile)) return true;
            if (cls is "#32768" or "ComboLBox" or "DropDown" or "Xaml_WindowedPopupClass") { menuOpen = true; return false; }
            if (!Rectangle.Inflate(tile, Common.TileGap, Common.TileGap).Contains(f)) return true;
            if ((GetWindowLongPtr(w, -20).ToInt64() & 0x08000000) != 0) return true;
            var ownedPopup = GetWindow(w, 4) != IntPtr.Zero && (GetWindowLongPtr(w, -16).ToInt64() & Caption) != Caption;
            windows.Add((w, f, cls, ownedPopup));
            return true;
        }, IntPtr.Zero);
        if (menuOpen || windows.Count == 0) return false;
        var sig = string.Join(";", windows.Select(x => $"{x.Handle}:{x.Frame.X},{x.Frame.Y},{x.Frame.Width},{x.Frame.Height}"));
        if (sig != screenWindows) { screenWindows = sig; return false; }

        var mainIndex = windows.FindIndex(x => !x.OwnedPopup);
        if (mainIndex < 0) return false;
        var top = windows[mainIndex];
        if (top.Class != "Chrome_WidgetWin_1") return false;
        for (var i = 0; i < mainIndex; i++)
            if (GetWindow(windows[i].Handle, 4) != top.Handle || windows[i].Frame.Width * windows[i].Frame.Height > 120_000) return false;

        if (!GetWindowRect(top.Handle, out var outer)) return false;
        int ow = outer.Right - outer.Left, oh = outer.Bottom - outer.Top;
        if (ow <= 0 || oh <= 0) return false;
        if (window is null || window.Width != ow || window.Height != oh) { window?.Dispose(); window = new Surface(ow, oh); }
        if (!window.Print(top.Handle)) return false;

        // Start from the last screen copy (background and any lower windows), paint the fresh main window over it, then any bubble above it.
        screen.BlitTo(dest, 0, 0, 0, 0, dest.Width, dest.Height);
        var vis = Rectangle.Intersect(top.Frame, Rectangle.FromLTRB(outer.Left, outer.Top, outer.Right, outer.Bottom));
        vis.Intersect(tile);
        if (vis.Width <= 0 || vis.Height <= 0) return false;
        window.BlitTo(dest, vis.X - tile.X, vis.Y - tile.Y, vis.X - outer.Left, vis.Y - outer.Top, vis.Width, vis.Height);
        for (var i = mainIndex - 1; i >= 0; i--)
        {
            var b = Rectangle.Intersect(windows[i].Frame, tile);
            if (b.Width > 0 && b.Height > 0) screen.BlitTo(dest, b.X - tile.X, b.Y - tile.Y, b.X - tile.X, b.Y - tile.Y, b.Width, b.Height);
        }
        return true;
    }
    static byte[] Encode(Bitmap picture, int width, int quality)
    {
        using var ms = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        if (width >= picture.Width) { picture.Save(ms, JpegCodec, parameters); return ms.ToArray(); }
        var height = (int)Math.Round(picture.Height * (double)width / picture.Width);
        using var small = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(picture, new Rectangle(0, 0, width, height), new Rectangle(0, 0, picture.Width, picture.Height), GraphicsUnit.Pixel);
        }
        small.Save(ms, JpegCodec, parameters);
        return ms.ToArray();
    }

    /// <summary>The layer's windows. They are listed on the layer's own thread; a layer that is busy simply has none this time.</summary>
    static JsonObject WindowsOf(JsonObject q)
    {
        var layer = Existing(q);
        try { return new JsonObject { ["windows"] = layer.Run(Agent.ListWindows, 700) }; }
        catch (TimeoutException) { return new JsonObject { ["windows"] = null, ["busy"] = true }; }
    }

    // ---- the user takes a layer ----

    static JsonObject Control(JsonObject q)
    {
        var key = Key(q);
        var viewer = (string?)q["viewer"] ?? throw new ArgumentException("viewer is required");
        switch ((string?)q["action"])
        {
            case "take":
                var ok = Layers.TakeControl(key, viewer, out var holder);
                return new JsonObject { ["taken"] = ok, ["holder"] = holder };
            case "release":
                return new JsonObject { ["released"] = Layers.ReleaseControl(key, viewer, "user", (string?)q["note"]) };
            case "away":
                // The page closed: control goes back, whatever the reason the page gave.
                return new JsonObject { ["released"] = Layers.ReleaseControl(key, viewer, "away") };
            case "dismiss":
                Layers.DismissHelp(key);
                return new JsonObject();
            default:
                throw new ArgumentException("unknown action");
        }
    }

    /// <summary>
    /// One input from the user while they hold the layer. It goes the same ways an agent's does: in place where that is
    /// known to behave like real input, otherwise as a turn with the real pointer and keyboard, ahead of agents waiting.
    /// </summary>
    static JsonObject UserInput(JsonObject q)
    {
        var key = Key(q);
        var viewer = (string?)q["viewer"] ?? "";
        var layer = Existing(q);
        if (layer.Controller != "user" || layer.ControlBy != viewer) throw new InvalidOperationException("not in control of this layer");
        Layers.TouchControl(key);

        var call = new JsonObject { ["layer"] = key };
        int Int(string name) => (int?)q[name] ?? throw new ArgumentException(name + " is required");
        string Button() => (string?)q["button"] is "right" or "middle" ? (string)q["button"]! : "left";
        switch ((string?)q["kind"])
        {
            case "click":
                call["op"] = "click"; call["x"] = Int("x"); call["y"] = Int("y"); call["button"] = Button();
                call["count"] = Math.Clamp((int?)q["count"] ?? 1, 1, 3);
                if ((bool?)q["double"] == true) call["double"] = true;
                if ((string?)q["modifiers"] is { Length: > 0 } m && m.Split('+').All(p => p is "ctrl" or "shift" or "alt")) call["modifiers"] = m;
                break;
            case "down": call["op"] = "user_down"; call["x"] = Int("x"); call["y"] = Int("y"); call["button"] = Button(); break;
            case "move":
                // Without a button down the pointer is only followed where a web page can take it in place; it never costs a turn.
                if (layer.UserButtonDown is null) return Agent.UserHover(key, Int("x"), Int("y"));
                call["op"] = "user_move"; call["x"] = Int("x"); call["y"] = Int("y");
                break;
            case "up": call["op"] = "user_up"; call["x"] = Int("x"); call["y"] = Int("y"); call["button"] = layer.UserButtonDown ?? Button(); break;
            case "wheel":
                call["op"] = "scroll"; call["x"] = Int("x"); call["y"] = Int("y");
                var dy = (int?)q["dy"] ?? 0; var dx = (int?)q["dx"] ?? 0;
                if (dy == 0 && dx == 0) return new JsonObject { ["route"] = "ignored" };
                call["direction"] = Math.Abs(dy) >= Math.Abs(dx) ? (dy > 0 ? "down" : "up") : (dx > 0 ? "right" : "left");
                call["amount"] = Math.Clamp((int)Math.Round(Math.Max(Math.Abs(dy), Math.Abs(dx)) / 100.0), 1, 10);
                break;
            case "key":
                call["op"] = "key"; call["keys"] = (string?)q["keys"] is { Length: > 0 and <= 80 } k ? k : throw new ArgumentException("keys is required");
                break;
            case "text":
                call["op"] = "type"; call["text"] = (string?)q["text"] is { Length: > 0 and <= 2000 } s ? s : throw new ArgumentException("text is required");
                break;
            default:
                throw new ArgumentException("unknown input");
        }
        // The many small moves of a drag are not each a line in the timeline; its start and its end are.
        if ((string?)call["op"] == "user_move")
        {
            Layers.UserTurn = true;
            try { return Agent.HandleCore(call); } finally { Layers.UserTurn = false; }
        }
        // What the user does is recorded like what an agent does, marked as theirs; typed text is not kept.
        var summary = "You: " + Layers.Summarize(call);
        Layers.UserTurn = true;
        try { return Layers.Track(key, null, (string)call["op"]!, summary, () => Agent.HandleCore(call), user: true); }
        finally { Layers.UserTurn = false; }
    }

    // ---- a small picture after each action, for the page's timeline ----

    static readonly BlockingCollection<(string Key, Activity.Entry Entry, long Due)> ThumbQueue = new(256);
    static Thread? _thumbThread;

    public static void QueueThumb(string key, Activity.Entry entry)
    {
        if (_thumbThread is null)
            lock (ThumbQueue) _thumbThread ??= new Thread(ThumbLoop) { IsBackground = true, Name = "timeline pictures" }.With(t => t.Start());
        // Looked at a moment after the action, when the program has had time to show what it did.
        ThumbQueue.TryAdd((key, entry, Environment.TickCount64 + 250));
    }

    static T With<T>(this T value, Action<T> action) { action(value); return value; }

    static void ThumbLoop()
    {
        foreach (var (key, entry, due) in ThumbQueue.GetConsumingEnumerable())
        {
            try
            {
                var wait = due - Environment.TickCount64;
                if (wait > 0) Thread.Sleep((int)wait);
                if (Layers.Peek(key) is not { } layer) continue;
                var state = Frames.GetOrAdd(layer.Key + "/mini", _ => new Frame());
                lock (state.Lock)
                {
                    if (state.Closed || Layers.Peek(key) != layer) continue;
                    Capture(layer, state, thumb: true);
                    if (state.Surface is null) continue;
                    using var view = state.Surface.View();
                    entry.Thumb = Encode(view, 240, 45);
                }
            }
            catch { }
        }
    }
    public static void Forget(string key)
    {
        foreach (var suffix in new[] { "/thumb", "/full", "/mini" })
            if (Frames.TryRemove(key + suffix, out var state)) state.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public int size, width, height; public short planes, bitCount; public int compression, sizeImage, xppm, yppm, used, important; }

    delegate bool EnumProc(IntPtr window, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
}
