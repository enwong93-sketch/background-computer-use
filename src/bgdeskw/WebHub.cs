using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BgDesk;

/// <summary>
/// The agent workplace: a web page, served by the host on the loopback address only, that shows every agent's layer
/// live. The host lives in the user's session and outlasts the agent, so the page keeps working while a session
/// restarts. Everything the page learns comes from the agent over its named pipe; nothing here touches the desktop.
///
/// Who may use it: only a program on this machine (the listener is bound to 127.0.0.1, and every request is checked
/// again), that knows the token (kept in the user's data folder), and, for the live connection, that comes from
/// the page itself (Origin). The page is plain files served from this program; it loads nothing from elsewhere.
/// </summary>
static class WebHub
{
    const string CookieName = "bgdesk";
    const int MaxViewers = 8;
    // A layer in focus is asked for a picture this often. The session's display itself changes about 11 times a second
    // at most (measured, with a plain GDI window), so asking a little faster than that loses nothing.
    const int FocusGapMs = 55, ControlGapMs = 4;

    static HttpListener? _listener;
    static string _token = "";
    static string _origin = "";
    static int _viewers;
    static volatile bool _stopping;

    public static void Start()
    {
        _token = Common.WebToken();
        // The address has to stay the same from one start to the next (the installed app and its sign-in belong to it), so the
        // candidates are a fixed list, tried in order, and the first free one is kept. Some ranges are not available on some
        // machines (another program or Windows has them reserved: the bind fails), hence several ranges. The last resort lets
        // Windows choose, and then the address changes.
        var ports = new List<int> { Common.WebPreferredPort };
        ports.AddRange(Enumerable.Range(0, 10).Select(i => Common.WebPreferredPort + 1 + i));
        ports.AddRange(Enumerable.Range(0, 10).Select(i => 52631 + i));
        ports.AddRange(Enumerable.Range(0, 10).Select(i => 38631 + i));
        ports.AddRange(Enumerable.Range(0, 10).Select(i => 20631 + i));
        for (var i = 0; i < 20; i++) ports.Add(Random.Shared.Next(49152, 65000));
        foreach (var port in ports)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException) { listener.Close(); continue; }
            _listener = listener;
            Common.WebPort = port;
            _origin = $"http://127.0.0.1:{port}";
            new Thread(AcceptLoop) { IsBackground = true, Name = "web hub" }.Start();
            return;
        }
        // No port: the workplace is simply not offered. Nothing else depends on it.
    }

    public static void Stop()
    {
        _stopping = true;
        try { _listener?.Close(); } catch { }
    }

    static void AcceptLoop()
    {
        while (!_stopping && _listener is { IsListening: true } listener)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (Exception) when (_stopping || !listener.IsListening) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception) { Thread.Sleep(50); continue; }
            Task.Run(() => Handle(ctx));
        }
    }

    static async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var request = ctx.Request;
            // Defence in depth: the listener is loopback-only already.
            if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address) || !IPAddress.IsLoopback(request.LocalEndPoint.Address)
                || request.UserHostName != $"127.0.0.1:{Common.WebPort}")
            { Reply(ctx, 400, "bad request"); return; }

            var path = request.Url!.AbsolutePath;
            var viaLink = request.QueryString["t"];
            if (path == "/" && viaLink is not null)
            {
                // The link that `bgdesk workplace` opens: keep the token in a cookie the page cannot read, then drop it from the address.
                if (!TokenOk(viaLink)) { Reply(ctx, 403, "wrong token"); return; }
                ctx.Response.Headers.Add("Set-Cookie", $"{CookieName}={_token}; Path=/; Max-Age=2592000; HttpOnly; SameSite=Strict");
                ctx.Response.Redirect("/");
                ctx.Response.Close();
                return;
            }
            if (!TokenOk(request.Cookies[CookieName]?.Value)) { Reply(ctx, 401, "Open the workplace with: bgdesk workplace"); return; }
            if (request.HttpMethod != "GET") { Reply(ctx, 405, "method not allowed"); return; }

            if (path == "/ws") { await Socket(ctx); return; }
            if (path == "/api/state") { Json(ctx, State()); return; }
            if (path.StartsWith("/api/layer/", StringComparison.Ordinal))
            {
                var parts = path["/api/layer/".Length..].Split('/');
                if (parts.Length == 2 && parts[1] is "windows" or "activity")
                { Json(ctx, Ask(new JsonObject { ["op"] = "workplace." + parts[1], ["layer"] = Uri.UnescapeDataString(parts[0]), ["count"] = int.TryParse(request.QueryString["n"], out var n) ? Math.Clamp(n, 1, 60) : 40 })); return; }
            }
            if (path.StartsWith("/thumb/", StringComparison.Ordinal) && path.EndsWith(".jpg", StringComparison.Ordinal))
            {
                var parts = path["/thumb/".Length..^4].Split('/');
                if (parts.Length == 2 && long.TryParse(parts[1], out var id)
                    && Ask(new JsonObject { ["op"] = "workplace.thumb", ["layer"] = Uri.UnescapeDataString(parts[0]), ["id"] = id })["jpeg"] is JsonValue jv)
                {
                    ctx.Response.Headers["Cache-Control"] = "private, max-age=86400, immutable";
                    Send(ctx, 200, "image/jpeg", Convert.FromBase64String((string)jv!));
                    return;
                }
                Reply(ctx, 404, "no picture"); return;
            }
            if (path.StartsWith("/frame/", StringComparison.Ordinal) && path.EndsWith(".jpg", StringComparison.Ordinal))
            {
                var key = Uri.UnescapeDataString(path["/frame/".Length..^4]);
                var r = Ask(new JsonObject
                {
                    ["op"] = "workplace.frame", ["layer"] = key,
                    ["width"] = int.TryParse(request.QueryString["w"], out var w) ? w : 1440,
                    ["quality"] = int.TryParse(request.QueryString["q"], out var q) ? q : 70,
                });
                if (r["jpeg"] is null) { Reply(ctx, 404, (string?)r["error"] ?? "no picture"); return; }
                Send(ctx, 200, "image/jpeg", Convert.FromBase64String((string)r["jpeg"]!));
                return;
            }
            var file = path == "/" ? "index.html" : path.TrimStart('/');
            if (Asset(file) is { } asset) { Send(ctx, 200, ContentType(file), asset); return; }
            Reply(ctx, 404, "not found");
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        catch (InvalidOperationException) { try { ctx.Response.Abort(); } catch { } }
        catch { try { Reply(ctx, 500, "error"); } catch { } }
    }

    static bool TokenOk(string? given) =>
        given is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(_token));

    // ---- what the agent says ------------------------------------------------------------------------------------

    static readonly object AgentLock = new();
    static AgentClient? _shared;

    /// <summary>One question to the agent over a connection shared by the plain requests; an agent that is not there answers with an error.</summary>
    static JsonObject Ask(JsonObject question)
    {
        lock (AgentLock)
        {
            try { return (_shared ??= new AgentClient()).Call(question, 300); }
            catch (Exception ex) { _shared?.Dispose(); _shared = null; return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
        }
    }

    static JsonObject State()
    {
        var layers = Ask(new JsonObject { ["op"] = "workplace.layers" });
        return new JsonObject
        {
            ["agent"] = (bool?)layers["ok"] == true,
            ["layers"] = layers["layers"]?.DeepClone(),
            ["canvas"] = layers["canvas"]?.DeepClone(),
            ["host"] = (string?)Common.ReadState()?["state"],
        };
    }

    // ---- the live connection ------------------------------------------------------------------------------------

    /// <summary>
    /// Server to page: text messages (the layers about once a second and at once when control changes; a reply for each
    /// input) and binary messages with a picture (4 bytes: header length; the header as JSON; the JPEG).
    /// Page to server: which layers to watch and how closely ("thumb": small, about once a second; "focus": large, as
    /// fast as the display changes), taking and handing back a layer, and, while holding one, its input.
    /// A layer's picture is only sent when it has changed.
    /// </summary>
    static async Task Socket(HttpListenerContext ctx)
    {
        if (!ctx.Request.IsWebSocketRequest || ctx.Request.Headers["Origin"] != _origin) { Reply(ctx, 403, "forbidden"); return; }
        var count = Interlocked.Increment(ref _viewers);
        if (count > MaxViewers) { Interlocked.Decrement(ref _viewers); Reply(ctx, 503, "too many viewers"); return; }
        if (count == 1) timeBeginPeriod(1);
        var viewer = Guid.NewGuid().ToString("N");
        var held = new HashSet<string>();
        try
        {
            var socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            using var cts = new CancellationTokenSource();
            var watch = new Dictionary<string, (string Mode, int Width, int Quality)>();
            var watchLock = new object();
            var outbox = new BlockingCollection<string>(512);
            var inbox = new BlockingCollection<JsonObject>(256);
            var wake = new ManualResetEventSlim();

            // One thread speaks to the agent for the user's input, in order, so that a click is never overtaken by what came after it.
            var doer = new Thread(() =>
            {
                using var agent = new AgentClient();
                foreach (var m in inbox.GetConsumingEnumerable())
                {
                    try
                    {
                        long? id = m["id"] is JsonValue idv && idv.TryGetValue<long>(out var idn) ? idn : null;
                        var type = m["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
                        var layer = m["layer"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : "";
                        var started = Environment.TickCount64;
                        JsonObject reply;
                        try
                        {
                            switch (type)
                            {
                                case "take":
                                    reply = agent.Call(new JsonObject { ["op"] = "workplace.control", ["layer"] = layer, ["viewer"] = viewer, ["action"] = "take" }, 500);
                                    if (reply["taken"] is JsonValue tkv && tkv.TryGetValue<bool>(out var tkb) && tkb) lock (held) held.Add(layer);
                                    break;
                                case "release":
                                    var note = m["note"] is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : null;
                                    reply = agent.Call(new JsonObject { ["op"] = "workplace.control", ["layer"] = layer, ["viewer"] = viewer, ["action"] = "release", ["note"] = Cut(note, 300) }, 500);
                                    lock (held) held.Remove(layer);
                                    break;
                                case "dismiss":
                                    reply = agent.Call(new JsonObject { ["op"] = "workplace.control", ["layer"] = layer, ["viewer"] = viewer, ["action"] = "dismiss" }, 500);
                                    break;
                                default:
                                    m["op"] = "workplace.input"; m["viewer"] = viewer;
                                    reply = agent.Call(m, 500);
                                    break;
                            }
                        }
                        catch (Exception ex) { reply = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                        // A reply for each input would be a flood (pointer moves come many times a second); the page hears about failures, and about any input that asks.
                        var ok = reply["ok"] is JsonValue okv && okv.TryGetValue<bool>(out var okb) && okb;
                        var wantsAck = m["ack"] is JsonValue av && av.TryGetValue<bool>(out var ab) && ab;
                        if (type == "input" && ok && !wantsAck) continue;
                        outbox.TryAdd(new JsonObject
                        {
                            ["type"] = "ack", ["id"] = id, ["kind"] = type, ["layer"] = layer,
                            ["ok"] = ok, ["error"] = reply["error"]?.DeepClone(), ["route"] = reply["route"]?.DeepClone(),
                            ["taken"] = reply["taken"]?.DeepClone(), ["holder"] = reply["holder"]?.DeepClone(), ["ms"] = (int)(Environment.TickCount64 - started),
                        }.ToJsonString());
                        wake.Set();
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "workplace input" };
            doer.Start();

            var reader = Task.Run(async () =>
            {
                var buffer = new byte[16384];
                var recent = new Queue<long>();
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var length = 0;
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), cts.Token);
                            length += result.Count;
                        } while (!result.EndOfMessage && length < buffer.Length);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (!result.EndOfMessage) break;                                   // longer than anything the page sends
                        // At most 600 messages a second: a page cannot flood the agent.
                        var now = Environment.TickCount64;
                        recent.Enqueue(now);
                        while (recent.Count > 0 && now - recent.Peek() > 1000) recent.Dequeue();
                        if (recent.Count > 600) break;
                        try
                        {
                            var m = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, length)) as JsonObject;
                            switch (m?["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null)
                            {
                                case "watch" when m!["layers"] is JsonObject layers:
                                    lock (watchLock)
                                    {
                                        watch.Clear();
                                        foreach (var (key, spec) in layers.Take(16))
                                        {
                                            var focus = spec?["mode"] is JsonValue mv && mv.TryGetValue<string>(out var ms) && ms == "focus";
                                            int Num(string name, int fallback) => spec?[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;
                                            watch[key] = focus
                                                ? ("focus", Math.Clamp(Num("width", 1440), 320, 1440), Math.Clamp(Num("quality", 70), 30, 90))
                                                : ("thumb", 360, 55);
                                        }
                                    }
                                    wake.Set();
                                    break;
                                case "take" or "release" or "dismiss" or "input":
                                    if (m!["layer"] is JsonValue lv && lv.TryGetValue<string>(out _) && !inbox.TryAdd(m))
                                        outbox.TryAdd("{\"type\":\"ack\",\"ok\":false,\"error\":\"too many inputs waiting\"}");
                                    break;
                            }
                        }
                        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
                    }
                }
                catch { }
                cts.Cancel();
            });

            try { await Task.Run(() => Push(socket, watch, watchLock, held, outbox, wake, viewer, cts.Token)); }
            catch { }
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closing = new CancellationTokenSource(2000);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token);
                }
            }
            catch { }
            cts.Cancel();
            try { await reader; } catch { }
            socket.Dispose();
            // A page that goes away gives back whatever it held, and a drag it had open is let go. The input thread finishes
            // first, so that a take it was carrying out is in the list.
            inbox.CompleteAdding();
            doer.Join(3000);
            string[] mine; lock (held) mine = held.ToArray();
            if (mine.Length > 0)
                try
                {
                    using var agent = new AgentClient();
                    foreach (var layer in mine) agent.Call(new JsonObject { ["op"] = "workplace.control", ["layer"] = layer, ["viewer"] = viewer, ["action"] = "away" }, 1000);
                }
                catch { }
        }
        finally { if (Interlocked.Decrement(ref _viewers) == 0) timeEndPeriod(1); }
    }

    static string? Cut(string? text, int length) => text is null ? null : text.Length <= length ? text : text[..length];

    static void Push(WebSocket socket, Dictionary<string, (string Mode, int Width, int Quality)> watch, object watchLock, HashSet<string> held, BlockingCollection<string> outbox, ManualResetEventSlim wake, string viewer, CancellationToken token)
    {
        using var agent = new AgentClient();
        var revs = new Dictionary<string, long>();
        var nextFrame = new Dictionary<string, long>();
        string? lastLayers = null;
        long nextLayers = 0;

        void SendBytes(byte[] bytes, WebSocketMessageType type)
        {
            // A page that stopped reading (a frozen tab) must not hold this thread: give up after a few seconds.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(5000);
            try { socket.SendAsync(bytes, type, true, limit.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { socket.Abort(); throw new WebSocketException("the page stopped reading"); }
        }
        void SendText(string text) => SendBytes(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                // Replies to the page's own requests go out first, and bring the layers list forward: a take or a hand-back shows at once.
                wake.Reset();
                while (outbox.TryTake(out var reply)) { SendText(reply); nextLayers = 0; }

                var now = Environment.TickCount64;
                if (now >= nextLayers)
                {
                    nextLayers = now + 1000;
                    JsonObject r;
                    try { r = agent.Call(new JsonObject { ["op"] = "workplace.layers" }, 300); }
                    catch (Exception) { r = new JsonObject { ["ok"] = false }; }
                    var list = r["layers"] as JsonArray;
                    // Which layer the user has here, as this page sees it.
                    if (list is not null) foreach (var l in list) if (l is JsonObject o) { o["you"] = (string?)o["controlBy"] == viewer; o.Remove("controlBy"); }
                    var message = new JsonObject
                    {
                        ["type"] = "layers", ["agent"] = (bool?)r["ok"] == true,
                        ["layers"] = list?.DeepClone(), ["canvas"] = r["canvas"]?.DeepClone(),
                    }.ToJsonString();
                    if (message != lastLayers) { SendText(message); lastLayers = message; }
                }

                List<(string Key, (string Mode, int Width, int Quality) Spec)> due;
                lock (watchLock)
                    due = watch.Where(w => !nextFrame.TryGetValue(w.Key, out var at) || now >= at).Select(w => (w.Key, w.Value)).ToList();
                foreach (var (key, spec) in due)
                {
                    bool controlling; lock (held) controlling = held.Contains(key);
                    nextFrame[key] = now + (spec.Mode == "focus" ? (controlling ? ControlGapMs : FocusGapMs) : 1000);
                    var r = agent.Call(new JsonObject
                    {
                        ["op"] = "workplace.frame", ["layer"] = key, ["since"] = revs.GetValueOrDefault(key + "/" + spec.Mode, -1),
                        ["width"] = spec.Width, ["quality"] = spec.Quality,
                    }, 300);
                    if (r["ok"] is not JsonValue okv || !okv.TryGetValue<bool>(out var okb) || !okb) continue;
                    if (r["unchanged"] is JsonValue uv && uv.TryGetValue<bool>(out var ub) && ub) continue;
                    if (r["rev"] is not JsonValue rv || !rv.TryGetValue<long>(out var rev) || r["jpeg"] is not JsonValue jv || !jv.TryGetValue<string>(out var b64)) continue;
                    revs[key + "/" + spec.Mode] = rev;
                    var header = Encoding.UTF8.GetBytes(new JsonObject
                    {
                        ["layer"] = key, ["rev"] = rev, ["width"] = r["width"]?.DeepClone(), ["height"] = r["height"]?.DeepClone(),
                        ["mode"] = spec.Mode, ["cursor"] = r["cursor"]?.DeepClone(),
                    }.ToJsonString());
                    var jpeg = Convert.FromBase64String(b64);
                    var packet = new byte[4 + header.Length + jpeg.Length];
                    packet[0] = (byte)(header.Length >> 24); packet[1] = (byte)(header.Length >> 16); packet[2] = (byte)(header.Length >> 8); packet[3] = (byte)header.Length;
                    header.CopyTo(packet, 4);
                    jpeg.CopyTo(packet, 4 + header.Length);
                    SendBytes(packet, WebSocketMessageType.Binary);
                }
                // Wait for whatever is due next, or for a reply to send.
                var next = nextLayers;
                lock (watchLock) foreach (var key in watch.Keys) next = Math.Min(next, nextFrame.GetValueOrDefault(key, 0));
                var wait = next - Environment.TickCount64;
                if (wait > 0) wake.Wait((int)Math.Min(wait, 1000), token);
            }
            catch (OperationCanceledException) { return; }
            catch (WebSocketException) { return; }
            catch (Exception)
            {
                // The agent is not answering (the session is restarting, say). The page is told by the next layers message; try again soon.
                Thread.Sleep(500);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    // ---- plain replies -------------------------------------------------------------------------------------------

    static void Harden(HttpListenerResponse r)
    {
        if (r.Headers["Cache-Control"] is null) r.Headers["Cache-Control"] = "no-store";
        r.Headers["X-Content-Type-Options"] = "nosniff";
        r.Headers["Referrer-Policy"] = "no-referrer";
        r.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        r.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }

    static void Send(HttpListenerContext ctx, int status, string type, byte[] body)
    {
        Harden(ctx.Response);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.Close();
    }

    static void Reply(HttpListenerContext ctx, int status, string text) => Send(ctx, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    static void Json(HttpListenerContext ctx, JsonObject o) => Send(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(o.ToJsonString()));

    static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".webmanifest" => "application/manifest+json",
        _ => "application/octet-stream",
    };

    /// <summary>The page's files are part of the program (resources named web/...), so there is no folder to browse or escape from.</summary>
    static byte[]? Asset(string file)
    {
        // Only a plain file name: nothing with a path in it is ever looked up.
        if (file.Length == 0 || file.IndexOfAny(['/', '\\', ':']) >= 0 || file.Contains("..")) return null;
        // Working on the page: with BGDESK_WEBDIR set (before the host starts) the files are read from that folder instead.
        if (Environment.GetEnvironmentVariable("BGDESK_WEBDIR") is { Length: > 0 } dir && Path.Combine(dir, file) is var path && File.Exists(path)) return File.ReadAllBytes(path);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + file);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
