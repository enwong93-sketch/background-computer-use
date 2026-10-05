using System.Text.Json.Nodes;

namespace BgDesk;

/// <summary>Minimal MCP stdio server (newline-delimited JSON-RPC) exposing the agent desktop.</summary>
static class Mcp
{
    static readonly AgentClient Client = new();
    static readonly string LayerKey = "agent-" + Guid.NewGuid().ToString("N")[..8];

    static JsonObject Tool(string name, string description, JsonObject? props = null, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props ?? new JsonObject(),
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        },
    };

    static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };

    static JsonObject Xy() => new()
    {
        ["x"] = P("integer", "X in screenshot pixels"),
        ["y"] = P("integer", "Y in screenshot pixels"),
    };

    static JsonObject With(this JsonObject o, string key, JsonObject value) { o[key] = value; return o; }

    static JsonObject Input() => P("string", "auto (default) or real. Actions are done without waiting for other agents where that is safe; real forces real input, for an app that ignored one.");

    static JsonArray Tools() =>
    [
        Tool("screenshot", "Capture your layer: your own 1440x900 area of a background Windows desktop that the user does not see and that no other agent shares. Start programs in it with launch. Coordinates for every other tool are pixels of this image."),
        Tool("click", "Click in your layer.",
            Xy().With("button", P("string", "left (default), right or middle"))
                .With("count", P("integer", "1 = single (default), 2 = double, 3 = triple"))
                .With("modifiers", P("string", "Keys held during the click, e.g. ctrl or ctrl+shift"))
                .With("input", Input()), "x", "y"),
        Tool("mouse_move", "Move the pointer (hover).", Xy().With("input", Input()), "x", "y"),
        Tool("drag", "Press at the first point, move through each waypoint and release at the last. Works between different application windows (drag and drop).",
            new JsonObject
            {
                ["path"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Two or more [x, y] points",
                    ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } },
                },
                ["button"] = P("string", "left (default), right or middle"),
                ["modifiers"] = P("string", "Keys held during the drag, e.g. ctrl"),
            }, "path"),
        Tool("scroll", "Scroll the wheel at a position.",
            Xy().With("direction", P("string", "up, down (default), left or right")).With("amount", P("integer", "Wheel notches, default 3")).With("input", Input()), "x", "y"),
        Tool("key", "Press key chords, space separated, e.g. \"ctrl+s\", \"enter\", \"win+r\". Shortcuts that act on the whole screen (Alt+Tab, Win+D and other Win combinations) are refused, since other agents share it.",
            new JsonObject { ["keys"] = P("string", "Chords such as ctrl+a ctrl+c"), ["input"] = Input() }, "keys"),
        Tool("type", "Type text into the focused control.", new JsonObject { ["text"] = P("string", "Text to type"), ["input"] = Input() }, "text"),
        Tool("launch", "Start a program, document, folder or URL in your layer; its windows open there. Chromium browsers and Electron apps get a profile of their own per layer, so they start without the user's logins. When a program only passes its work to a copy already running, the result says so.",
            new JsonObject { ["command"] = P("string", "Executable, file path or URL"), ["args"] = P("string", "Command-line arguments"), ["cwd"] = P("string", "Working directory") }, "command"),
        Tool("list_windows", "List the visible windows in your layer with their rectangles."),
        Tool("clipboard_read", "Read text from your layer's clipboard (separate from the user's clipboard and from other agents')."),
        Tool("clipboard_write", "Put text on your layer's clipboard.", new JsonObject { ["text"] = P("string", "Text") }, "text"),
        Tool("desktop_stop", "Close your layer and everything running on it."),
    ];

    public static void Run()
    {
        var stdout = Console.Out;
        while (Console.In.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject msg;
            try { msg = (JsonObject)JsonNode.Parse(line)!; } catch { continue; }
            var id = msg["id"]?.DeepClone();
            if (id is null) continue; // notification

            var res = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
            switch ((string?)msg["method"])
            {
                case "initialize":
                    res["result"] = new JsonObject
                    {
                        ["protocolVersion"] = (string?)msg["params"]?["protocolVersion"] ?? "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "bgdesk", ["version"] = "1.0.0" },
                    };
                    break;
                case "ping":
                    res["result"] = new JsonObject();
                    break;
                case "tools/list":
                    res["result"] = new JsonObject { ["tools"] = Tools() };
                    break;
                case "tools/call":
                    res["result"] = CallTool((string)msg["params"]!["name"]!, msg["params"]!["arguments"] as JsonObject ?? new JsonObject());
                    break;
                default:
                    res["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" };
                    break;
            }
            stdout.WriteLine(res.ToJsonString());
            stdout.Flush();
        }
    }

    static JsonObject Text(string text, bool error = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    static JsonObject CallTool(string name, JsonObject args)
    {
        try
        {
            Desktop.EnsureUp();
            var q = (JsonObject)args.DeepClone();
            q["op"] = name switch { "list_windows" => "windows", "desktop_stop" => "release", _ => name };
            // Each MCP server process is one agent and works on its own layer, which the agent side
            // creates on first use and removes when this connection ends.
            q["layer"] = LayerKey;
            q["owner"] = name != "desktop_stop";
            var r = Program.Check(Client.Call(q));
            if (name == "desktop_stop") return Text("layer closed");

            switch (name)
            {
                case "screenshot":
                    return new JsonObject
                    {
                        ["content"] = new JsonArray(
                            new JsonObject { ["type"] = "image", ["data"] = (string)r["png"]!, ["mimeType"] = "image/png" },
                            new JsonObject { ["type"] = "text", ["text"] = $"{r["width"]}x{r["height"]}, pointer at {r["cursor"]!.ToJsonString()}" }),
                        ["isError"] = false,
                    };
                case "list_windows":
                    return Text(r["windows"]!.ToJsonString());
                case "clipboard_read":
                    return Text((string)r["text"]!);
                case "launch":
                    return Text((string?)r["note"] ?? $"started (pid {r["pid"]})", r["note"] is not null);
                default:
                    // An action done without taking the front may be ignored by an app; say when nothing showed.
                    var text = r["changed"] is { } c && !(bool)c
                        ? "ok, but nothing visibly changed. Take a screenshot; if the app ignored it, repeat with input=real."
                        : "ok";
                    return Text(r["notice"] is { } notice ? $"{text}. {(string)notice!}" : text);
            }
        }
        catch (Exception ex)
        {
            return Text(ex.Message, true);
        }
    }
}
