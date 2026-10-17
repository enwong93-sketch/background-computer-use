using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BgDesk;

/// <summary>
/// `bgdesk install &lt;agent&gt;` registers this MCP server in a coding agent's user-level configuration.
/// The existing file is backed up next to itself before it is changed.
/// </summary>
static class Install
{
    record Agent(string Id, string Label, string ConfigPath, string Key, Func<string, string[], JsonObject> Entry);

    static string Home(params string[] parts) => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. parts]);
    static string Roaming(params string[] parts) => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), .. parts]);

    static JsonArray Arr(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());
    static JsonObject Stdio(string command, string[] args) => new() { ["type"] = "stdio", ["command"] = command, ["args"] = Arr(args) };
    static JsonObject Plain(string command, string[] args) => new() { ["command"] = command, ["args"] = Arr(args) };

    static readonly Agent[] JsonAgents =
    [
        new("claude-code", "Claude Code", Home(".claude.json"), "mcpServers", Stdio),
        new("claude-desktop", "Claude Desktop", Roaming("Claude", "claude_desktop_config.json"), "mcpServers", Plain),
        new("cursor", "Cursor", Home(".cursor", "mcp.json"), "mcpServers", Stdio),
        // Windsurf became Devin Desktop and moved its config; use the old location only where it is the one in use.
        new("windsurf", "Windsurf / Devin Desktop",
            Directory.Exists(Home(".codeium", "windsurf")) && !Directory.Exists(Roaming("devin"))
                ? Home(".codeium", "windsurf", "mcp_config.json")
                : Roaming("devin", "mcp_config.json"),
            "mcpServers", Plain),
        new("vscode", "VS Code (GitHub Copilot)", Roaming("Code", "User", "mcp.json"), "servers", Stdio),
        new("gemini", "Gemini CLI", Home(".gemini", "settings.json"), "mcpServers", Plain),
        new("opencode", "OpenCode", Home(".config", "opencode", "opencode.json"), "mcp",
            (command, args) => new JsonObject { ["type"] = "local", ["command"] = Arr([command, .. args]), ["enabled"] = true }),
    ];

    static string CodexConfig => Home(".codex", "config.toml");

    public static string Agents => "codex, " + string.Join(", ", JsonAgents.Select(a => a.Id)) + ", all";

    public static int Run(string[] args, bool remove)
    {
        if (args.Length == 0) throw new ArgumentException($"usage: bgdesk {(remove ? "uninstall" : "install")} <{Agents}> [--name <name>]");
        var name = "bgdesk";
        for (var i = 1; i + 1 < args.Length; i++)
            if (args[i] == "--name") name = args[i + 1];

        var command = Path.Combine(AppContext.BaseDirectory, "bgdesk.exe");
        string[] commandArgs = ["mcp"];

        var target = args[0].ToLowerInvariant();
        if (target == "all")
        {
            // Only agents that are already set up on this machine.
            if (File.Exists(CodexConfig)) Codex(name, command, commandArgs, remove);
            foreach (var a in JsonAgents.Where(a => Directory.Exists(Path.GetDirectoryName(a.ConfigPath)) && (a.Id != "claude-code" || File.Exists(a.ConfigPath))))
                Json(a, name, command, commandArgs, remove);
            return 0;
        }
        if (target == "codex") { Codex(name, command, commandArgs, remove); return 0; }
        var agent = JsonAgents.FirstOrDefault(a => a.Id == target) ?? throw new ArgumentException($"unknown agent '{target}'. Known: {Agents}");
        Json(agent, name, command, commandArgs, remove);
        return 0;
    }

    static void Json(Agent agent, string name, string command, string[] args, bool remove)
    {
        JsonObject root;
        if (File.Exists(agent.ConfigPath))
        {
            try { root = JsonNode.Parse(File.ReadAllText(agent.ConfigPath)) as JsonObject ?? new JsonObject(); }
            catch (JsonException)
            {
                // A file with comments would lose them if we rewrote it, so leave it to the user.
                var snippet = new JsonObject { [agent.Key] = new JsonObject { [name] = agent.Entry(command, args) } };
                throw new InvalidOperationException($"{agent.ConfigPath} is not plain JSON, so it was not changed. Add this yourself:\n{snippet.ToJsonString(Indented)}");
            }
            Backup(agent.ConfigPath);
        }
        else
        {
            if (remove) { Console.WriteLine($"{agent.Label}: nothing to remove"); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(agent.ConfigPath)!);
            root = new JsonObject();
        }

        if (root[agent.Key] is not JsonObject servers) root[agent.Key] = servers = new JsonObject();
        if (remove) servers.Remove(name);
        else servers[name] = agent.Entry(command, args);

        File.WriteAllText(agent.ConfigPath, root.ToJsonString(Indented), new UTF8Encoding(false));
        Console.WriteLine($"{agent.Label}: {(remove ? "removed" : "registered")} '{name}' in {agent.ConfigPath}");
    }

    static void Codex(string name, string command, string[] args, bool remove)
    {
        var path = CodexConfig;
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (File.Exists(path)) Backup(path);
        else Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";

        // Drop an existing table for this server, including its sub-tables.
        var header = $"[mcp_servers.{name}]";
        var prefix = $"[mcp_servers.{name}.";
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('[')) skipping = trimmed.StartsWith(header) || trimmed.StartsWith(prefix);
            if (!skipping) kept.Add(line);
        }
        var body = string.Join(nl, kept).TrimEnd('\r', '\n');

        if (!remove)
        {
            var block = string.Join(nl,
                header,
                $"command = {Toml(command)}",
                $"args = [ {string.Join(", ", args.Select(Toml))} ]",
                // The first call may have to start the desktop.
                "tool_timeout_sec = 180");
            body = body.Length == 0 ? block : body + nl + nl + block;
        }
        File.WriteAllText(path, body + nl, new UTF8Encoding(false));
        Console.WriteLine($"Codex: {(remove ? "removed" : "registered")} '{name}' in {path}");
    }

    /// <summary>Literal string when possible, so Windows paths need no escaping.</summary>
    static string Toml(string s) => s.Contains('\'') ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : $"'{s}'";

    /// <summary>Keeps one copy of the file as it was before BgDesk first touched it.</summary>
    static void Backup(string path)
    {
        var backup = path + ".bak-bgdesk";
        if (!File.Exists(backup)) File.Copy(path, backup);
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
