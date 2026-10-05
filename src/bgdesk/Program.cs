using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace BgDesk;

static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        try
        {
            switch (args.FirstOrDefault())
            {
                case "mcp":
                    Mcp.Run();
                    return 0;
                case "install":
                case "uninstall":
                    return Install.Run(args.Skip(1).ToArray(), args[0] == "uninstall");
                case "enable":
                case "disable":
                    var on = args[0] == "enable";
                    if (!Common.WTSEnableChildSessions(on))
                        throw new InvalidOperationException($"WTSEnableChildSessions failed (error {Marshal.GetLastWin32Error()}); run from an elevated terminal");
                    Console.WriteLine(on ? "child sessions enabled" : "child sessions disabled");
                    return 0;
                case "status":
                    Common.WTSIsChildSessionsEnabled(out var enabled);
                    var st = Common.ReadState();
                    Console.WriteLine(new JsonObject
                    {
                        ["childSessionsEnabled"] = enabled,
                        ["hostAlive"] = Common.HostAlive(st),
                        ["host"] = st?.DeepClone(),
                        ["agentReachable"] = AgentClient.Reachable(),
                    }.ToJsonString());
                    return 0;
                case "up":
                    Desktop.EnsureUp(args.Length > 1 ? int.Parse(args[1]) : 0, args.Length > 2 ? int.Parse(args[2]) : 0);
                    Console.WriteLine("agent desktop ready");
                    return 0;
                case "down":
                    Desktop.Down();
                    return 0;
                case "shot" when args.Length > 1:
                    Desktop.EnsureUp();
                    using (var c = new AgentClient())
                    {
                        var r = Check(c.Call(new JsonObject { ["op"] = "screenshot" }));
                        File.WriteAllBytes(args[1], Convert.FromBase64String((string)r["png"]!));
                        Console.WriteLine($"{r["width"]}x{r["height"]} -> {args[1]}");
                    }
                    return 0;
                case "call" when args.Length > 1:
                    using (var c = new AgentClient())
                    {
                        var q = args.Length > 2 ? (JsonObject)JsonNode.Parse(args[2])! : new JsonObject();
                        q["op"] = args[1];
                        Console.WriteLine(c.Call(q).ToJsonString());
                    }
                    return 0;
                default:
                    Console.Error.WriteLine("usage: bgdesk mcp | install <agent> | uninstall <agent> | up [w h] | down | status | shot <file.png> | call <op> [json] | enable | disable");
                    Console.Error.WriteLine("agents: " + Install.Agents);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static JsonObject Check(JsonObject r) =>
        (bool)r["ok"]! ? r : throw new InvalidOperationException((string?)r["error"] ?? "agent error");
}

static class Desktop
{
    public static void EnsureUp(int width = 0, int height = 0, int timeoutSec = 120)
    {
        if (Ready()) return;
        if (width <= 0 || height <= 0) (width, height) = (Common.CanvasWidth, Common.CanvasHeight);

        if (!Common.WTSIsChildSessionsEnabled(out var enabled) || !enabled)
            throw new InvalidOperationException("Windows child sessions are not enabled. Run `bgdesk enable` once from an elevated terminal.");

        // The agent can outlive its host (the session is then disconnected and has no display), so a reachable
        // agent is not enough. Starting the host again reconnects to the same session with its apps intact.
        if (!Common.HostAlive(Common.ReadState())) StartHost(width, height);

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        DateTime? silentSince = null;
        var restarted = false;
        while (DateTime.UtcNow < deadline)
        {
            if (Ready()) return;
            var st = Common.ReadState();
            if ((string?)st?["state"] is "error" or "disconnected")
                throw new InvalidOperationException("agent desktop failed to start: " + (string?)st!["detail"]);
            // The user is typing the password; that time does not count.
            if ((string?)st?["state"] == "credentials") deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            // A session that is logged on but whose agent never answers cannot be used by anyone: its agent did not
            // start. Log it off and start once more.
            if ((string?)st?["state"] == "logged-in" && Common.HostAlive(st) && !AgentClient.Reachable(200))
            {
                silentSince ??= DateTime.UtcNow;
                if (!restarted && DateTime.UtcNow - silentSince > TimeSpan.FromSeconds(45))
                {
                    restarted = true;
                    if (Common.WTSGetChildSessionId(out var id) && id != uint.MaxValue) Common.WTSLogoffSession(IntPtr.Zero, id, true);
                    try { using var host = Process.GetProcessById((int)st!["pid"]!); host.Kill(); host.WaitForExit(10000); } catch { }
                    Thread.Sleep(3000);
                    StartHost(width, height);
                    deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
                    silentSince = null;
                }
            }
            else silentSince = null;
            Thread.Sleep(500);
        }
        throw new TimeoutException($"agent desktop not ready after {timeoutSec}s (host state: {(string?)Common.ReadState()?["state"] ?? "none"})");
    }

    static void StartHost(int width, int height)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "bgdeskw.exe");
        try { File.Delete(Common.StatePath); } catch { }
        // Started through a short-lived cmd so the host is not our child: a client that kills its MCP
        // server's process tree must not take the desktop down. Shell-execute keeps it from inheriting
        // our stdio handles, which an MCP client would otherwise wait on.
        using var launcher = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c start \"\" \"{exe}\" host {width} {height}")
        {
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
        })!;
        launcher.WaitForExit(10000);
    }

    static bool Ready()
    {
        var st = Common.ReadState();
        return Common.HostAlive(st) && (string?)st!["state"] is "logged-in" or "connected" && AgentClient.Reachable();
    }

    public static void Down()
    {
        if (AgentClient.Reachable())
        {
            using var c = new AgentClient();
            c.Call(new JsonObject { ["op"] = "logoff" });
            // Logging off takes a few seconds. Return only when it is over, so that a start right after
            // this does not mistake the session that is going away for one that is ready.
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until && (AgentClient.Reachable(200) || Common.HostAlive(Common.ReadState()))) Thread.Sleep(300);
            return;
        }
        // The agent is gone but its session may still be there with apps running in it.
        else if (Common.WTSGetChildSessionId(out var id) && id != uint.MaxValue)
            Common.WTSLogoffSession(IntPtr.Zero, id, false);
    }
}
