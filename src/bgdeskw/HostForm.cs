using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace BgDesk;

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMsRdpExtendedSettings
{
    void put_Property([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
    [return: MarshalAs(UnmanagedType.Struct)]
    object get_Property([MarshalAs(UnmanagedType.BStr)] string name);
}

[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(22)] void OnLogonError(int error);
}

sealed class RdpAx : AxHost
{
    // MsRdpClient9NotSafeForScripting
    public RdpAx() : base("8b918b82-7985-4c24-89df-c33ad2bbfbcd") { }
    public object Ocx => GetOcx()!;
}

/// <summary>
/// Keeps the child session's display alive. The window stays off-screen (never minimised,
/// because a minimised RDP client suppresses output) unless the user asks to look at it.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class HostForm : Form, IMsTscAxEvents
{
    const int WS_EX_TOOLWINDOW = 0x80;
    static readonly Point Offscreen = new(-32000, -32000);

    readonly int _width, _height;
    readonly RdpAx _rdp = new();
    readonly NotifyIcon _tray = new();
    readonly ToolStripMenuItem _toggle = new();
    AxHost.ConnectionPointCookie? _events;
    bool _loggedIn, _peek, _closing;
    (string user, string password)? _unsaved;
    string? _previousLogonScript;

    public HostForm(int width, int height)
    {
        _width = width;
        _height = height;
        Text = "BgDesk";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = Offscreen;
        ClientSize = new Size(width, height);
        _rdp.Dock = DockStyle.Fill;
        Controls.Add(_rdp);

        _toggle.Click += (_, _) => SetPeek(!_peek);
        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggle);
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "BgDesk";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        SetPeek(false);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    void SetPeek(bool on)
    {
        _peek = on;
        _toggle.Text = on ? "Hide background desktop" : "Show background desktop";
        if (on)
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
        }
        else Location = Offscreen;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Common.WriteState("connecting", null, _width, _height);
        try { Connect(); }
        catch (Exception ex) { Fail("connect: " + ex.Message); }
    }

    void Connect()
    {
        dynamic rdp = _rdp.Ocx;
        _events = new AxHost.ConnectionPointCookie(_rdp.Ocx, this, typeof(IMsTscAxEvents));

        rdp.Server = "localhost";
        rdp.DesktopWidth = _width;
        rdp.DesktopHeight = _height;
        rdp.ColorDepth = 32;

        dynamic adv = rdp.AdvancedSettings9;
        Try(() => adv.EnableCredSspSupport = true);
        Try(() => adv.GrabFocusOnConnect = false);
        Try(() => adv.DisplayConnectionBar = false);
        Try(() => adv.SmartSizing = true);
        // The agent desktop keeps its own clipboard and devices; nothing leaks into the user's session.
        Try(() => adv.RedirectClipboard = false);
        Try(() => adv.RedirectDrives = false);
        Try(() => adv.RedirectPrinters = false);
        Try(() => adv.RedirectSmartCards = false);
        Try(() => rdp.SecuredSettings3.AudioRedirectionMode = 2);
        Try(() => rdp.SecuredSettings3.KeyboardHookMode = 0);

        object yes = true;
        ((IMsRdpExtendedSettings)_rdp.Ocx).put_Property("ConnectToChildSession", ref yes);

        var cred = Cred.Read();
        if (cred is null)
        {
            Common.WriteState("credentials", null, _width, _height);
            cred = Cred.Prompt($"{Environment.MachineName}\\{Environment.UserName}");
            if (cred is null) throw new OperationCanceledException("credential prompt cancelled");
            _unsaved = cred;
            Common.WriteState("connecting", null, _width, _height);
        }
        var (user, password) = cred.Value;
        var slash = user.IndexOf('\\');
        rdp.Domain = slash >= 0 ? user[..slash] : Environment.MachineName;
        rdp.UserName = slash >= 0 ? user[(slash + 1)..] : user;
        adv.ClearTextPassword = password;

        // userinit runs the logon script as soon as the child session logs on, well before Explorer gets
        // to the Run keys. Both entries are removed again once the agent answers.
        var command = $"\"{Application.ExecutablePath}\" agent";
        using (var key = Registry.CurrentUser.CreateSubKey(Common.LogonScriptKey))
        {
            _previousLogonScript = key.GetValue(Common.LogonScriptValue) as string;
            if (_previousLogonScript == command) _previousLogonScript = null;
            key.SetValue(Common.LogonScriptValue, command);
        }
        using (var key = Registry.CurrentUser.CreateSubKey(Common.AutostartKey))
            key.SetValue(Common.AutostartValue, command);

        // HKCU is shared with the child session, so its "startup already processed" markers can be written from
        // here before its Explorer starts; the user's startup apps are then not launched a second time.
        var markers = new System.Windows.Forms.Timer { Interval = 50 };
        var ticks = 0;
        markers.Tick += (_, _) =>
        {
            if (++ticks > 1200 || _closing) { markers.Stop(); return; }
            if (!Common.WTSGetChildSessionId(out var id) || id == uint.MaxValue) return;
            foreach (var marker in new[] { "StartupHasBeenRun", "RunStuffHasBeenRun" })
                Try(() => Registry.CurrentUser.CreateSubKey($@"{Common.SessionInfoKey}\{id}\{marker}", RegistryKeyPermissionCheck.Default, RegistryOptions.Volatile)?.Dispose());
            if (_loggedIn) markers.Stop();
        };
        markers.Start();

        rdp.Connect();

        // The agent answering is the proof that the session is up; the autostart entries have then done their job.
        var poll = new System.Windows.Forms.Timer { Interval = 500 };
        poll.Tick += (_, _) =>
        {
            if (_closing) { poll.Stop(); return; }
            if (!AgentClient.Reachable(100)) return;
            // An agent of a session that is still logging off can answer too; only this session's counts. Removing
            // the autostart entries on its word would leave this session without an agent.
            if (!Common.WTSGetChildSessionId(out var id) || id == uint.MaxValue) return;
            try
            {
                using var client = new AgentClient();
                if ((int?)client.Call(new System.Text.Json.Nodes.JsonObject { ["op"] = "ping" }, 300)["session"] != (int)id) return;
            }
            catch { return; }
            poll.Stop();
            LoggedIn();
            RemoveAutostart();
        };
        poll.Start();
    }

    static void Try(Action a) { try { a(); } catch { } }

    public void OnConnected() => Common.WriteState("connected", null, _width, _height);

    public void OnLoginComplete() => LoggedIn();

    /// <summary>
    /// The session is usable. Remote Desktop does not always say so (a logon that had to wait for another
    /// one to finish reports a logon notice instead), so this is also called once the agent answers.
    /// </summary>
    void LoggedIn()
    {
        if (_loggedIn) return;
        _loggedIn = true;
        // Only a password that actually logged on is kept.
        if (_unsaved is { } c) { Try(() => Cred.Write(c.user, c.password)); _unsaved = null; }
        Common.WriteState("logged-in", null, _width, _height);
    }
    void RemoveAutostart()
    {
        Try(() => { using var key = Registry.CurrentUser.OpenSubKey(Common.AutostartKey, true); key?.DeleteValue(Common.AutostartValue, false); });
        Try(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(Common.LogonScriptKey, true);
            if (key is null) return;
            if (_previousLogonScript is null) key.DeleteValue(Common.LogonScriptValue, false);
            else key.SetValue(Common.LogonScriptValue, _previousLogonScript);
        });
    }

    public void OnDisconnected(int discReason)
    {
        if (_closing) return;
        string text = "";
        try
        {
            dynamic rdp = _rdp.Ocx;
            int ext = (int)rdp.ExtendedDisconnectReason;
            text = $"{(string)rdp.GetErrorDescription((uint)discReason, (uint)ext)} (reason {discReason}, extended {ext})";
        }
        catch { text = $"reason {discReason}"; }
        if (_loggedIn) { Common.WriteState("disconnected", text, _width, _height); Quit(); }
        else
        {
            // A stored password that no longer works must not be retried forever.
            if (_unsaved is null) Cred.Delete();
            Fail(text);
        }
    }

    public void OnFatalError(int errorCode) => Fail($"fatal error {errorCode}");

    // A notice from the logon, not necessarily a failure (for example, having to wait for an earlier
    // session to finish logging off). A real failure ends in OnDisconnected.
    public void OnLogonError(int error)
    {
        if (!_loggedIn) Common.WriteState("connected", $"logon notice {error}", _width, _height);
    }

    void Fail(string detail)
    {
        Common.WriteState("error", detail, _width, _height);
        Quit();
    }

    /// <summary>Tray exit: log the child session off so nothing keeps running without a display.</summary>
    void Shutdown()
    {
        try
        {
            using var c = new AgentClient();
            c.Call(new JsonObject { ["op"] = "logoff" }, 500);
        }
        catch { }
        Common.WriteState("stopped", null, _width, _height);
        Quit();
    }

    void Quit()
    {
        if (_closing) return;
        _closing = true;
        RemoveAutostart();
        _tray.Visible = false;
        BeginInvoke(() =>
        {
            try { ((dynamic)_rdp.Ocx).Disconnect(); } catch { }
            Application.Exit();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the peek window only hides it; the desktop keeps running.
        if (!_closing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SetPeek(false);
            return;
        }
        base.OnFormClosing(e);
    }
}
