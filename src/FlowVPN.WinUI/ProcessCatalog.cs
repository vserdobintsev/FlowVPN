using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FlowVPN;

internal sealed record ProcessSnapshot(int Id, string Path, string Name, string Company, string Version, string WindowTitle, bool HasWindow);
internal enum ProcessCategory
{
    Known, Application, Background, System
}
internal enum ProcessFilter
{
    Applications, All, Known, Background, System
}
internal sealed class RunningApplication(CatalogApp app, ProcessCategory category, IReadOnlyList<ProcessSnapshot> processes)
{
    public CatalogApp App { get; } = app;
    public ProcessCategory Category { get; } = category;
    public IReadOnlyList<ProcessSnapshot> Processes { get; } = processes;
    // The inventory is a snapshot. Build this index once, rather than on every search keystroke.
    public string SearchText { get; } = app.Name + " " + string.Join(" ", processes.Select(p => $"{p.Name} {p.Path} {p.Company} {p.Id} {p.WindowTitle}"));
}
internal sealed record ProcessInventory(IReadOnlyList<RunningApplication> Applications, int Inspected, int Unavailable, DateTimeOffset CapturedAt);

// Windows enumeration is separate from deterministic matching and presentation. No command lines or credentials are collected.
internal static class ProcessCatalog
{
    public static ProcessInventory Read(IReadOnlyList<DetectedPreset> presets, CancellationToken token)
    {
        var snapshot = new List<ProcessSnapshot>();
        var metadata = new Dictionary<string, (string Name, string Company, string Version)>(StringComparer.OrdinalIgnoreCase);
        int inspected = 0, unavailable = 0;
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                inspected++;
                try
                {
                    string? path = Native.ProcessPath(process.Id);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        unavailable++;
                        continue;
                    }
                    if (!metadata.TryGetValue(path, out var info))
                    {
                        info = (Path.GetFileNameWithoutExtension(path), "", "");
                        try
                        {
                            var version = FileVersionInfo.GetVersionInfo(path);
                            string name = version.FileDescription?.Trim() ?? "";
                            if (name.Length == 0)
                                name = version.ProductName?.Trim() ?? "";
                            info = (name.Length > 0 ? name : info.Name, version.CompanyName?.Trim() ?? "", version.FileVersion ?? "");
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception) { }
                        metadata[path] = info;
                    }
                    bool window = false;
                    string title = "";
                    try
                    {
                        window = process.MainWindowHandle != IntPtr.Zero;
                        if (window)
                            title = process.MainWindowTitle;
                    }
                    catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
                    snapshot.Add(new(process.Id, path, info.Name, info.Company, info.Version, title, window));
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException) { unavailable++; }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return new(Group(snapshot, presets, Environment.GetFolderPath(Environment.SpecialFolder.Windows)), inspected, unavailable, DateTimeOffset.Now);
    }

    public static IReadOnlyList<RunningApplication> Group(IEnumerable<ProcessSnapshot> snapshot, IReadOnlyList<DetectedPreset> presets, string windowsDirectory)
    {
        var matchers = presets.Where(p => p.Available).SelectMany(p => p.Components.Where(c => c.Available)
            .Select(c => (Preset: p, Pattern: new Regex(c.Rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))))
            .OrderByDescending(m => m.Preset.Definition.ParentId.Length > 0).ThenByDescending(m => m.Pattern.ToString().Length).ToArray();
        var groups = new Dictionary<string, (CatalogApp App, List<ProcessSnapshot> Processes)>(StringComparer.OrdinalIgnoreCase);
        var knownApps = new Dictionary<string, CatalogApp>(StringComparer.Ordinal);
        var definitions = presets.Select(p => p.Definition).ToList();
        foreach (var executable in snapshot.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            // Only an executable's actual path identifies a preset. Product names and publishers are not identities.
            var preset = matchers.FirstOrDefault(m => Matches(executable.Key, m.Pattern)).Preset;
            var first = executable.First();
            CatalogApp app;
            if (preset != null)
            {
                if (!knownApps.TryGetValue(preset.Definition.Id, out app!))
                {
                    var detected = preset.ToApp();
                    app = new CatalogApp
                    {
                        Name = PresetHierarchy.QualifiedName(preset.Definition, definitions),
                        Path = detected.Path,
                        IconPath = detected.IconPath,
                        PresetId = detected.PresetId,
                        ParentId = detected.ParentId,
                        Origin = detected.Origin,
                        Rules = detected.Rules,
                        SuggestedPatterns = detected.SuggestedPatterns,
                        Running = true
                    };
                    knownApps.Add(preset.Definition.Id, app);
                }
            }
            else
                app = new CatalogApp { Name = first.Name, Path = first.Path, IconPath = first.Path, Running = true, Rules = [AppRule.File(first.Path, first.Name)] };
            if (!groups.TryGetValue(app.Id, out var group))
                groups[app.Id] = group = (app, []);
            group.Processes.AddRange(executable);
        }
        return groups.Values.Select(g => new RunningApplication(g.App,
            g.App.PresetId.Length > 0 ? ProcessCategory.Known : g.Processes.Any(p => p.HasWindow) ? ProcessCategory.Application :
            IsWithin(g.App.Path, windowsDirectory) ? ProcessCategory.System : ProcessCategory.Background, g.Processes.OrderBy(p => p.Id).ToArray()))
            .OrderBy(g => g.Category).ThenBy(g => g.App.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.App.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static bool IsWithin(string path, string directory) => directory.Length > 0 && path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    static bool Matches(string path, Regex pattern)
    {
        try
        {
            return pattern.IsMatch(path);
        }
        catch (RegexMatchTimeoutException) { return false; }
    }
    public static bool Matches(ProcessSnapshot process, AppRule rule) => Matches(process.Path, new Regex(rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
    public static IReadOnlyList<RunningApplication> Filter(IEnumerable<RunningApplication> apps, ProcessFilter filter, string query)
    {
        query = query.Trim();
        return apps.Where(a =>
        (filter switch
        {
            ProcessFilter.Applications => a.Category is ProcessCategory.Known or ProcessCategory.Application,
            ProcessFilter.Known => a.Category == ProcessCategory.Known,
            ProcessFilter.Background => a.Category == ProcessCategory.Background,
            ProcessFilter.System => a.Category == ProcessCategory.System,
            _ => true
        }) && (query.Length == 0 || a.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
