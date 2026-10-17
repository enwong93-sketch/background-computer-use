using System.Runtime.InteropServices;
using System.Text;

namespace BgDesk;

/// <summary>
/// The child-session logon needs the account password. It is asked for once with the standard
/// Windows prompt and kept in Windows Credential Manager, never in a file of ours.
/// </summary>
static class Cred
{
    const string Target = "BgDesk/child-session";
    const uint Generic = 1, PersistLocalMachine = 2;

    public static (string user, string password)? Read()
    {
        if (!CredRead(Target, Generic, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            var user = Marshal.PtrToStringUni(c.UserName) ?? "";
            var password = c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
            return (user, password);
        }
        finally { CredFree(p); }
    }

    public static void Write(string user, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        var target = Marshal.StringToCoTaskMemUni(Target);
        var name = Marshal.StringToCoTaskMemUni(user);
        try
        {
            var c = new CREDENTIAL
            {
                Type = Generic, TargetName = target, UserName = name, Persist = PersistLocalMachine,
                CredentialBlob = blob, CredentialBlobSize = (uint)(password.Length * 2),
            };
            if (!CredWrite(ref c, 0)) throw new InvalidOperationException("CredWrite failed (error " + Marshal.GetLastWin32Error() + ")");
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(name);
        }
    }

    public static void Delete() => CredDelete(Target, Generic, 0);

    /// <summary>Standard Windows credential prompt, centred on the primary screen. Null when cancelled.</summary>
    public static (string user, string password)? Prompt(string user)
    {
        // A background process has no foreground window; a topmost owner keeps the prompt where the user can see it.
        using var owner = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, Opacity = 0,
            StartPosition = FormStartPosition.Manual, Size = new Size(1, 1),
        };
        var area = Screen.PrimaryScreen!.WorkingArea;
        owner.Location = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        owner.Show();
        owner.Activate();

        var info = new CREDUI_INFO { hwndParent = owner.Handle, pszCaptionText = "BgDesk", pszMessageText = "Windows account password" };
        info.cbSize = Marshal.SizeOf(info);

        var inSize = 0;
        CredPackAuthenticationBuffer(0, user, "", IntPtr.Zero, ref inSize);
        var inBuf = Marshal.AllocCoTaskMem(inSize);
        IntPtr outBuf = IntPtr.Zero;
        uint outSize = 0;
        try
        {
            CredPackAuthenticationBuffer(0, user, "", inBuf, ref inSize);
            uint package = 0;
            var save = false;
            if (CredUIPromptForWindowsCredentials(ref info, 0, ref package, inBuf, (uint)inSize, out outBuf, out outSize, ref save, 0x1) != 0)
                return null;

            int nu = 512, nd = 512, np = 512;
            StringBuilder u = new(nu), d = new(nd), pw = new(np);
            if (!CredUnPackAuthenticationBuffer(0, outBuf, outSize, u, ref nu, d, ref nd, pw, ref np)) return null;
            return (u.ToString(), pw.ToString());
        }
        finally
        {
            Marshal.FreeCoTaskMem(inBuf);
            if (outBuf != IntPtr.Zero)
            {
                Marshal.Copy(new byte[outSize], 0, outBuf, (int)outSize);
                Marshal.FreeCoTaskMem(outBuf);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CREDENTIAL
    {
        public uint Flags, Type;
        public IntPtr TargetName, Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText, pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref CREDENTIAL credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);
    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    static extern int CredUIPromptForWindowsCredentials(ref CREDUI_INFO info, int authError, ref uint authPackage, IntPtr inAuth, uint inAuthSize, out IntPtr outAuth, out uint outAuthSize, ref bool save, int flags);
    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredPackAuthenticationBuffer(int flags, string user, string password, IntPtr buffer, ref int size);
    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredUnPackAuthenticationBuffer(int flags, IntPtr buffer, uint size, StringBuilder user, ref int maxUser, StringBuilder domain, ref int maxDomain, StringBuilder password, ref int maxPassword);
}
