using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
namespace FlowVPN;

internal static class WindowSizing
{
    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MinMax
    {
        public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    public static IDisposable Install(Window window, int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        return new WindowSubclass(hwnd, (message, wparam, lparam) =>
        {
            if (message == 0x24)
            {
                var limits = Marshal.PtrToStructure<MinMax>(lparam);
                double scale = GetDpiForWindow(hwnd) / 96d;
                limits.MinTrackSize = new()
                {
                    X = (int)(width * scale),
                    Y = (int)(height * scale)
                };
                Marshal.StructureToPtr(limits, lparam, false);
            }
            return null;
        });
    }
}
