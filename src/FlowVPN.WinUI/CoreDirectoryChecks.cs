using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace FlowVPN;

internal static class CoreDirectoryChecks
{
    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Store.Run, "core-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var baseline = WriteHandle(directory))
                check(!baseline.IsInvalid, "Directory-write regression fixture is writable before acquiring a core lease");
            using (CoreIntegrity.LockDirectories(directory))
            using (var writer = WriteHandle(directory))
                check(writer.IsInvalid && Marshal.GetLastWin32Error() == 32,
                    "Core directory lease rejects concurrent GENERIC_WRITE with a Windows sharing violation");
            using (var released = WriteHandle(directory))
                check(!released.IsInvalid, "Core directory lease releases its Windows sharing guard after disposal");
        }
        finally { Directory.Delete(directory); }
    }
    static SafeFileHandle WriteHandle(string path) => CreateFile(path, 0x40000000,
        FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
