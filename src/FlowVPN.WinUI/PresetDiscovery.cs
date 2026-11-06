using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace FlowVPN;

internal static class PresetDiscovery
{
    public static List<DetectedComponent> NormalizeComponents(IEnumerable<DetectedComponent> components) => components
        .GroupBy(c => c.Rule.Pattern, StringComparer.OrdinalIgnoreCase).Select(g => g.FirstOrDefault(c => c.Available) ?? g.First())
        .GroupBy(c => c.Available ? c.Executable + "|" + c.Rule.ComponentRole : c.Rule.Pattern, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(c => c.Rule.Path.Contains('*')).First()).ToList();
    internal sealed record Registration(string Name, string Directory);
    public static List<string> AppPaths(string executable, string productContains)
    {
        var result = new List<string>();
        if (Path.GetFileName(executable) != executable)
            return result;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var entry = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executable);
                    if (entry?.GetValue(null) is not string value)
                        continue;
                    string path = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                    if (!File.Exists(path))
                        continue;
                    if (productContains.Length > 0 && !(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName ?? "").Contains(productContains, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(Path.GetDirectoryName(path)!);
                }
                catch { }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static List<Registration> Registrations()
    {
        var result = new List<Registration>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null)
                        continue;
                    foreach (string key in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(key);
                        string name = entry?.GetValue("DisplayName") as string ?? "";
                        string directory = entry?.GetValue("InstallLocation") as string ?? "";
                        if (Path.IsPathFullyQualified(directory) && Directory.Exists(directory))
                            result.Add(new(name, directory));
                        string icon = entry?.GetValue("DisplayIcon") as string ?? "";
                        var match = Regex.Match(icon, "^\\s*\"?(.*?\\.exe)(?:\"?\\s*,\\s*-?\\d+)?\\s*$", RegexOptions.IgnoreCase);
                        if (match.Success && File.Exists(match.Groups[1].Value))
                            result.Add(new(name, Path.GetDirectoryName(match.Groups[1].Value)!));
                    }
                }
                catch { }
        return result.Distinct().ToList();
    }
    // Expand only explicitly named directory segments; never scan an entire drive or follow junction trees.
    public static IEnumerable<string> Directories(string template)
    {
        if (!template.Contains('*'))
            return [template];
        string root = Path.GetPathRoot(template) ?? "";
        if (root.Length == 0)
            return [];
        IEnumerable<string> paths = [root];
        foreach (string part in template[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new List<string>();
            foreach (string parent in paths)
            {
                if (!part.Contains('*'))
                {
                    next.Add(Path.Combine(parent, part));
                    continue;
                }
                try
                {
                    next.AddRange(Directory.EnumerateDirectories(parent, part, new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    }));
                }
                catch { }
            }
            paths = next;
        }
        return paths;
    }
    public static bool RequirementsMet(PresetComponent component, IReadOnlyDictionary<string, string> locations) =>
        component.RequiresAnyFile.Count == 0 || component.RequiresAnyFile.Any(t =>
        {
            string path = AppPresets.Expand(t, locations);
            return Directories(Path.GetDirectoryName(path)!).Any(d => File.Exists(Path.Combine(d, Path.GetFileName(path))));
        });
    public static IEnumerable<string> PathExecutables(string name)
    {
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name) != name)
            yield break;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path;
            try
            {
                if (!Path.IsPathFullyQualified(directory.Trim('"')))
                    continue;
                path = Path.Combine(directory.Trim('"'), name);
                if (!File.Exists(path))
                    continue;
                path = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
                if (!Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch { continue; }
            yield return path;
        }
    }
    public static string[] Executables(string directory, string name)
    {
        try
        {
            return Directory.GetFiles(directory, name, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = 10
            });
        }
        catch { return []; }
    }
    public static AppRule Rule(string template, string directory, string exe, PresetComponent component)
    {
        string path = component.FileOnly ? Path.Combine(template, exe.Length > 0 ? Path.GetRelativePath(directory, exe) : component.Executable) : template;
        if (!template.Contains('*'))
            return component.FileOnly ? AppRule.File(path, component.Name) : AppRule.Folder(path, component.Name);
        return new AppRule
        {
            Name = component.Name,
            Path = path,
            Pattern = "(?i)^" + Regex.Escape(path.TrimEnd('\\')).Replace(@"\*", @"[^\\]+") + (component.FileOnly ? "$" : @"\\.*\.exe$")
        };
    }
}
