using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace BgDesk;

/// <summary>
/// One layer per agent. The background session is one large screen divided into tiles that do not overlap,
/// with a gap between them; each layer is one tile. A tile is the agent's whole screen: it sees only its tile,
/// its coordinates start at the tile's corner, and its windows are kept inside it (see <see cref="Containment"/>).
/// All tiles are on screen at once, so pictures are always taken from the screen and nothing has to be switched.
/// The session has one pointer and one keyboard, so real input takes turns; much input can also be done without
/// a turn (see <see cref="Fusion"/>).
/// </summary>
sealed class Layer
{
    public required string Key { get; init; }
    public required int Slot { get; init; }
    public required Rectangle Tile { get; init; }
    public IntPtr Job { get; init; }
    public object? Owner { get; set; }

    // In-place input does not move the real pointer or change which window has the keyboard. These remember
    // where it acted, so that real input, when it is next needed, first catches up: the window clicked in place
    // gets the keyboard and the pointer is put where it was left.
    public IntPtr VirtualTop { get; set; }
    public IntPtr LastTarget { get; set; }
    /// <summary>Whether the window visibly changed after the last in-place action; null when that could not be told.</summary>
    public bool? LastChanged { get; set; }
    // The window that had the keyboard when real input was last used.
    public IntPtr LastForeground { get; set; }

    /// <summary>Where this layer's menu was, when it kept it open too long and another layer's turn closed it.</summary>
    public Rectangle? LostMenu { get; set; }

    // Where this layer's pointer was left, and whether it was left hovering over something.
    public Point? Pointer { get; set; }
    public bool Hovering { get; set; }

    // What this layer last had on the clipboard, and the clipboard's change counter when that was true.
    public IDataObject Clipboard { get; set; } = new DataObject();
    public uint ClipboardSequence { get; set; }
    // Set when the layer's clipboard was changed while another layer held the shared one.
    public bool ClipboardPending { get; set; }

    readonly BlockingCollection<Action> _work = [];

    /// <summary>All work for a layer runs on one thread, which only sees the layer's tile (<see cref="Virtual.Scope"/>).</summary>
    public void StartWorker()
    {
        using var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            Virtual.Scope = Tile;
            ready.Set();
            foreach (var action in _work.GetConsumingEnumerable()) action();
        }) { IsBackground = true, Name = "layer " + Key }.Start();
        ready.Wait();
    }

    /// <summary>
    /// Runs work on the layer's thread and waits for it. With a timeout, gives up waiting (the work still runs
    /// to its end) and throws TimeoutException. Code that holds a lock while waiting must use one, so that a
    /// layer thread that is busy or waiting for that same lock cannot stop everything.
    /// </summary>
    public T Run<T>(Func<T> work, int timeoutMs = Timeout.Infinite)
    {
        T result = default!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        _work.Add(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeoutMs)) throw new TimeoutException("the layer did not answer in time");
        done.Dispose();
        return error is null ? result : throw error;
    }

    public void StopWorker() => _work.CompleteAdding();
}

static class Layers
{
    const uint CreateSuspended = 0x4, CreateNewConsole = 0x10;

    /// <summary>The tiles the screen has room for, in order; a layer's slot is its index here.</summary>
    static readonly List<Rectangle> Slots = LayOutTiles();

    /// <summary>
    /// As many tiles of the standard size as fit, with a gap around each and room for the taskbar. A session that
    /// was started smaller than the canvas (by an older version) still works: with no room for a standard tile it
    /// becomes a single tile of the whole work area.
    /// </summary>
    static List<Rectangle> LayOutTiles()
    {
        var screen = Screen.PrimaryScreen!.Bounds;
        int w = Common.TileWidth, h = Common.TileHeight, gap = Common.TileGap;
        var columns = (screen.Width - gap) / (w + gap);
        var rows = (screen.Height - Common.TaskbarAllowance - gap) / (h + gap);
        if (columns < 1 || rows < 1) return [Screen.PrimaryScreen.WorkingArea];
        var tiles = new List<Rectangle>();
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
                tiles.Add(new Rectangle(screen.X + gap + column * (w + gap), screen.Y + gap + row * (h + gap), w, h));
        return tiles.Take(Common.TileColumns * Common.TileRows).ToList();
    }

    /// <summary>The tiles and jobs of the layers that exist, for the containment thread, which must not wait for <see cref="Gate"/>.</summary>
    public static readonly ConcurrentDictionary<string, (Rectangle Tile, IntPtr Job)> Tiles = new();

    /// <summary>The layer that last used real input, and when: a window that comes up by itself most likely answers it.</summary>
    public static (string Key, long At)? LastInput;

    static readonly object Gate = new();
    static readonly Dictionary<string, Layer> All = [];
    static Layer? _active;

    // Turn-taking. One real-input operation runs at a time; waiting operations go in arrival order. A layer that
    // has something open which moving the keyboard elsewhere would close (a menu, a drop-down, a drag) keeps
    // the keyboard until that is gone, for at most MaxHoldMs, so another agent's turn cannot break it.
    const int MaxHoldMs = 20_000;
    static readonly LinkedList<(string Key, bool IgnoreHold)> Waiting = new();
    static bool _busy;
    static string? _holder;
    static long _holdStart;
    // A layer whose hold ran out; its menu is not taken as a reason to wait again until another layer has had a turn.
    static string? _released;

    static void Enter(string key, bool ignoreHold = false)
    {
        lock (Gate)
        {
            var node = Waiting.AddLast((key, ignoreHold));
            string? expired = null;
            try
            {
                while (true)
                {
                    if (!_busy)
                    {
                        // A menu opens a moment after the click that asks for it, which can be after that turn ended.
                        // The layer that had the keyboard last still holds it if a menu of its has come up since.
                        if (_holder is null && _active is not null && _active.Key != key && _active.Key != _released && All.ContainsKey(_active.Key) && Transient(_active))
                        {
                            _holder = _active.Key;
                            _holdStart = Environment.TickCount64;
                        }
                        if (_holder is not null && All.TryGetValue(_holder, out var holding) && Transient(holding) && Environment.TickCount64 - _holdStart > MaxHoldMs)
                        {
                            // Held too long: the next turn of another layer will close that menu.
                            expired = _released = _holder;
                            _holder = null;
                        }
                        else if (_holder is not null && (!All.TryGetValue(_holder, out holding) || !Transient(holding)))
                            _holder = null;
                        var first = Waiting.First;
                        while (first is not null && !(first.Value.IgnoreHold || _holder is null || first.Value.Key == _holder)) first = first.Next;
                        if (first == node) break;
                    }
                    Monitor.Wait(Gate, 200);
                }
            }
            finally { Waiting.Remove(node); }
            _busy = true;
            // Its agent will act on the menu it believes is still open; tell it instead of letting a click land behind it.
            if (expired is not null && expired != key && All.TryGetValue(expired, out var loser)) loser.LostMenu = MenuArea(loser.Tile);
        }
    }

    static void Exit(Layer? layer)
    {
        lock (Gate)
        {
            _busy = false;
            if (layer is not null && All.ContainsKey(layer.Key))
            {
                if (Transient(layer))
                {
                    if (_holder != layer.Key) { _holder = layer.Key; _holdStart = Environment.TickCount64; }
                }
                else if (_holder == layer.Key) _holder = null;
            }
            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>
    /// True when the layer's tile shows something that closes when the keyboard goes elsewhere. Called with the
    /// turn lock held, so it looks at the screen directly instead of asking the layer's thread, which may be busy.
    /// </summary>
    static bool Transient(Layer layer)
    {
        try { return HasTransientUi(layer.Tile); } catch { return false; }
    }

    static bool HasTransientUi(Rectangle tile)
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && Virtual.FrameOf(foreground).IntersectsWith(tile))
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info))
            {
                // in a menu, a system menu, a pop-up menu, or moving/sizing a window
                if ((info.flags & 0x1E) != 0) return true;
                // something holds the mouse: a drop-down, a custom menu, a drag in progress
                if (info.hwndCapture != IntPtr.Zero) return true;
            }
        }
        var found = false;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !Virtual.FrameOf(window).IntersectsWith(tile)) return true;
            if (Virtual.ClassOf(window) is "#32768" or "ComboLBox" or "DropDown" or "Xaml_WindowedPopupClass") { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Once, after the layer's open menu was closed by another layer's turn: where the menu was (empty when not known).</summary>
    public static Rectangle? TakeLostMenu(string key)
    {
        lock (Gate)
        {
            if (!All.TryGetValue(key, out var layer) || layer.LostMenu is not { } area) return null;
            layer.LostMenu = null;
            return area;
        }
    }

    /// <summary>The area the open menus and lists of a tile cover: menus, drop-down lists, and title-less pop-ups that belong to a window.</summary>
    static Rectangle MenuArea(Rectangle tile)
    {
        var area = Rectangle.Empty;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var frame = Virtual.FrameOf(window);
            if (!frame.IntersectsWith(tile)) return true;
            // Overlays that never take the focus (the language bar, input-method windows) are not menus.
            if ((GetWindowLongPtr(window, -20).ToInt64() & 0x08000000) != 0) return true;
            var menu = Virtual.ClassOf(window) is "#32768" or "ComboLBox" or "DropDown" or "Xaml_WindowedPopupClass"
                || (GetWindow(window, 4) != IntPtr.Zero && (GetWindowLongPtr(window, -16).ToInt64() & 0x00C00000) != 0x00C00000
                    && Virtual.ClassOf(window) is not ("tooltips_class32" or "TF_FloatingLangBar_WndTitle"));
            if (menu) area = area.IsEmpty ? frame : Rectangle.Union(area, frame);
            return true;
        }, IntPtr.Zero);
        return area;
    }

    /// <summary>Whether the layer's window visibly changed after its last in-place action; null when that could not be told.</summary>
    public static bool? LastChanged(string key)
    {
        lock (Gate) return All.TryGetValue(key, out var layer) ? layer.LastChanged : null;
    }

    /// <summary>
    /// Keys and the mouse buttons are held down for the whole session while real input is being sent, and a
    /// program asks Windows for that state when it handles a message (Ctrl makes a wheel turn zoom a page and a
    /// click open a link in a new tab). Real input therefore takes this lock exclusively while it runs, and input
    /// done in place takes it shared, so an in-place action never meets another layer's held key.
    /// </summary>
    static readonly ReaderWriterLockSlim InputState = new(LockRecursionPolicy.NoRecursion);

    static Layer Get(string key)
    {
        lock (Gate)
        {
            if (!All.TryGetValue(key, out var layer)) All[key] = layer = Create(key);
            return layer;
        }
    }

    /// <summary>Runs something on the layer's thread without waiting for a turn.</summary>
    public static T RunInPlace<T>(string key, Func<Layer, T> operation, bool sendsInput = false)
    {
        var target = Get(key);
        return target.Run(() =>
        {
            if (sendsInput) InputState.EnterReadLock();
            try { return operation(target); }
            finally { if (sendsInput) InputState.ExitReadLock(); }
        });
    }

    /// <summary>Runs an operation that needs real input on a layer, in turn, creating the layer on first use.</summary>
    public static T Run<T>(string key, Func<Layer, T> operation, bool hover = false, bool observe = false)
    {
        Enter(key);
        Layer? layer = null;
        try
        {
            layer = Get(key);
            var arrived = Activate(layer);
            var target = layer;
            return target.Run(() =>
            {
                // The keyboard may be in another tile; it has to come here first, to the window on top of this tile.
                if (!InTile(target, GetForegroundWindow()) && target.VirtualTop == IntPtr.Zero)
                {
                    var want = Virtual.TopOfZOrder();
                    if (want == IntPtr.Zero && target.LastForeground != IntPtr.Zero && IsWindowVisible(target.LastForeground) && InTile(target, target.LastForeground)) want = target.LastForeground;
                    if (want != IntPtr.Zero)
                    {
                        Input.Nudge();
                        SetForegroundWindow(want);
                        Thread.Sleep(120);
                    }
                    LockFocus();
                }
                var catchUp = target.VirtualTop != IntPtr.Zero;
                if (catchUp)
                {
                    // Real input goes to the window that has the keyboard. After work done in place that should be the
                    // window that was clicked, or a dialog or further window its program has opened since.
                    var onTop = Virtual.Successor(target.VirtualTop, w => StartedByLayer(target, w));
                    if (onTop != IntPtr.Zero && GetForegroundWindow() != onTop)
                    {
                        Input.Nudge();
                        SetForegroundWindow(onTop);
                        Thread.Sleep(120);
                    }
                    LockFocus();
                    // The control clicked in place stays the one that has the focus when it is still in the window that
                    // got the keyboard; key presses do not move the focus. Typing in place goes there if another layer
                    // has the keyboard by the time the next text comes.
                    if (onTop != target.VirtualTop) target.LastTarget = IntPtr.Zero;
                    target.VirtualTop = IntPtr.Zero;
                }
                if ((arrived || catchUp) && target.Pointer is { } left)
                {
                    // The pointer is shared by the whole session, so another layer has moved it. Put it
                    // back where this layer left it; if it was hovering and is about to be looked at, give the tooltip time to return.
                    Input.Move(left.X, left.Y);
                    if (target.Hovering && observe) Thread.Sleep(700);
                }
                InputState.EnterWriteLock();
                Input.KeyGuard = () => KeepFocusInTile(target);
                LastInput = (target.Key, Environment.TickCount64);
                try { return operation(target); }
                finally
                {
                    LastInput = (target.Key, Environment.TickCount64);
                    Input.KeyGuard = null;
                    LockFocus();
                    InputState.ExitWriteLock();
                    if (GetCursorPos(out var now)) target.Pointer = now;
                    target.Hovering = hover;
                    target.LastForeground = GetForegroundWindow();
                }
            });
        }
        finally { Exit(layer); }
    }

    static bool InTile(Layer layer, IntPtr window) => window != IntPtr.Zero && Virtual.FrameOf(window).IntersectsWith(layer.Tile);

    /// <summary>
    /// Every program could take the keyboard away from the tile being typed in: a window that another agent's
    /// program opens comes to the front by default. Locking the foreground stops that; only this process moves
    /// it, at the start of a turn. The lock has to be set again after each such move.
    /// </summary>
    static void LockFocus() => LockSetForegroundWindow(1);

    /// <summary>Before each key press: the keyboard must still be in the tile, or be brought back.</summary>
    static void KeepFocusInTile(Layer layer)
    {
        if (InTile(layer, GetForegroundWindow())) return;
        var back = layer.LastForeground != IntPtr.Zero && IsWindowVisible(layer.LastForeground) && InTile(layer, layer.LastForeground) ? layer.LastForeground : Virtual.TopOfZOrder();
        if (back != IntPtr.Zero) { Input.Nudge(); SetForegroundWindow(back); LockFocus(); Thread.Sleep(50); }
        if (!InTile(layer, GetForegroundWindow()))
            throw new InvalidOperationException("the keyboard could not be kept in your area; nothing more was typed");
    }

    /// <summary>The tile of a layer, creating the layer on first use.</summary>
    public static Rectangle TileOf(string key) => Get(key).Tile;

    /// <summary>True when the window belongs to a process that runs in the layer's job, that is, one started on this layer.</summary>
    static bool StartedByLayer(Layer layer, IntPtr window)
    {
        try
        {
            GetWindowThreadProcessId(window, out var pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return IsProcessInJob(process.Handle, layer.Job, out var inJob) && inJob;
        }
        catch { return false; }
    }

    /// <summary>Makes the layer the one whose input is going on. Returns true when another layer had it before.</summary>
    static bool Activate(Layer layer)
    {
        // Right after the session reconnects Windows shows its own logon screen for a moment, and input sent then
        // goes nowhere. Wait that out instead of handing the agent an error it can do nothing about.
        var until = Environment.TickCount64 + 8000;
        while (InputDesktopName() != "Default")
        {
            if (Environment.TickCount64 > until)
                throw new InvalidOperationException("the background desktop cannot take input right now; the background session may be reconnecting");
            Thread.Sleep(250);
        }
        if (_active == layer) return false;

        // The clipboard is shared by the whole session. Each layer gets its own by keeping a copy of
        // what it put there and putting that back when another layer has changed the clipboard since.
        var sequence = GetClipboardSequenceNumber();
        // If the clipboard cannot be read right now (another program holds it), keep the older copy
        // rather than recording an empty clipboard for the layer.
        if (_active is not null && _active.ClipboardSequence != sequence && Snapshot() is { } copy)
        {
            _active.Clipboard = copy;
            _active.ClipboardSequence = sequence;
        }
        if (layer.ClipboardPending || layer.ClipboardSequence != sequence)
        {
            Restore(layer.Clipboard);
            layer.ClipboardSequence = GetClipboardSequenceNumber();
            layer.ClipboardPending = false;
        }
        _active = layer;
        _released = null;
        return true;
    }

    /// <summary>The layer's clipboard text without taking a turn.</summary>
    public static string LayerClipboardText(string key)
    {
        var layer = Get(key);
        lock (Gate)
        {
            // The shared clipboard is the active layer's. When no layer has had real input yet (everything so far
            // was done in place) it belongs to nobody, so every layer has its own copy only.
            if (_active == layer) return ClipboardText();
            return layer.Clipboard.GetData(DataFormats.UnicodeText) as string ?? "";
        }
    }

    public static void SetLayerClipboardText(string key, string text)
    {
        var layer = Get(key);
        lock (Gate)
        {
            if (_active == layer) { SetClipboardText(text); return; }
            // Not the layer that owns the shared clipboard right now (or nobody owns it yet): keep it for when it is.
            layer.Clipboard = text.Length == 0 ? new DataObject() : new DataObject(DataFormats.UnicodeText, text);
            layer.ClipboardPending = true;
        }
    }

    public static string ClipboardText() => Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : "", "");

    public static string ClipboardFormats() => Sta(() => string.Join(", ", Clipboard.GetDataObject()?.GetFormats(false) ?? []), "(unreadable)");

    public static void SetClipboardText(string text) => Sta(() =>
    {
        if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text);
        return true;
    }, false);

    // Text, rich text, HTML, copied files and images: what people and agents actually copy.
    static readonly string[] KeptFormats = [DataFormats.UnicodeText, DataFormats.Rtf, DataFormats.Html, DataFormats.FileDrop, DataFormats.Bitmap];

    static IDataObject? Snapshot()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (SnapshotOnce() is { } copy) return copy;
            Thread.Sleep(120);
        }
        return null;
    }

    static IDataObject? SnapshotOnce() => Sta<IDataObject?>(() =>
    {
        var copy = new DataObject();
        if (Clipboard.GetDataObject() is not { } source) return copy;
        foreach (var format in KeptFormats)
        {
            if (!source.GetDataPresent(format) || source.GetData(format) is not { } data) continue;
            // The image on the clipboard belongs to the clipboard; keep our own.
            copy.SetData(format, data is Image image ? new Bitmap(image) : data);
        }
        return copy;
    }, null);

    static void Restore(IDataObject data) => Sta(() =>
    {
        if (data.GetFormats().Length == 0) Clipboard.Clear(); else Clipboard.SetDataObject(data, true);
        return true;
    }, false);

    /// <summary>Clipboard calls need a single-threaded apartment. A busy clipboard yields the fallback.</summary>
    static T Sta<T>(Func<T> f, T fallback)
    {
        var result = fallback;
        var t = new Thread(() => { try { result = f(); } catch { } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    /// <summary>The key of the layer that has the keyboard, if any.</summary>
    public static string? FrontLayer()
    {
        lock (Gate) return _active?.Key;
    }

    public static JsonArray List()
    {
        lock (Gate) return new JsonArray(All.Keys.Select(k => (JsonNode)k).ToArray());
    }

    public static void SetOwner(string key, object owner)
    {
        lock (Gate) Owners[key] = owner;
    }

    static readonly Dictionary<string, object> Owners = [];

    /// <summary>
    /// Closes everything started on the layer and frees its tile. With an owner given, only when that owner
    /// still holds the layer.
    /// </summary>
    public static void Release(string key, object? owner = null)
    {
        Enter(key, ignoreHold: true);
        try { ReleaseNow(key, owner); }
        finally { Exit(null); }
    }

    static void ReleaseNow(string key, object? owner)
    {
        Layer? layer;
        lock (Gate)
        {
            if (owner is not null && Owners.TryGetValue(key, out var current) && !ReferenceEquals(current, owner)) return;
            Owners.Remove(key);
            if (!All.Remove(key, out layer)) return;
            if (_holder == key) _holder = null;
            if (_active == layer) _active = null;
            Tiles.TryRemove(key, out _);
        }
        // The rest takes a moment and is done without the lock, so other agents are not held up.
        TerminateJobObject(layer.Job, 0);
        CloseHandle(layer.Job);
        // Windows that Windows itself made for the layer (folders, Store apps) are not in the job; close what
        // is left in the tile. The processes behind them may serve other tiles, so they stay.
        try { CloseAppWindows(layer.Tile); } catch { }
        Containment.Forget(key);
        layer.StopWorker();
    }

    static Layer Create(string key)
    {
        if (All.Count >= Slots.Count) throw new InvalidOperationException($"all {Slots.Count} layers are in use");
        // Slots are reused, so per-layer data such as browser profiles does not pile up.
        var slot = Enumerable.Range(0, Slots.Count).First(n => All.Values.All(l => l.Slot != n));
        var layer = new Layer { Key = key, Slot = slot, Tile = Slots[slot], Job = CreateJobObject(IntPtr.Zero, null) };
        layer.StartWorker();
        Tiles[key] = (layer.Tile, layer.Job);
        return layer;
    }

    /// <summary>Asks every application window in the tile to close. The shell's own windows stay.</summary>
    static void CloseAppWindows(Rectangle tile)
    {
        var shell = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "bgdeskw", "TextInputHost", "ctfmon", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "ShellHost", "LockApp", "Widgets",
        };
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
            // Screen-wide windows (the desktop background, overlays) reach into every tile; they are nobody's.
            var frame = Virtual.FrameOf(window);
            if (!frame.IntersectsWith(tile) || !Rectangle.Inflate(tile, Common.TileGap, Common.TileGap).Contains(frame)) return true;
            GetWindowThreadProcessId(window, out var pid);
            string process;
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); process = p.ProcessName; } catch { return true; }
            // Explorer is the shell, but its folder windows and file-operation dialogs are application windows.
            var explorerWindow = process.Equals("explorer", StringComparison.OrdinalIgnoreCase) && Virtual.ClassOf(window) is "CabinetWClass" or "OperationStatusWindow" or "#32770";
            if (explorerWindow || !shell.Contains(process)) PostMessage(window, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Starts a program, document or URL on the layer. The note, when present, tells the agent that no window came up.</summary>
    public static (int Pid, string? Note) Launch(Layer layer, string command, string args, string? cwd)
    {
        // Windows would bring up an existing window for the same folder, which may be in another tile.
        if (Directory.Exists(command))
            return StartShellWindow(layer, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/n,\"{Path.GetFullPath(command)}\"", cwd);

        // A web address handed to the shell would open as a tab in the browser the user already has
        // running, on the user's own screen. Start the default browser here ourselves instead.
        if ((command.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || command.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) && DefaultBrowser() is { } browser)
            return Launch(layer, browser, $"\"{command}\" {args}".TrimEnd(), cwd);

        var exe = ResolveExecutable(command);
        if (exe is null)
        {
            // A document or other URL: let the shell pick the program, from a process started on this layer.
            return StartShellWindow(layer, Path.Combine(Environment.SystemDirectory, "rundll32.exe"), $"url.dll,FileProtocolHandler {command}", cwd);
        }

        // Many programs allow one running copy: a second start hands its work to the first copy and exits, and
        // the work then happens in that copy's window. Chromium browsers and Electron apps decide this by their
        // data folder, so giving each layer its own folder gives each layer its own copy.
        var name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        if ((name is "chrome" or "msedge" or "brave" or "chromium" or "vivaldi" || IsElectron(exe)) && !args.Contains("--user-data-dir"))
            args = $"--user-data-dir=\"{Path.Combine(Common.DataDir, "profiles", $"{name}-{layer.Slot}")}\" --no-first-run {args}";

        // A console program would be handed to the default terminal (Windows Terminal), which is one shared
        // process with one window for every console. The classic console host gives this one its own window.
        var shown = Path.GetFileName(exe);
        if (IsConsoleProgram(exe))
        {
            args = $"\"{exe}\" {args}".TrimEnd();
            exe = Path.Combine(Environment.SystemDirectory, "conhost.exe");
        }

        var all = AllVisibleWindows();
        var pid = Start(layer, exe, args, cwd);
        var note = ClaimNewWindows(layer, all, pid, shell: false);
        BringUpNewest(layer, all);
        return (pid, note is null ? null : $"{shown} {note}");
    }

    /// <summary>Starts something whose window is made by another process than the one started (a folder by the shell, a document by whatever program opens it).</summary>
    static (int Pid, string? Note) StartShellWindow(Layer layer, string exe, string args, string? cwd)
    {
        var all = AllVisibleWindows();
        var pid = Start(layer, exe, args, cwd);
        ClaimNewWindows(layer, all, pid, shell: true);
        BringUpNewest(layer, all);
        return (pid, null);
    }

    /// <summary>
    /// Waits for the windows a launch brings up and makes them this layer's, so they come to its tile. A window of a
    /// program started on another layer is that layer's; any other new window (made by the shell, by a copy of the
    /// program that was already running, by a Store app) is this one's. Returns a note when nothing came up at all.
    /// </summary>
    static string? ClaimNewWindows(Layer layer, HashSet<IntPtr> before, int pid, bool shell)
    {
        // A shell start (a folder, a document) hands over to another process and ends at once; its window can take
        // a good while when that process is starting cold.
        var start = Environment.TickCount64;
        var any = false;
        var until = start + (shell ? 15_000 : 6000);
        while (Environment.TickCount64 < until)
        {
            var found = false;
            EnumWindows((window, _) =>
            {
                if (before.Contains(window) || !IsWindowVisible(window) || GetWindowTextLength(window) == 0 || GetWindow(window, 4) != IntPtr.Zero) return true;
                if (Containment.InAnyJob(window, out var owner))
                {
                    if (owner == layer.Key) found = true;
                    return true;
                }
                Containment.Claim(window, layer.Key);
                found = true;
                return true;
            }, IntPtr.Zero);
            if (found)
            {
                // Give a further window of the same start (a splash screen, then the main window) a moment to come too.
                any = true;
                before = AllVisibleWindows(before);
                until = Math.Min(start + 8000, Environment.TickCount64 + 700);
            }
            // A program that has already ended without any window has nothing to show.
            else if (!shell && !any && Environment.TickCount64 - start > 3500 && Exited(pid)) return "exited without opening a window.";
            Thread.Sleep(200);
        }
        // Still nothing: the next window that nobody owns is this layer's, when it comes.
        if (!any) Containment.ExpectOne(layer.Key);
        return null;
    }

    static bool Exited(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    /// <summary>Every visible titled window on the screen, whatever tile it is in.</summary>
    static HashSet<IntPtr> AllVisibleWindows(HashSet<IntPtr>? add = null)
    {
        var windows = add is null ? new HashSet<IntPtr>() : new HashSet<IntPtr>(add);
        EnumWindows((window, _) => { if (IsWindowVisible(window) && GetWindowTextLength(window) > 0) windows.Add(window); return true; }, IntPtr.Zero);
        return windows;
    }

    /// <summary>
    /// Windows places a window that a background process opens behind the one in front, so a program the agent
    /// just started could sit under an older window of the tile and look as if it had not opened. Put the newest
    /// windows on top, and make the newest the one this layer works in: it gets the keyboard at the layer's next
    /// turn (now could take it from another agent in the middle of typing).
    /// </summary>
    static void BringUpNewest(Layer layer, HashSet<IntPtr> before)
    {
        // A program can take a moment to settle its windows (a browser that opens a window in an already running
        // process shows it, then rearranges it), so keep raising until the new windows have not changed for a while.
        var newest = IntPtr.Zero;
        var seen = "";
        for (int i = 0, stable = 0; i < 10 && stable < 3; i++)
        {
            var fresh = new List<IntPtr>();                       // top to bottom
            EnumWindows((window, _) =>
            {
                if (!before.Contains(window) && IsWindowVisible(window) && GetWindowTextLength(window) > 0 && GetWindow(window, 4) == IntPtr.Zero && Virtual.InScope(window)) fresh.Add(window);
                return true;
            }, IntPtr.Zero);
            var now = string.Join(",", fresh);
            // The program may also put its window back under the one that was in front when it shows it.
            var covered = fresh.Count > 0 && Virtual.AppWindows().FindIndex(fresh.Contains) > 0;
            if (fresh.Count > 0 && (now != seen || covered))
            {
                // bottom to top, so that their order among themselves is kept and the topmost stays on top
                for (var k = fresh.Count - 1; k >= 0; k--) Virtual.Place(fresh[k], IntPtr.Zero);
                // A plain raise does not get past every window, so put whatever still covers the new ones just below them.
                var apps = Virtual.AppWindows();
                var above = apps.Take(Math.Max(0, apps.FindIndex(fresh.Contains))).Where(w => !fresh.Contains(w)).ToList();
                for (var k = above.Count - 1; k >= 0; k--) Virtual.Place(above[k], fresh[^1]);
                newest = fresh[0];
                seen = now;
                stable = 0;
            }
            else stable++;
            Thread.Sleep(200);
        }
        if (newest == IntPtr.Zero) return;
        Virtual.Activate(newest);
        layer.VirtualTop = newest;
        layer.LastTarget = IntPtr.Zero;
    }

    /// <summary>True when the program's PE header says it is a console program (subsystem 3).</summary>
    static bool IsConsoleProgram(string exe)
    {
        try
        {
            using var file = File.OpenRead(exe);
            using var reader = new BinaryReader(file);
            file.Position = 0x3C;
            var pe = reader.ReadInt32();
            file.Position = pe;
            if (reader.ReadUInt32() != 0x00004550) return false;      // "PE\0\0"
            file.Position = pe + 24 + 68;                              // optional header, Subsystem
            return reader.ReadUInt16() == 3;
        }
        catch { return false; }
    }

    /// <summary>The program registered for https links, when it can be found.</summary>
    static string? DefaultBrowser()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId) return null;
            using var open = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (open?.GetValue(null) is not string line) return null;
            line = line.Trim();
            var exe = line.StartsWith('"') ? line[1..line.IndexOf('"', 1)] : line.Split(' ')[0];
            return File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    /// <summary>Electron apps ship their code as resources\app or resources\app.asar, next to the program or one folder down.</summary>
    static bool IsElectron(string exe)
    {
        var home = Path.GetDirectoryName(exe) ?? "";
        try
        {
            return new[] { home }.Concat(Directory.GetDirectories(home)).Select(d => Path.Combine(d, "resources"))
                .Any(r => File.Exists(Path.Combine(r, "app.asar")) || Directory.Exists(Path.Combine(r, "app")));
        }
        catch { return false; }
    }

    static int Start(Layer layer, string exe, string args, string? cwd)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"WinSta0\Default" };
        // Ask for the first window to open in the tile. Programs that choose their own place are moved there.
        si.dwX = layer.Tile.X + 40;
        si.dwY = layer.Tile.Y + 40;
        si.dwFlags |= 0x4;                                          // STARTF_USEPOSITION
        var commandLine = new StringBuilder($"\"{exe}\" {args}".TrimEnd());
        if (cwd is not null && !Directory.Exists(cwd)) cwd = null;
        // Suspended until it is in the layer's job, so that its children are in the job too.
        if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateNewConsole, IntPtr.Zero, cwd, ref si, out var pi))
            throw new InvalidOperationException($"could not start {Path.GetFileName(exe)} (error {Marshal.GetLastWin32Error()})");
        AssignProcessToJobObject(layer.Job, pi.hProcess);
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }

    /// <summary>Finds a program the way the Run dialog does: a path, a name on PATH, or an App Paths entry.</summary>
    static string? ResolveExecutable(string command)
    {
        if (command.Contains("://")) return null;
        if (File.Exists(command)) return Path.GetExtension(command).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(command) : null;
        if (command.IndexOfAny(['\\', '/']) >= 0) return null;
        var file = Path.HasExtension(command) ? command : command + ".exe";
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(dir.Trim(), file); if (File.Exists(candidate)) return candidate; } catch { }
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{file}");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        return null;
    }

    static string InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) return "";
        try
        {
            var buffer = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, buffer, buffer.Capacity * 2, out _) ? buffer.ToString() : "";
        }
        finally { CloseDesktop(desktop); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int left, top, right, bottom;
    }

    delegate bool EnumProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(int flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool LockSetForegroundWindow(uint code);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? cwd, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
