using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace FlowVPN;

internal sealed record StartupStatus(bool Enabled, bool Blocked, string Description);
internal static class StartupIntegration
{
    const string TaskId = "FlowVPNStartup";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string ValueName => Entry.Preview ? "FlowVPN Preview" : "FlowVPN";
    public static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            if (c == '"')
            {
                result.Append('\\', slashes * 2 + 1);
                result.Append(c);
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes);
            slashes = 0;
            result.Append(c);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static string Command(string executable, string root, bool preview) => Quote(executable) + " --startup --root " + Quote(root) + (preview ? " --preview" : "");
    public static async Task<StartupStatus> Read()
    {
        if (UpdateProtocol.IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => new(true, false, "FlowVPN запускается при входе в Windows."),
                StartupTaskState.DisabledByUser => new(false, true, "Автозапуск выключен в Windows. Включить его снова можно в системных настройках."),
                StartupTaskState.DisabledByPolicy => new(false, true, "Автозапуск запрещён политикой Windows."),
                _ => new(false, false, "Автозапуск выключен.")
            };
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        bool enabled = key?.GetValue(ValueName) is string value && value == Command(Environment.ProcessPath!, Store.Root, Entry.Preview);
        // Read-only compatibility hint. Never override Windows' StartupApproved policy or approval bytes.
        using var approval = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        bool blocked = approval?.GetValue(ValueName) is byte[] { Length: >= 4 } bytes && BitConverter.ToUInt32(bytes, 0) is 3 or 7;
        return new(enabled && !blocked, enabled && blocked, enabled && blocked ? "Запись автозапуска есть, но Windows её отключила." : enabled ? "FlowVPN запускается при входе в Windows." : "Автозапуск выключен.");
    }
    public static async Task Set(bool enabled)
    {
        if (UpdateProtocol.IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
                await task.RequestEnableAsync();
            else
                task.Disable();
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command(Environment.ProcessPath!, Store.Root, Entry.Preview), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, false);
    }
    public static bool WasStartupActivation()
    {
        if (Environment.GetCommandLineArgs().Contains("--startup"))
            return true;
        if (!UpdateProtocol.IsPackaged)
            return false;
        return Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
    }
}

internal sealed partial class FluentWindow
{
    bool bindingStartup, changingStartup;
    void HookStartup()
    {
        C<ToggleSwitch>("StartupTraySwitch").IsOn = settings.StartInTray;
        C<ToggleSwitch>("StartupTraySwitch").Toggled += (_, _) => { settings.StartInTray = C<ToggleSwitch>("StartupTraySwitch").IsOn; Store.Save(settings); };
        C<ToggleSwitch>("StartupSwitch").Toggled += async (_, _) =>
        {
            if (bindingStartup || changingStartup || Entry.VisualTest)
                return;
            changingStartup = true;
            C<ToggleSwitch>("StartupSwitch").IsEnabled = false;
            try
            {
                await StartupIntegration.Set(C<ToggleSwitch>("StartupSwitch").IsOn);
            }
            catch (Exception e) { if (!windowClosed) await Message(e.Message, "Не удалось изменить автозапуск"); }
            finally { changingStartup = false; await RefreshStartup(); }
        };
        Hook("WindowsStartupButton", () => { Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); return Task.CompletedTask; });
        _ = RefreshStartup();
    }
    async Task RefreshStartup()
    {
        try
        {
            var state = await StartupIntegration.Read();
            if (windowClosed)
                return;
            bindingStartup = true;
            try
            {
                C<ToggleSwitch>("StartupSwitch").IsOn = state.Enabled;
                C<ToggleSwitch>("StartupSwitch").IsEnabled = !state.Blocked;
                C<TextBlock>("StartupStatus").Text = state.Description;
            }
            finally { bindingStartup = false; }
        }
        catch (Exception e) { if (!windowClosed) C<TextBlock>("StartupStatus").Text = "Не удалось прочитать состояние автозапуска: " + e.Message; }
    }
    public void ApplyStartupVisibility()
    {
        if (Entry.Startup && settings.StartInTray)
            AppWindow.Hide();
    }
}
