using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace BgDesk;

public static class Common
{
    public const string AgentPipe = "bgdesk-agent";
    public const string AutostartValue = "BgDeskAgent";
    public const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string LogonScriptKey = "Environment";
    public const string LogonScriptValue = "UserInitMprLogonScript";
    public const string SessionInfoKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo";

    public static string DataDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BgDesk");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string StatePath => Path.Combine(DataDir, "host.json");

    // The background session is one large screen divided into tiles that do not overlap, one tile per agent,
    // with a gap between them so that menus and lists near an edge stay clear of the next tile. Every agent
    // sees and works in its own tile; all tiles are on screen at the same time.
    public const int TileWidth = 1440, TileHeight = 900, TileGap = 120, TileColumns = 4, TileRows = 3, TaskbarAllowance = 48;

    public static int CanvasWidth => TileColumns * TileWidth + (TileColumns + 1) * TileGap;
    public static int CanvasHeight => TileRows * TileHeight + (TileRows + 1) * TileGap + TaskbarAllowance;


    public static void WriteState(string state, string? detail, int width, int height)
    {
        var o = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["parentSession"] = Process.GetCurrentProcess().SessionId,
            ["state"] = state,
            ["detail"] = detail,
            ["width"] = width,
            ["height"] = height,
        };
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString());
        File.Move(tmp, StatePath, true);
    }

    public static JsonObject? ReadState()
    {
        try { return JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject; }
        catch { return null; }
    }

    public static bool HostAlive(JsonObject? st)
    {
        if (st is null) return false;
        try
        {
            using var p = Process.GetProcessById((int)st["pid"]!);
            // The name follows the program file, which an update may have renamed while the host runs (bgdeskw.exe.old).
            return !p.HasExited && p.ProcessName.StartsWith("bgdeskw", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSIsChildSessionsEnabled(out bool enabled);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSEnableChildSessions(bool enable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr server, uint sessionId, bool wait);
}

/// <summary>One JSON line per request and per response over the agent's named pipe.</summary>
public sealed class AgentClient : IDisposable
{
    NamedPipeClientStream? _pipe;
    StreamReader? _reader;
    StreamWriter? _writer;

    public static bool Reachable(int timeoutMs = 300)
    {
        try
        {
            using var c = new AgentClient();
            c.Connect(timeoutMs);
            return true;
        }
        catch { return false; }
    }

    void Connect(int timeoutMs)
    {
        var p = new NamedPipeClientStream(".", Common.AgentPipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        try { p.Connect(timeoutMs); }
        catch { p.Dispose(); throw; }
        _pipe = p;
        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(p, utf8);
        _writer = new StreamWriter(p, utf8) { AutoFlush = true, NewLine = "\n" };
    }

    public JsonObject Call(JsonObject request, int connectTimeoutMs = 2000)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (_pipe is null || !_pipe.IsConnected) { Reset(); Connect(connectTimeoutMs); }
                _writer!.WriteLine(request.ToJsonString());
                var line = _reader!.ReadLine() ?? throw new IOException("agent closed the pipe");
                return (JsonObject)JsonNode.Parse(line)!;
            }
            catch (IOException) when (attempt == 0) { Reset(); }
        }
    }

    void Reset()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose() => Reset();
}
