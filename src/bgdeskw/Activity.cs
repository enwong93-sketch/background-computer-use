using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace BgDesk;

/// <summary>What one agent has been doing on its layer, for the workplace page.</summary>
sealed class Activity
{
    public sealed class Entry
    {
        public long Id;
        public long At;
        public string Op = "";
        public string Summary = "";
        public string Route = "";
        public bool Ok;
        public int Ms;
        /// <summary>Done by the user at the page, not by the agent.</summary>
        public bool User;
        /// <summary>A line about the layer rather than an action (control changed hands, help was asked for).</summary>
        public bool Note;
        /// <summary>A small picture of the layer just after the action. Kept in memory only.</summary>
        public byte[]? Thumb;
    }

    public string Label = "";
    public long Ops;
    public int Inflight;
    public string? InflightSummary;
    public long LastAt = Environment.TickCount64;
    readonly LinkedList<Entry> _log = new();
    const int Keep = 60;
    static long _nextId;

    public Entry Add(Entry e)
    {
        e.Id = Interlocked.Increment(ref _nextId);
        lock (_log)
        {
            _log.AddLast(e);
            // Old actions go first; a note about who had the layer or what was asked of the user stays longer.
            while (_log.Count > Keep)
            {
                var oldest = _log.FirstOrDefault(x => !x.Note);
                if (_log.Count > Keep * 2 || oldest is null) { _log.RemoveFirst(); continue; }
                _log.Remove(oldest);
            }
        }
        return e;
    }

    public void AddNote(string text) =>
        Add(new Entry { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Op = "note", Summary = text, Ok = true, Note = true });

    public Entry[] Recent(int count)
    {
        lock (_log) return _log.Reverse().Take(count).Reverse().ToArray();
    }

    public Entry? Find(long id)
    {
        lock (_log) return _log.FirstOrDefault(e => e.Id == id);
    }
}

static partial class Layers
{
    static readonly ConcurrentDictionary<string, Activity> Activities = new();

    static Activity ActivityOf(string key) => Activities.GetOrAdd(key, _ => new Activity());

    /// <summary>The layer, or null when there is none by that name. Unlike <see cref="Get"/> this never creates one.</summary>
    public static Layer? Peek(string key)
    {
        lock (Gate) return All.TryGetValue(key, out var layer) ? layer : null;
    }

    /// <summary>Runs a call and records it: what it was, how long it took, and whether it worked. By the agent unless user is set.</summary>
    public static JsonObject Track(string key, string? label, string op, string summary, Func<JsonObject> run, bool user = false)
    {
        var activity = ActivityOf(key);
        if (!user && !string.IsNullOrWhiteSpace(label)) activity.Label = label!.Trim();
        Interlocked.Increment(ref activity.Inflight);
        if (!user) activity.InflightSummary = summary;
        var started = Environment.TickCount64;
        var ok = false;
        string route = "";
        try
        {
            var result = run();
            ok = true;
            route = (string?)result["route"] ?? (result["held"] is not null ? "held" : "");
            return result;
        }
        finally
        {
            var now = Environment.TickCount64;
            Interlocked.Decrement(ref activity.Inflight);
            activity.Ops++;
            if (!user) activity.LastAt = now;
            var entry = activity.Add(new Activity.Entry
            {
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Op = op, Summary = summary, Route = route, Ok = ok, Ms = (int)(now - started), User = user,
            });
            if (op == "release") Activities.TryRemove(key, out _);
            else if (op != "screenshot" && op != "windows" && op != "clipboard_read") Workplace.QueueThumb(key, entry);
        }
    }

    /// <summary>A short, safe description of a call. What was typed is never kept: only how much.</summary>
    public static string Summarize(JsonObject q)
    {
        string S(string name) => (string?)q[name] ?? "";
        switch (S("op"))
        {
            case "click":
                var button = S("button") is { Length: > 0 } b && b != "left" ? $" {b}" : "";
                var count = (int?)q["count"] is > 1 and var n ? $" x{n}" : "";
                return $"click{button}{count} at {q["x"]},{q["y"]}";
            case "mouse_move": return $"move to {q["x"]},{q["y"]}";
            case "user_down": return $"press {(S("button") is { Length: > 0 } pb ? pb : "left")} button at {q["x"]},{q["y"]}";
            case "user_up": return $"release at {q["x"]},{q["y"]}";
            case "user_move": return $"drag to {q["x"]},{q["y"]}";
            case "scroll": return $"scroll {(S("direction") is { Length: > 0 } d ? d : "down")} at {q["x"]},{q["y"]}";
            case "drag": return $"drag over {(q["path"] as JsonArray)?.Count ?? 0} points";
            case "key": return $"key {S("keys")}";
            case "type": return $"type {S("text").Length} characters";
            case "launch":
                var command = S("command");
                // A web address is shown as its site only; the rest of it can carry tokens.
                if (Uri.TryCreate(command, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return "open " + uri.Host;
                var program = Path.GetFileName(command.TrimEnd('\\', '/'));
                return program.Length > 0 ? "launch " + program : "launch";
            case "windows": return "list windows";
            case "screenshot": return "screenshot";
            case "clipboard_read": return "read clipboard";
            case "clipboard_write": return $"write clipboard ({S("text").Length} characters)";
            case "release": return "stop";
            case "ask_user": return "ask the user";
            default: return S("op");
        }
    }

    /// <summary>The layers and what each is doing, for the workplace page.</summary>
    public static JsonArray Overview()
    {
        List<Layer> layers;
        lock (Gate) layers = All.Values.OrderBy(l => l.Slot).ToList();
        var front = FrontLayer();
        var now = Environment.TickCount64;
        var list = new JsonArray();
        foreach (var layer in layers)
        {
            Activities.TryGetValue(layer.Key, out var a);
            var inflight = a?.Inflight ?? 0;
            var ago = a is null ? int.MaxValue : (int)Math.Min(int.MaxValue, now - a.LastAt);
            var last = a?.Recent(8).LastOrDefault(e => !e.User && !e.Note);
            var info = new JsonObject
            {
                ["key"] = layer.Key,
                ["slot"] = layer.Slot,
                ["tile"] = new JsonArray(layer.Tile.X, layer.Tile.Y, layer.Tile.Width, layer.Tile.Height),
                ["label"] = a?.Label ?? "",
                ["ops"] = a?.Ops ?? 0,
                ["state"] = layer.Controller == "user" ? "user" : layer.HelpMessage is not null ? "needs" : inflight > 0 || ago < 3000 ? "working" : "idle",
                ["doing"] = inflight > 0 ? a!.InflightSummary : null,
                ["last"] = last?.Summary,
                ["lastAgoMs"] = last is null ? null : ago,
                ["hasKeyboard"] = front == layer.Key,
            };
            foreach (var (name, value) in Layers.ControlInfo(layer)) info[name] = value?.DeepClone();
            list.Add(info);
        }
        return list;
    }

    public static JsonArray RecentActivity(string key, int count)
    {
        var list = new JsonArray();
        if (!Activities.TryGetValue(key, out var a)) return list;
        foreach (var e in a.Recent(count))
            list.Add(new JsonObject
            {
                ["id"] = e.Id, ["at"] = e.At, ["op"] = e.Op, ["summary"] = e.Summary, ["route"] = e.Route, ["ok"] = e.Ok, ["ms"] = e.Ms,
                ["user"] = e.User, ["note"] = e.Note, ["thumb"] = e.Thumb is not null,
            });
        return list;
    }

    public static byte[]? ThumbOf(string key, long id) => Activities.TryGetValue(key, out var a) ? a.Find(id)?.Thumb : null;
}
