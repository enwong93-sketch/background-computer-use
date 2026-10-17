using System.Text.Json.Nodes;

namespace BgDesk;

/// <summary>
/// Who is working a layer: its agent, or the user, who has taken it over from the workplace page. Taking over uses
/// the same screen, the same windows and the same routes for input as the agent does (see <see cref="Fusion"/>),
/// so everything an agent can do there the user can do, and nothing else is shared or changed.
///
/// While the user has a layer, the agent can still look (a picture, the window list, the clipboard) and sees what
/// the user does, but anything that would act is held back for a short while and then answered with a plain
/// "not done" note instead of an error, and is never carried out afterwards: the screen will have changed.
/// An agent that cannot go on without the user (a login, a captcha, a decision) says so with ask_user.
/// </summary>
static partial class Layers
{
    /// <summary>How long an agent's action waits for the user to hand the layer back before it is answered as not done.</summary>
    public static int HeldWaitMs = int.TryParse(Environment.GetEnvironmentVariable("BGDESK_HELDWAIT_MS"), out var h) ? h : 25_000;
    /// <summary>The user's control goes back to the agent after this long without any input from the page.</summary>
    public static int ControlIdleMs = int.TryParse(Environment.GetEnvironmentVariable("BGDESK_CONTROL_IDLE_MS"), out var i) ? i : 120_000;

    /// <summary>Raised when control of a layer changes without the page asking (idle hand-back, the layer going away): layer, reason.</summary>
    public static event Action<string, string>? ControlEnded;

    // What an agent may do while the user has the layer: look.
    static readonly HashSet<string> LookingOps = ["screenshot", "windows", "clipboard_read", "release", "ask_user"];

    static readonly object ControlLock = new();
    /// <summary>The one controlled layer the user touched or took most recently; only that one gets the 32 fps window print.</summary>
    public static string? ActiveUserLayer { get; private set; }

    public static bool IsLooking(string op) => LookingOps.Contains(op);

    public static bool UserHas(string key)
    {
        var layer = Peek(key);
        return layer is not null && layer.Controller == "user";
    }

    /// <summary>The user takes the layer. False when another viewer already has it.</summary>
    public static bool TakeControl(string key, string viewer, out string? holder)
    {
        lock (ControlLock)
        {
            holder = null;
            var layer = Peek(key) ?? throw new KeyNotFoundException("no such layer");
            if (layer.Controller == "user" && layer.ControlBy != viewer) { holder = layer.ControlBy; return false; }
            if (layer.Controller != "user")
            {
                layer.Controller = "user";
                layer.ControlBy = viewer;
                layer.ControlEpoch++;
                if (layer.HelpMessage is not null) layer.HelpTaken = true;
                ActivityOf(key).AddNote("You took over this layer");
            }
            layer.LastUserInput = Environment.TickCount64;
            ActiveUserLayer = key;
            return true;
        }
    }

    /// <summary>The user hands the layer back (or the page went away, or nobody touched it for a while).</summary>
    public static bool ReleaseControl(string key, string? viewer, string reason, string? note = null)
    {
        Layer? layer;
        lock (ControlLock)
        {
            layer = Peek(key);
            if (layer is null || layer.Controller != "user") return false;
            if (viewer is not null && layer.ControlBy != viewer) return false;
            if (reason == "idle" && Environment.TickCount64 - layer.LastUserInput <= ControlIdleMs) return false;
            layer.Controller = "agent";
            layer.UserTouched = true;
            layer.LastEndReason = reason;
            layer.LastEndAt = Environment.TickCount64;
            layer.ControlBy = null;
            layer.ControlEpoch++;
            if (ActiveUserLayer == key) ActiveUserLayer = null;
            if (note is { Length: > 0 }) layer.HelpAnswer = note;
            ActivityOf(key).AddNote(reason switch { "idle" => "Control went back to the agent (no input for a while)", "away" => "Control went back to the agent (the page closed)", _ => "You handed the layer back" });
            Monitor.PulseAll(ControlLock);
        }
        // A drag that was open when control ended must not stay open.
        if (layer.UserButtonDown is { } button)
        {
            layer.UserButtonDown = null;
            try { Layers.Run(layer.Key, l => { Input.ButtonUp(button); return 0; }); } catch { }
        }
        ControlEnded?.Invoke(key, reason);
        return true;
    }

    /// <summary>When a layer is removed, anyone waiting on its control is woken and its active-layer slot is freed.</summary>
    public static void ForgetControl(Layer layer)
    {
        lock (ControlLock)
        {
            layer.Controller = "agent";
            layer.ControlBy = null;
            layer.ControlEpoch++;
            if (ActiveUserLayer == layer.Key) ActiveUserLayer = null;
            Monitor.PulseAll(ControlLock);
        }
    }

    public static void TouchControl(string key)
    {
        lock (ControlLock)
        {
            if (Peek(key) is { Controller: "user" } layer) { layer.LastUserInput = Environment.TickCount64; ActiveUserLayer = key; }
        }
    }

    /// <summary>
    /// An agent's acting call arrives. Null means go ahead. Otherwise the call is not carried out and the text says why:
    /// the user has the layer (after waiting a while for it to come back), or the user worked in it since the agent last
    /// looked, so the agent's picture of the screen is out of date.
    /// </summary>
    public static string? GateAgent(string key)
    {
        var layer = Peek(key);
        if (layer is null) return null;
        lock (ControlLock)
        {
            if (layer.Controller == "user")
            {
                var until = Environment.TickCount64 + HeldWaitMs;
                while (layer.Controller == "user" && Peek(key) == layer && Environment.TickCount64 < until) Monitor.Wait(ControlLock, 200);
                if (Peek(key) != layer) return null;
                if (layer.Controller == "user")
                    return "Not done: the user is working in this layer right now. You can still take a screenshot to see what they do. Try again later, or call ask_user to wait for them.";
                layer.UserTouched = false;
                return "Not done: the user was working in this layer and has just handed it back, so the screen is not what you last saw. Take a screenshot, then continue.";
            }
            if (layer.UserTouched)
            {
                layer.UserTouched = false;
                return "Not done: the user worked in this layer since your last picture, so the screen has changed. Take a screenshot, then continue.";
            }
            return null;
        }
    }

    /// <summary>The agent looked at the screen: what the user did is no longer news to it.</summary>
    public static void AgentLooked(string key)
    {
        if (Peek(key) is { } layer && layer.Controller != "user") layer.UserTouched = false;
    }
    // ---- asking the user ----

    /// <summary>
    /// The agent cannot go on without the user. The page shows the request on the agent's card. The call waits for
    /// the user to take the layer and hand it back, or to dismiss the request, for at most waitMs.
    /// </summary>
    public static string AskUser(string key, string message, int waitMs)
    {
        var layer = Peek(key) ?? throw new KeyNotFoundException("no such layer");
        lock (ControlLock)
        {
            // A request that the user dismissed while the agent was not waiting is answered on the agent's next ask.
            if (layer.HelpDismissed && layer.HelpMessage is null)
            {
                layer.HelpDismissed = false;
                return "The user dismissed the request without doing anything.";
            }
            if (layer.HelpMessage is null)
            {
                layer.HelpMessage = message.Length > 400 ? message[..400] : message;
                layer.HelpAt = Environment.TickCount64;
                layer.HelpTaken = false;
                layer.HelpAnswer = null;
                layer.HelpDismissed = false;
                ActivityOf(key).AddNote("Asked for you: " + layer.HelpMessage);
            }
            var until = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < until)
            {
                // Answered: the user took the layer and gave it back.
                if (layer.HelpTaken && layer.Controller != "user") break;
                if (layer.HelpDismissed) break;
                if (Peek(key) != layer) break;
                Monitor.Wait(ControlLock, 250);
            }
            string result;
            if (layer.HelpDismissed) result = "The user dismissed the request without doing anything.";
            else if (layer.HelpTaken && layer.Controller != "user")
                result = "The user took over and has handed the screen back" + (layer.HelpAnswer is { Length: > 0 } a ? $", saying: {a}" : "") + ". Take a screenshot to see what changed.";
            else
            {
                // Still waiting: the request stays on the page, and asking again carries on waiting for the same one.
                return "No answer yet. The request is still shown to the user; call ask_user again to keep waiting, or carry on without it.";
            }
            layer.HelpMessage = null;
            layer.HelpTaken = false;
            layer.HelpAnswer = null;
            layer.HelpDismissed = false;
            return result;
        }
    }

    public static void DismissHelp(string key)
    {
        lock (ControlLock)
        {
            if (Peek(key) is { HelpMessage: not null } layer) { layer.HelpDismissed = true; layer.HelpMessage = null; }
            Monitor.PulseAll(ControlLock);
        }
    }

    // ---- looking after control that nobody ended ----

    static System.Threading.Timer? _controlTimer;

    public static void StartControlWatch()
    {
        _controlTimer ??= new System.Threading.Timer(_ =>
        {
            try
            {
                List<Layer> layers;
                lock (Gate) layers = All.Values.ToList();
                var now = Environment.TickCount64;
                foreach (var l in layers)
                    if (l.Controller == "user" && now - l.LastUserInput > ControlIdleMs) ReleaseControl(l.Key, null, "idle");
            }
            catch { }
        }, null, 1000, 1000);
    }

    /// <summary>The user's side of a layer for the workplace page.</summary>
    public static JsonObject ControlInfo(Layer layer) => new()
    {
        ["controller"] = layer.Controller,
        ["controlBy"] = layer.ControlBy,
        ["help"] = layer.HelpMessage,
        ["helpAgoMs"] = layer.HelpMessage is null ? null : (int)Math.Min(int.MaxValue, Environment.TickCount64 - layer.HelpAt),
        ["endedReason"] = layer.LastEndReason,
        ["endedAgoMs"] = layer.LastEndReason is null ? null : (int)Math.Min(int.MaxValue, Environment.TickCount64 - layer.LastEndAt),
    };
}
