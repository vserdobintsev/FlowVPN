using System.Runtime.InteropServices;

namespace FlowVPN;

internal readonly record struct TaskbarState(bool Running, bool Busy, bool Enabled)
{
    public static TaskbarState From(bool preview, bool running, bool busy, bool hasSource) =>
        new(running, busy, !preview && !busy && (running || hasSource));
    public string Tooltip => Busy ? "Изменяем состояние VPN…" : Running ? "Отключить VPN" : "Подключить выбранный профиль";
}

// Native thumbnail toolbar: no extra UI framework or shell extension is loaded.
internal sealed class TaskbarControls : IDisposable
{
    const uint ToggleId = 41001;
    readonly nint window;
    readonly WindowSubclass subclass;
    readonly Action<bool> command;
    readonly uint created = RegisterWindowMessage("TaskbarButtonCreated");
    ITaskbarList3? taskbar;
    bool ready, added, disposed;
    TaskbarState state;
    TaskbarState? lastSent;
    public bool Registered => added;
    public int LastResult
    {
        get; private set;
    }
    public TaskbarControls(nint window, Action<bool> command)
    {
        this.window = window;
        this.command = command;
        subclass = new(window, HandleMessage);
        // Explorer runs at medium integrity even when the VPN UI was elevated.
        ChangeWindowMessageFilterEx(window, created, 1, 0);
    }
    nint? HandleMessage(uint message, nint wparam, nint lparam)
    {
        if (disposed)
            return null;
        if (message == created)
        {
            ReleaseTaskbar();
            ready = true;
            added = false;
            lastSent = null;
            Apply();
        }
        else if (message == 0x0111 && ((wparam.ToInt64() >> 16) & 0xffff) == 0x1800)
        {
            uint id = (uint)(wparam.ToInt64() & 0xffff);
            if (id == ToggleId && state.Enabled)
                command(!state.Running);
            if (id == ToggleId)
                return 0;
        }
        else if (message == 0x02e0)
        {
            lastSent = null;
            Apply();
        } // WM_DPICHANGED
        return null;
    }
    public void Update(TaskbarState value)
    {
        state = value;
        Apply();
    }
    void Apply()
    {
        if (!ready || disposed || lastSent == state)
            return;
        var handles = new List<nint>(1);
        try
        {
            if (taskbar == null)
            {
                taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(new("56FDF344-FD6D-11D0-958A-006097C9A090"), true)!)!;
                Marshal.ThrowExceptionForHR(taskbar.HrInit());
            }
            ThumbButton Button(uint id, string glyph, string tip, bool enabled)
            {
                int size = Math.Max(32, GetSystemMetricsForDpi(11, GetDpiForWindow(window)));
                using var bitmap = TrayArt.Glyph(glyph, System.Drawing.Color.FromArgb(0, 128, 116), size);
                nint icon = bitmap.GetHicon();
                handles.Add(icon);
                return new()
                {
                    Mask = 2 | 4 | 8,
                    Id = id,
                    Icon = icon,
                    Tip = tip,
                    Flags = enabled ? 2u : 3u
                };
            }
            ThumbButton[] buttons = [Button(ToggleId, state.Running ? "\uE71A" : "\uE768", state.Tooltip, state.Enabled)];
            LastResult = added ? taskbar.ThumbBarUpdateButtons(window, (uint)buttons.Length, buttons) : taskbar.ThumbBarAddButtons(window, (uint)buttons.Length, buttons);
            Marshal.ThrowExceptionForHR(LastResult);
            added = true;
            lastSent = state;
        }
        catch (COMException e) { LastResult = e.HResult; ReleaseTaskbar(); }
        finally { foreach (nint icon in handles) Native.DestroyIcon(icon); }
    }
    void ReleaseTaskbar()
    {
        if (taskbar != null)
        {
            Marshal.FinalReleaseComObject(taskbar);
            taskbar = null;
        }
    }
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        subclass.Dispose();
        ReleaseTaskbar();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ThumbButton
    {
        public uint Mask, Id, Bitmap;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Tip;
        public uint Flags;
    }
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);
        [PreserveSig] int MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(nint hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(nint hwnd, uint flags);
        [PreserveSig] int RegisterTab(nint tab, nint mdi);
        [PreserveSig] int UnregisterTab(nint tab);
        [PreserveSig] int SetTabOrder(nint tab, nint before);
        [PreserveSig] int SetTabActive(nint tab, nint mdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(nint hwnd, uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(nint hwnd, uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] buttons);
        [PreserveSig] int ThumbBarSetImageList(nint hwnd, nint images);
        [PreserveSig] int SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        [PreserveSig] int SetThumbnailClip(nint hwnd, nint rect);
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilter);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
}
