using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace FlowVPN;

internal sealed class CatalogApp
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Origin { get; set; } = "";
    public string IconPath { get; set; } = "";
    public bool Running
    {
        get; set;
    }
    public List<AppRule> Rules { get; init; } = [];
    public string PresetId { get; init; } = "";
    public string ParentId { get; init; } = "";
    public HashSet<string>? SuggestedPatterns
    {
        get; init;
    }
    public string Id => PresetId.Length > 0 ? "preset:" + PresetId : Rules.FirstOrDefault()?.GroupId is string id && id.Length > 0 ? id : Rules.FirstOrDefault()?.Pattern ?? Path;
}
internal static class AppCatalog
{
    public static List<CatalogApp> Read() => Read(AppPresets.Discover());
    public static List<CatalogApp> Read(IReadOnlyList<DetectedPreset> presets)
    {
        var apps = new Dictionary<string, CatalogApp>(StringComparer.OrdinalIgnoreCase);
        bool LocalExe(string path) => RegexPath(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        void Add(string path, string name, string origin, bool running = false, string iconPath = "")
        {
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (Path.GetFileName(path).Equals("FlowVPN.exe", StringComparison.OrdinalIgnoreCase) || !LocalExe(path) || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "unins|uninstall|updater|crashpad", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return;
                if (apps.TryGetValue(path, out var found))
                {
                    found.Running |= running;
                    if (found.IconPath.Length == 0)
                        found.IconPath = iconPath;
                    return;
                }
                apps[path] = new()
                {
                    Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name,
                    Path = path,
                    Origin = origin,
                    IconPath = iconPath,
                    Running = running,
                    Rules = [AppRule.File(path)]
                };
            }
            catch { }
        }
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null)
                shell = Activator.CreateInstance(type);
            if (shell != null)
            {
                dynamic dynamicShell = shell;
                foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
                {
                    if (!Directory.Exists(dir))
                        continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        object? link = null;
                        try
                        {
                            link = dynamicShell.CreateShortcut(file);
                            dynamic shortcut = link;
                            Add((string)shortcut.TargetPath, Path.GetFileNameWithoutExtension(file), "Меню «Пуск»", false, file);
                        }
                        catch { }
                        finally { if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); }
                    }
                }
            }
        }
        finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var paths = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                    if (paths == null)
                        continue;
                    foreach (var name in paths.GetSubKeyNames())
                    {
                        using var entry = paths.OpenSubKey(name);
                        if (entry?.GetValue(null) is string path)
                        {
                            string title = Path.GetFileNameWithoutExtension(name);
                            try
                            {
                                title = FileVersionInfo.GetVersionInfo(path.Trim('"')).FileDescription ?? title;
                            }
                            catch { }
                            Add(path, title, "Установленное приложение");
                        }
                    }
                }
                catch { }
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero)
                    continue;
                string? path = Native.ProcessPath(process.Id);
                if (path == null)
                    continue;
                string name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    name = FileVersionInfo.GetVersionInfo(path).FileDescription ?? name;
                }
                catch { }
                Add(path, name, "Запущено", true);
            }
            catch { }
            finally { process.Dispose(); }
        }
        var bundled = presets.Where(p => p.Available).Select(p => p.ToApp()).ToList();
        foreach (var app in bundled)
        {
            app.Running = apps.Values.Any(a => a.Running && app.Rules.Any(r => System.Text.RegularExpressions.Regex.IsMatch(a.Path, r.Pattern)));
            if (app.IconPath.Length == 0)
                app.IconPath = apps.Values.FirstOrDefault(a => a.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase))?.IconPath ?? "";
        }
        foreach (var app in apps.Values)
            foreach (var rule in app.Rules)
            {
                rule.GroupId = "app:" + Store.Hash(app.Path.ToUpperInvariant());
                rule.GroupName = app.Name;
                rule.IconPath = app.IconPath.Length > 0 ? app.IconPath : app.Path;
            }
        bool Covered(CatalogApp a) => bundled.Any(b => b.Rules.Any(rule => (b.SuggestedPatterns == null || b.SuggestedPatterns.Contains(rule.Pattern)) && System.Text.RegularExpressions.Regex.IsMatch(a.Path, rule.Pattern)));
        return bundled.Concat(apps.Values.Where(a => !Covered(a))).OrderByDescending(a => a.Running).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    static bool RegexPath(string path) => path.Length > 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
}
