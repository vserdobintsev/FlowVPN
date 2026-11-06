using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal static class Entry
{
    public static bool Preview, VisualTest, ReleaseUiTest, VerificationFailed, Startup;
    internal static void ConfigureRoot(string[] args)
    {
        int root = Array.IndexOf(args, "--root");
#if FLOWVPN_PACKAGE
        if (root >= 0)
            throw new ArgumentException("Установленная версия использует только свой каталог MSIX; параметр --root недоступен.");
#else
        if (root >= 0)
        {
            if (root + 1 >= args.Length)
                throw new ArgumentException("Не указан каталог --root.");
            Store.Root = Path.GetFullPath(args[root + 1]);
        }
#endif
    }
    internal static void ValidateUiPrivilege(bool elevated, bool utilityTest)
    {
#if FLOWVPN_PACKAGE
        if (utilityTest)
            throw new InvalidOperationException("Изолированные проверки доступны только в отдельной сборке разработчика.");
#endif
        if (elevated && !utilityTest)
            throw new InvalidOperationException("Откройте FlowVPN без прав администратора. Интерфейс и настройки работают от вашей учётной записи; Windows запросит разрешение отдельно при подключении VPN.");
    }
    static Mutex? singleton;
    [STAThread]
    static int Main(string[] args)
    {
        // The privileged entry point must never initialize user storage or WinUI.
        if (args.Contains("--broker"))
            return BrokerHost.Run(args);
        bool releaseTests = args.Contains("--release-self-test") || args.Contains("--self-test");
        bool performance = args.Contains("--performance-test");
        bool updateTests = args.Contains("--update-self-test") || releaseTests;
        try
        {
            ValidateUiPrivilege(Native.IsAdmin, updateTests || performance);
            if (args.Any(value => value is "--handoff-user-sid" or "--handoff-parent" or "--connect-selected"))
                throw new ArgumentException("Перезапуск интерфейса с повышением прав больше не поддерживается. Откройте FlowVPN обычным способом.");
        }
        catch (Exception error)
        {
            if (!updateTests && !performance)
                System.Windows.Forms.MessageBox.Show(error.Message, "FlowVPN");
            return 1;
        }
        try
        {
            ConfigureRoot(args);
            VisualTest = args.Contains("--visual-test");
            ReleaseUiTest = args.Contains("--release-ui-test");
            Preview = args.Contains("--preview") || VisualTest || File.Exists(Path.Combine(Store.Root, "preview-only.flag"));
            bool refreshBase = args.Contains("--refresh-base");
            bool utility = updateTests || refreshBase || performance;
#if FLOWVPN_PACKAGE
  WinRT.ComWrappersSupport.InitializeComWrappers();
  Store.DataDirectory=Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path,"data");
#endif
#if FLOWVPN_PACKAGE_PREVIEW
  Preview=true;
#endif
            Startup = !utility && !VisualTest && !ReleaseUiTest && StartupIntegration.WasStartupActivation();
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
            // Older releases generated a plaintext runtime file in user storage.
            // The broker never reads it; remove only this obsolete generated file.
            if (!utility)
                File.Delete(Path.Combine(Store.Run, "runtime.json"));
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
                        BrokerChecks.Run().GetAwaiter().GetResult();
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
