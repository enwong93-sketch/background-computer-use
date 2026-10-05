using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace BgDesk;

/// <summary>
/// Decides, per action, whether it can be done in place with window messages (no turn, no desktop switch,
/// so other agents are not affected) or needs real input (a turn at the front). The rule is conservative:
/// an action goes the in-place way only for a kind of window and a kind of action that was tested to work
/// that way. Everything else, and anything in doubt, uses real input, which works everywhere.
/// All methods run on the layer's own thread.
/// </summary>
static class Fusion
{
    public enum Outcome { Done, NeedsRealInput, OpenedPopup }

    // Tested: Chromium browsers and Electron apps, File Explorer, consoles, classic dialogs.
    const string Chromium = "Chrome_WidgetWin_1", Explorer = "CabinetWClass", Console = "ConsoleWindowClass", Dialog = "#32770";
    static readonly HashSet<string> ClickTops = [Chromium, Explorer, Console, Dialog];
    // Inside a classic dialog only the standard controls are known to take message input.
    static readonly string[] DialogControls = ["Button", "Edit", "RICHEDIT", "RichEdit", "Static", "SysListView32", "SysTreeView32", "ListBox", "SysTabControl32"];

    public static Outcome Click(Layer layer, Point at, int count = 1, bool ctrl = false, bool shift = false)
    {
        if (MenuOpen()) return Outcome.NeedsRealInput;
        var top = Virtual.TopLevelAt(at);
        if (top == IntPtr.Zero) return Outcome.NeedsRealInput;
        var topClass = Virtual.ClassOf(top);
        // Pop-ups (drop-down lists, menus, bubbles) track the real mouse; only main windows qualify.
        if (!ClickTops.Contains(topClass) || IsPopup(top)) return Outcome.NeedsRealInput;
        var target = Virtual.Deepest(top, at);
        if (!Virtual.InClientArea(target, at)) return Outcome.NeedsRealInput;
        if (topClass == Dialog && target != top && !DialogControls.Any(c => Virtual.ClassOf(target).StartsWith(c, StringComparison.OrdinalIgnoreCase)))
            return Outcome.NeedsRealInput;

        var before = Popups();
        if (layer.VirtualTop != top) Virtual.Activate(top);
        var picture = Virtual.Snapshot(top);
        Virtual.Click(target, at, count, ctrl, shift);
        layer.VirtualTop = top;
        layer.LastTarget = target;
        layer.Pointer = at;
        layer.Hovering = false;
        // A click that opens a list or menu has to be continued with real input.
        Thread.Sleep(180);
        layer.LastChanged = null;
        if (Popups().Any(p => !before.Contains(p)) || MenuOpen()) return Outcome.OpenedPopup;
        Measure(layer, top, picture, 0);
        return Outcome.Done;
    }

    /// <summary>
    /// Records whether the window visibly changed since <paramref name="before"/>, so the agent can be told when an
    /// in-place action seems to have been ignored. A slow reaction gets a second look.
    /// </summary>
    static void Measure(Layer layer, IntPtr top, byte[]? before, int waitMs)
    {
        if (waitMs > 0) Thread.Sleep(waitMs);
        var changed = Virtual.Differs(before, Virtual.Snapshot(top));
        if (changed == false)
        {
            Thread.Sleep(250);
            changed = Virtual.Differs(before, Virtual.Snapshot(top));
        }
        layer.LastChanged = changed;
    }

    public static Outcome Type(Layer layer, string text)
    {
        // Line breaks and tabs are sent as the Enter and Tab keys; any other control character is left to real input.
        if (text.Any(c => c < 0x20 && c is not ('\n' or '\r' or '\t')) || MenuOpen()) return Outcome.NeedsRealInput;
        var top = layer.VirtualTop != IntPtr.Zero ? layer.VirtualTop : layer.LastForeground;
        if (top == IntPtr.Zero || !IsWindow(top)) return Outcome.NeedsRealInput;
        // Typing goes to whatever window is on top. If that is no longer the window this layer last worked
        // in (a dialog opened, a new window appeared), only real input is sure to reach the right place.
        if (Virtual.TopOfZOrder() != top) return Outcome.NeedsRealInput;
        var topClass = Virtual.ClassOf(top);
        if (topClass is not (Chromium or Console or Dialog or Explorer)) return Outcome.NeedsRealInput;
        // A window that was in front for a real turn and has been left since is no longer the active one, and a
        // browser then ignores typed characters. Make it active again, as the click that began this work did. This
        // comes before looking for the focus: a program with several windows has one focus for all of them, which
        // belongs to whichever window was active until now.
        Virtual.Activate(top);
        var focus = Virtual.FocusOf(layer.LastTarget != IntPtr.Zero && IsWindow(layer.LastTarget) ? layer.LastTarget : top);
        var focusClass = Virtual.ClassOf(focus);
        var known = topClass is Chromium or Console
            || (topClass is Dialog or Explorer && (focusClass.StartsWith("Edit", StringComparison.OrdinalIgnoreCase) || focusClass.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase)));
        if (!known) return Outcome.NeedsRealInput;
        var picture = Virtual.Snapshot(top);
        Virtual.Type(focus, text);
        Measure(layer, top, picture, 120);
        return Outcome.Done;
    }

    /// <summary>
    /// Key presses and chords as messages. Tested in Chromium browsers and Electron apps, File Explorer, consoles and
    /// classic dialogs: Enter, Tab, Esc, arrows, Home, End, Page Up and Down, Backspace, Delete, Insert, F-keys, letters
    /// and digits, with Ctrl and Shift, and with Alt for Alt+F4 and Alt+arrows. Left to real input: anything with the
    /// Windows key; chords that use the clipboard (it belongs to whoever has the keyboard); other Alt chords, which
    /// open menus; Ctrl+C and Ctrl+Break in a console, which a console does not take as messages.
    /// </summary>
    public static Outcome Key(Layer layer, string keys)
    {
        if (MenuOpen()) return Outcome.NeedsRealInput;
        var chords = new List<(ushort Vk, bool Ctrl, bool Shift, bool Alt)>();
        foreach (var chord in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool ctrl = false, shift = false, alt = false;
            ushort main = 0;
            var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl" or "control": ctrl = true; continue;
                    case "shift": shift = true; continue;
                    case "alt": alt = true; continue;
                    case "win" or "cmd" or "super": return Outcome.NeedsRealInput;
                }
                if (main != 0 || !Input.TryResolve(part, out main)) return Outcome.NeedsRealInput;
            }
            if (main == 0) return Outcome.NeedsRealInput;
            if (ctrl && main is 0x43 or 0x58 or 0x56 or 0x2D or 0x13) return Outcome.NeedsRealInput;              // C, X, V, Insert, Pause
            if (shift && main is 0x2D or 0x2E) return Outcome.NeedsRealInput;                                      // Shift+Insert, Shift+Delete
            if (alt && !(main == 0x73 || (!ctrl && !shift && main is 0x25 or 0x26 or 0x27 or 0x28 or 0x24))) return Outcome.NeedsRealInput;
            if (main is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0x5D or 0x70 + 9 /* F10 */) return Outcome.NeedsRealInput;
            chords.Add((main, ctrl, shift, alt));
        }
        if (chords.Count == 0) return Outcome.NeedsRealInput;

        var top = layer.VirtualTop != IntPtr.Zero ? layer.VirtualTop : layer.LastForeground;
        if (top == IntPtr.Zero || !IsWindow(top) || Virtual.TopOfZOrder() != top) return Outcome.NeedsRealInput;
        var topClass = Virtual.ClassOf(top);
        if (topClass is not (Chromium or Explorer or Console or Dialog)) return Outcome.NeedsRealInput;
        if (topClass == Console && chords.Any(c => c.Ctrl && c.Vk is 0x43 or 0x03)) return Outcome.NeedsRealInput;

        Virtual.Activate(top);
        var focus = Virtual.FocusOf(layer.LastTarget != IntPtr.Zero && IsWindow(layer.LastTarget) ? layer.LastTarget : top);
        var picture = Virtual.Snapshot(top);
        foreach (var c in chords) Virtual.Chord(focus, c.Vk, c.Ctrl, c.Shift, c.Alt);
        Measure(layer, top, picture, 150);
        return Outcome.Done;
    }

    /// <summary>The pointer moved over a Chromium page or Electron app (hover effects), as a message.</summary>
    public static Outcome Move(Layer layer, Point at)
    {
        if (MenuOpen()) return Outcome.NeedsRealInput;
        var top = Virtual.TopLevelAt(at);
        if (top == IntPtr.Zero || Virtual.ClassOf(top) != Chromium || IsPopup(top)) return Outcome.NeedsRealInput;
        var target = Virtual.Deepest(top, at);
        if (!Virtual.InClientArea(target, at)) return Outcome.NeedsRealInput;
        Virtual.Hover(target, at);
        layer.Pointer = at;
        layer.Hovering = false;
        return Outcome.Done;
    }

    public static Outcome Scroll(Layer layer, Point at, string direction, int amount)
    {
        if (MenuOpen()) return Outcome.NeedsRealInput;
        var top = Virtual.TopLevelAt(at);
        if (top == IntPtr.Zero || Virtual.ClassOf(top) is not (Chromium or Explorer or Dialog) || IsPopup(top)) return Outcome.NeedsRealInput;   // a console moves only a little for a wheel message: real input
        var picture = Virtual.Snapshot(top);
        Virtual.Activate(top);
        Virtual.Scroll(Virtual.Deepest(top, at), at, direction, amount);
        layer.Pointer = at;
        Measure(layer, top, picture, 150);
        return Outcome.Done;
    }

    /// <summary>True when a pop-up that appeared after an in-place click is still there.</summary>
    public static bool PopupStillOpen() => Popups().Count > 0 || MenuOpen();

    static bool MenuOpen()
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !Virtual.InScope(window)) return true;
            if (Virtual.ClassOf(window) is "#32768" or "ComboLBox" or "DropDown") { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Visible windows that belong to another window and have no title bar: lists, menus, bubbles.</summary>
    static HashSet<IntPtr> Popups()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((window, _) => { if (IsWindowVisible(window) && Virtual.InScope(window) && IsPopup(window)) set.Add(window); return true; }, IntPtr.Zero);
        return set;
    }

    static bool IsPopup(IntPtr window)
    {
        if (Virtual.ClassOf(window) is "tooltips_class32") return false;
        const long caption = 0x00C00000;
        return GetWindow(window, 4) != IntPtr.Zero && (GetWindowLongPtr(window, -16).ToInt64() & caption) != caption;
    }

    delegate bool EnumProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
