using System.Collections.Concurrent;
using System.Runtime.InteropServices;
namespace FlowVPN;

internal static class AppIcons
{
    sealed record Request(CatalogApp App, CancellationToken Token, TaskCompletionSource<byte[]?> Completion);
    static readonly BlockingCollection<Request> Queue = new();
    static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    static AppIcons()
    {
        var worker = new Thread(Work) { IsBackground = true, Name = "FlowVPN shell icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }
    public static Task<byte[]?> Load(CatalogApp app, CancellationToken token = default)
    {
        string key = app.IconPath + "\n" + app.Path;
        if (Cache.TryGetValue(key, out var cached))
            return Task.FromResult<byte[]?>(cached.Length > 0 ? cached : null);
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(new(app, token, completion));
        return completion.Task;
    }
    static void Work()
    {
        int hr = CoInitializeEx(IntPtr.Zero, 2);
        try
        {
            foreach (var request in Queue.GetConsumingEnumerable())
            {
                if (request.Token.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.Token);
                    continue;
                }
                try
                {
                    var bytes = ReadBrandImage(request.App.IconPath) ?? ReadShellIcon(request.App.IconPath) ?? ReadShellIcon(request.App.Path) ?? ReadAssociatedIcon(request.App.Path);
                    if (Cache.Count >= 1024)
                        foreach (string key in Cache.Keys.Take(128))
                            Cache.TryRemove(key, out _);
                    Cache[request.App.IconPath + "\n" + request.App.Path] = bytes ?? [];
                    request.Completion.TrySetResult(bytes);
                }
                catch { request.Completion.TrySetResult(null); }
            }
        }
        finally { if (hr >= 0) CoUninitialize(); }
    }
    static byte[]? ReadAssociatedIcon(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\") || !File.Exists(path))
            return null;
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon == null)
            return null;
        using var bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }
    static byte[]? ReadShellIcon(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\") || !File.Exists(path))
            return null;
        var info = new ShellFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) == UIntPtr.Zero || info.Icon == IntPtr.Zero)
            return null;
        try
        {
            using var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(info.Icon).Clone();
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        finally { Native.DestroyIcon(info.Icon); }
    }
    static byte[]? ReadBrandImage(string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\") || !File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
            return null;
        try
        {
            using var source = System.Drawing.Image.FromFile(path);
            if (source.Width > 4096 || source.Height > 4096)
                return null;
            using var bitmap = new System.Drawing.Bitmap(48, 48);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.White);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float scale = 40f / Math.Max(source.Width, source.Height);
                float width = source.Width * scale, height = source.Height * scale;
                graphics.DrawImage(source, (48 - width) / 2, (48 - height) / 2, width, height);
            }
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch { return null; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    static extern UIntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll")] static extern void CoUninitialize();
}
