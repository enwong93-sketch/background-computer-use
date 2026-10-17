using System.Runtime.InteropServices;

namespace BgDesk;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    static int Main(string[] args)
    {
        // Per-monitor v2, so agent coordinates are physical pixels of the virtual desktop.
        SetProcessDpiAwarenessContext(new IntPtr(-4));

        switch (args.FirstOrDefault())
        {
            case "host":
                var width = args.Length > 1 ? int.Parse(args[1]) : 1440;
                var height = args.Length > 2 ? int.Parse(args[2]) : 900;
                using (var single = new Mutex(true, @"Local\bgdesk-host", out var first))
                {
                    if (!first) return 0;
                    Application.EnableVisualStyles();
                    Application.Run(new HostForm(width, height));
                }
                return 0;
            case "agent":
                return Agent.Run();
            default:
                return 2;
        }
    }
}
