using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal static class Entry
{
    public static bool Preview, VisualTest, ReleaseUiTest, VerificationFailed, Startup, ConnectSelected;
    internal static void ValidateElevationIdentity(string? expectedSid, string currentSid)
    {
        if (expectedSid != null && !string.Equals(expectedSid, currentSid, StringComparison.Ordinal))
            throw new InvalidOperationException("Для подключения подтвердите права текущей учётной записи Windows. Повышение от имени другого пользователя не поддерживается: настройки защищены вашей учётной записью и не изменены.");
    }
    internal static ProcessStartInfo ElevatedStart(string executable, string directory, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return start;
    }
    static Mutex? singleton;
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            int root = Array.IndexOf(args, "--root");
            if (root >= 0 && root + 1 < args.Length)
                Store.Root = Path.GetFullPath(args[root + 1]);
            VisualTest = args.Contains("--visual-test");
            ReleaseUiTest = args.Contains("--release-ui-test");
            Preview = args.Contains("--preview") || VisualTest || File.Exists(Path.Combine(Store.Root, "preview-only.flag"));
            bool releaseTests = args.Contains("--release-self-test") || args.Contains("--self-test");
            bool performance = args.Contains("--performance-test");
            bool updateTests = args.Contains("--update-self-test") || releaseTests;
            bool refreshBase = args.Contains("--refresh-base");
            bool utility = updateTests || refreshBase || performance;
#if FLOWVPN_PACKAGE
  WinRT.ComWrappersSupport.InitializeComWrappers();
  Store.DataDirectory=Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path,"data");
#endif
#if FLOWVPN_PACKAGE_PREVIEW
  Preview=true;
#endif
            int identityIndex = Array.IndexOf(args, "--handoff-user-sid");
            if (identityIndex >= 0)
            {
                if (identityIndex + 1 >= args.Length)
                    throw new ArgumentException("Не указан пользователь для повышения прав.");
                using var identity = WindowsIdentity.GetCurrent();
                ValidateElevationIdentity(args[identityIndex + 1], identity.User!.Value);
            }
            Startup = !utility && !VisualTest && !ReleaseUiTest && StartupIntegration.WasStartupActivation();
            ConnectSelected = args.Contains("--connect-selected") && !Startup && !ReleaseUiTest;
            int parentIndex = Array.IndexOf(args, "--handoff-parent");
            if (parentIndex >= 0 && parentIndex + 1 < args.Length && int.TryParse(args[parentIndex + 1], out int parentId))
            {
                try
                {
                    using var parent = Process.GetProcessById(parentId);
                    if (string.Equals(Native.ProcessPath(parentId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        parent.WaitForExit(8000);
                }
                catch (ArgumentException) { }
            }
            if (!utility)
            {
                singleton = new Mutex(true, "Local\\FlowVPN-Fluent-" + Environment.UserName + (VisualTest || ReleaseUiTest ? "-test-" + Environment.ProcessId : ""), out bool first);
                if (!first)
                {
                    if (!Startup)
                        System.Windows.Forms.MessageBox.Show("FlowVPN уже запущен. Откройте его значок в трее.", "FlowVPN");
                    return 0;
                }
            }
            InstalledAppLocations.Initialize();
            Store.Init();
            File.WriteAllText(Path.Combine(Store.Data, "winui-startup-trace.txt"), "Store initialized\n");
            if (utility)
            {
                if (performance)
                {
                    PerformanceChecks.Run();
                    return 0;
                }
                if (refreshBase)
                {
                    using var client = BoundedHttp.Client(TimeSpan.FromSeconds(45));
                    DomainCategories.Update(client).GetAwaiter().GetResult();
                    DomainRepository.Update(client, Store.Load().BaseDomainCategory).GetAwaiter().GetResult();
                    return 0;
                }
                if (updateTests)
                {
                    UpdateChecks.Run().GetAwaiter().GetResult();
                    if (releaseTests)
                    {
                        ReleaseChecks.Run().GetAwaiter().GetResult();
                        CoreControlChecks.Run().GetAwaiter().GetResult();
                        Directory.CreateDirectory(Path.Combine(Store.Root, "design-preview"));
                        FeatureChecks.Run(AppPresets.Discover());
                    }
                }
                return 0;
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            File.AppendAllText(Path.Combine(Store.Data, "winui-startup-trace.txt"), "COM wrappers ready\n");
            Application.Start(init => { try { File.AppendAllText(Path.Combine(Store.Data, "winui-startup-trace.txt"), "Application callback\n"); var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(queue)); _ = new FluentApp(); } catch (Exception e) { File.WriteAllText(Path.Combine(Store.Data, "winui-initialize-error.txt"), e.ToString()); throw; } });
            return VerificationFailed ? 1 : 0;
        }
        catch (Exception e) { try { Directory.CreateDirectory(Store.Data); File.WriteAllText(Path.Combine(Store.Data, "startup-error-winui.txt"), e.ToString()); } catch { } if (!args.Contains("--self-test") && !args.Contains("--update-self-test") && !args.Contains("--release-self-test") && !args.Contains("--refresh-base") && !args.Contains("--visual-test") && !args.Contains("--release-ui-test") && !args.Contains("--performance-test")) System.Windows.Forms.MessageBox.Show(e.Message, "FlowVPN"); return 1; }
        finally { singleton?.Dispose(); }
    }
}
public sealed partial class FluentApp : Application
{
    Window? window;
    public FluentApp()
    {
        File.AppendAllText(Path.Combine(Store.Data, "winui-startup-trace.txt"), "Application created\n");
        UnhandledException += (_, e) => { File.WriteAllText(Path.Combine(Store.Data, "ui-error.txt"), e.Exception.ToString()); };
        InitializeComponent();
        File.AppendAllText(Path.Combine(Store.Data, "winui-startup-trace.txt"), "Resources ready\n");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            File.AppendAllText(Path.Combine(Store.Data, "winui-startup-trace.txt"), "Creating window\n");
            window = new FluentWindow();
            window.Activate();
            ((FluentWindow)window).ApplyStartupVisibility();
            File.AppendAllText(Path.Combine(Store.Data, "winui-startup-trace.txt"), "Window activated\n");
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(Store.Data, "winui-window-error.txt"), e.ToString()); throw; }
    }
}
