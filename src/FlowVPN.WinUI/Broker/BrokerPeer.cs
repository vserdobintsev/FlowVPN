using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FlowVPN;

internal static class BrokerPeer
{
    const string Prefix = "FlowVPN.Broker.";
    public static string CreateName() => Prefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public static void ValidateName(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !BrokerWire.IsNonce(name[Prefix.Length..]))
            throw new ArgumentException("Некорректное имя канала управления VPN.");
    }

    public static NamedPipeServerStream CreateServer(string name)
    {
        if (Native.IsAdmin)
            throw new InvalidOperationException("Откройте интерфейс FlowVPN без прав администратора.");
        return CreateServerCore(name);
    }

    // Exercises the real ACL/remote/instance policy on elevated CI runners without enabling a UI or broker CLI bypass.
    internal static NamedPipeServerStream CreateServerCore(string name)
    {
        ValidateName(name);
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");
        // CurrentUserOnly rejects split-token UAC peers. Use a protected explicit DACL instead.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;{sid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            const uint duplex = 3, overlapped = 0x40000000, firstInstance = 0x00080000, rejectRemote = 8;
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, duplex | overlapped | firstInstance, rejectRemote,
                1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Не удалось создать защищённый канал VPN.");
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    public static void ValidateClient(NamedPipeServerStream pipe, Process expectedBroker)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ValidateProcess(pid, expectedBroker, elevatedPeer: true);
    }

    public static void ValidateServer(NamedPipeClientStream pipe, Process expectedParent)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ValidateProcess(pid, expectedParent, elevatedPeer: false);
    }

    static void ValidateProcess(uint pid, Process expected, bool elevatedPeer)
    {
        // Keep the original process handle alive throughout authentication and session lifetime.
        _ = expected.SafeHandle;
        if (pid != expected.Id || expected.HasExited || Native.IsAdmin == elevatedPeer)
            throw new InvalidOperationException("Неверный процесс управления VPN.");
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось проверить процесс управления VPN.");
        using (token)
        using (var peer = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        using (var currentProcess = Process.GetCurrentProcess())
        {
            if (peer.User == null || current.User == null || !peer.User.Equals(current.User) || !GetTokenInformation(token, 20, out uint elevation, sizeof(uint), out _) ||
                (elevation != 0) != elevatedPeer || !GetTokenInformation(token, 12, out uint session, sizeof(uint), out _) ||
                session != (uint)currentProcess.SessionId)
                throw new InvalidOperationException("Процесс VPN принадлежит другому пользователю или сеансу Windows.");
        }
        var path = new StringBuilder(32768);
        int size = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref size) ||
            !string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Процесс управления VPN запущен из другого приложения.");
    }

    public static bool HasPackageIdentity()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) == 122 && length > 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out uint value, uint length, out uint returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int GetCurrentPackageFullName(ref int length, IntPtr name);
}
