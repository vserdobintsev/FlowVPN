using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace FlowVPN;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blob
    {
        public int size; public IntPtr data;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    public static byte[] Protect(byte[] data) => Crypt(data, true);
    public static byte[] Unprotect(byte[] data) => Crypt(data, false);
    static byte[] Crypt(byte[] bytes, bool protect)
    {
        var b = new Blob { size = bytes.Length, data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, b.data, bytes.Length);
            Blob result;
            bool ok = protect ? CryptProtectData(ref b, "FlowVPN", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result) : CryptUnprotectData(ref b, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[result.size];
                Marshal.Copy(result.data, output, 0, result.size);
                return output;
            }
            finally { LocalFree(result.data); }
        }
        finally { Marshal.FreeHGlobal(b.data); }
    }
    public static bool IsAdmin
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint control, uint group);
    public static async Task Stop(Process p)
    {
        if (p.HasExited)
            return;
        // Keep CTRL+C ignored in the GUI: Windows may dispatch the event after detaching.
        FreeConsole();
        if (AttachConsole((uint)p.Id))
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            GenerateConsoleCtrlEvent(0, 0);
            FreeConsole();
        }
        try
        {
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException) { if (!p.HasExited) p.Kill(); await p.WaitForExitAsync(); }
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder path, ref int length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static string? ProcessPath(int id)
    {
        var h = OpenProcess(0x1000, false, (uint)id);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            var b = new System.Text.StringBuilder(32768);
            var n = b.Capacity;
            return QueryFullProcessImageName(h, 0, b, ref n) ? b.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
