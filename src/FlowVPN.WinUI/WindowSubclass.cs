using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FlowVPN;

internal sealed class WindowSubclass : IDisposable
{
    delegate nint Procedure(nint hwnd, uint message, nint wparam, nint lparam, nuint id, nuint data);
    static long nextId;
    readonly nint window;
    readonly nuint id = (nuint)Interlocked.Increment(ref nextId);
    readonly Procedure procedure;
    bool disposed;
    public WindowSubclass(nint window, Func<uint, nint, nint, nint?> handler)
    {
        this.window = window;
        procedure = (hwnd, message, wparam, lparam, _, _) =>
        {
            nint? result = null;
            try
            {
                result = handler(message, wparam, lparam);
            }
            catch (Exception e) { System.Diagnostics.Trace.TraceError("Window subclass: {0}", e); }
            if (message == 0x0082)
                Dispose(); // WM_NCDESTROY
            return result ?? DefSubclassProc(hwnd, message, wparam, lparam);
        };
        if (!SetWindowSubclass(window, procedure, id, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        RemoveWindowSubclass(window, procedure, id);
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll", SetLastError = true)] static extern bool SetWindowSubclass(nint hwnd, Procedure procedure, nuint id, nuint data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(nint hwnd, Procedure procedure, nuint id);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint hwnd, uint message, nint wparam, nint lparam);
}
