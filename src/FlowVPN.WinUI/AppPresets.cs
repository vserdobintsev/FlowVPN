using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace FlowVPN;

internal sealed class PresetDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ParentId { get; set; } = "";
    public List<string> LegacyGroupIds { get; set; } = [];
    public string ProfileIcon { get; set; } = "network";
    public bool RecommendedProfile { get; set; }
    public List<PresetComponent> Components { get; set; } = [];
    public List<string> IconFiles { get; set; } = [];
}
internal sealed class PresetComponent
{
    public string Name { get; set; } = "";
    public string Package { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Locations { get; set; } = [];
    public bool Recursive
    {
        get; set;
    }
    public bool FileOnly
    {
        get; set;
    }
    public bool SearchPath
    {
        get; set;
    }
    public string Registration { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string ProductContains { get; set; } = "";
    public List<string> RequiresAnyFile { get; set; } = [];
    public string Role { get; set; } = "application";
    public string ScopeNote { get; set; } = "";
    public bool Suggested { get; set; } = true;
}
internal sealed record DetectedComponent(AppRule Rule, string Executable, bool Suggested = true)
{
    public bool Available => Executable.Length > 0;
}
internal sealed record DetectedPreset(PresetDefinition Definition, List<DetectedComponent> Components)
{
    public string Id => "preset:" + Definition.Id;
    public List<AppRule> Rules => Components.Where(c => c.Available).Select(c => c.Rule).ToList();
    public string Executable => Components.FirstOrDefault(c => c.Available)?.Executable ?? "";
    public bool Available => Components.Any(c => c.Available);
    public CatalogApp ToApp() => new()
    {
        Name = Definition.Name,
        Path = Executable,
        PresetId = Definition.Id,
        ParentId = Definition.ParentId,
        Origin = Definition.Description,
        Rules = Rules,
        IconPath = Components.FirstOrDefault(c => c.Available)?.Rule.IconPath ?? "",
        SuggestedPatterns = Components.Where(c => c.Available && c.Suggested).Select(c => c.Rule.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase)
    };
}

internal static class AppPresets
{
    public static readonly List<PresetDefinition> Definitions = ReadDefinitions();
    static List<PresetDefinition> ReadDefinitions()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlowVPN.AppPresets.json")!;
        var definitions = JsonSerializer.Deserialize<List<PresetDefinition>>(stream)!;
        PresetHierarchy.Validate(definitions);
        return definitions;
    }
    public static Dictionary<string, string> Locations() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["AppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["UserProfile"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["ProgramFilesX86"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
    };
    public static string Expand(string template, IReadOnlyDictionary<string, string> locations) =>
        Regex.Replace(template, @"\{([^}]+)\}", match => locations.TryGetValue(match.Groups[1].Value, out var value)
            && value.Length > 0 ? value.TrimEnd('\\') : throw new ArgumentException("Unknown location: " + match.Value));

    public static List<DetectedPreset> Discover()
    {
        var locations = Locations();
        var registrations = PresetDiscovery.Registrations();
        var detected = new List<DetectedPreset>();
        foreach (var definition in Definitions)
        {
            var components = new List<DetectedComponent>();
            foreach (var component in definition.Components)
            {
                if (!PresetDiscovery.RequirementsMet(component, locations))
                    continue;
                if (component.Package.Length > 0)
                {
                    // Only registered packages count as installed. Keep a version-independent routing rule.
                    var installedPackages = InstalledAppLocations.PackageDirectories(component.Package);
                    var packageRoots = installedPackages.Select(Path.GetDirectoryName).OfType<string>()
                        .Append(Path.Combine(locations["ProgramFiles"], "WindowsApps")).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (string packageRoot in packageRoots)
                    {
                        string exe = installedPackages.Where(p => string.Equals(Path.GetDirectoryName(p), packageRoot, StringComparison.OrdinalIgnoreCase))
                            .Select(p => FindPackageExecutable(p, component.Executable)).FirstOrDefault(p => p.Length > 0) ?? "";
                        var rule = new AppRule
                        {
                            Name = component.Name,
                            Path = Path.Combine(packageRoot, component.Package + "_*"),
                            Pattern = "(?i)^" + Regex.Escape(packageRoot.TrimEnd('\\')) + @"\\" + Regex.Escape(component.Package) + @"_[^\\]+\\.*\.exe$"
                        };
                        components.Add(new(Stamp(rule, definition, exe, component), exe, component.Suggested));
                    }
                    continue;
                }
                var paths = component.Locations.Select(path => Expand(path, locations));
                if (component.AppPath.Length > 0)
                    paths = paths.Concat(PresetDiscovery.AppPaths(component.AppPath, component.ProductContains));
                if (component.Registration.Length > 0)
                    paths = paths.Concat(registrations
                    .Where(r => Regex.IsMatch(r.Name, component.Registration, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    .Select(r => r.Directory));
                foreach (string template in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string path in PresetDiscovery.Directories(template))
                    {
                        var executables = component.Recursive && component.FileOnly ? PresetDiscovery.Executables(path, component.Executable)
                            : new[] { FindExecutable(path, component.Executable, component.Recursive || component.Executable.Length == 0) };
                        foreach (string exe in executables.DefaultIfEmpty(""))
                        {
                            var rule = PresetDiscovery.Rule(template, path, exe, component);
                            components.Add(new(Stamp(rule, definition, exe, component), exe, component.Suggested));
                        }
                    }
                }
                if (component.SearchPath)
                    foreach (string exe in PresetDiscovery.PathExecutables(component.Executable))
                        components.Add(new(Stamp(AppRule.File(exe, component.Name), definition, exe, component), exe, component.Suggested));
            }
            string icon = definition.IconFiles.Select(t => Expand(t, locations)).SelectMany(p => PresetDiscovery.Directories(Path.GetDirectoryName(p)!).Select(d => Path.Combine(d, Path.GetFileName(p)))).FirstOrDefault(File.Exists) ?? "";
            if (icon.Length > 0)
                foreach (var component in components)
                    component.Rule.IconPath = icon;
            detected.Add(new(definition, PresetDiscovery.NormalizeComponents(components)));
        }
        return detected;
    }
    static AppRule Stamp(AppRule rule, PresetDefinition definition, string executable, PresetComponent component)
    {
        rule.GroupId = "preset:" + definition.Id;
        rule.GroupName = PresetHierarchy.QualifiedName(definition, Definitions);
        rule.IconPath = executable;
        rule.ComponentRole = component.Role;
        rule.ScopeNote = component.ScopeNote;
        return rule;
    }
    static string FindPackageExecutable(string folder, string fallback)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(Path.Combine(folder, "AppxManifest.xml"),
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var manifest = System.Xml.Linq.XDocument.Load(reader);
            foreach (var app in manifest.Descendants().Where(e => e.Name.LocalName == "Application"))
            {
                string relative = (string?)app.Attribute("Executable") ?? "";
                if (relative.Length == 0)
                    continue;
                string executable = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', '\\')));
                if (executable.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(executable))
                    return executable;
            }
        }
        catch { }
        return FindExecutable(folder, fallback, true);
    }
    internal static string FindExecutable(string folder, string executable, bool recursive)
    {
        try
        {
            if (!Directory.Exists(folder))
                return "";
            if (!recursive)
            {
                string path = Path.Combine(folder, executable);
                return File.Exists(path) ? path : "";
            }
            return Directory.EnumerateFiles(folder, executable.Length == 0 ? "*.exe" : executable,
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MaxRecursionDepth = 10
                }).FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }
}

internal sealed class ApplicationGroup
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string IconPath { get; set; } = "";
    public List<AppRule> Rules { get; } = [];
}
internal static class ApplicationGroups
{
    public static List<ApplicationGroup> Create(IEnumerable<AppRule> rules, IEnumerable<DetectedPreset> presets, bool resolveIcons = true)
    {
        var patterns = new Dictionary<string, DetectedComponent>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, DetectedComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in presets.SelectMany(p => p.Components))
        {
            patterns.TryAdd(component.Rule.Pattern, component);
            paths.TryAdd(component.Rule.Path, component);
        }
        var groups = new Dictionary<string, ApplicationGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            // Migrate only exact known rules; a similar name alone must never merge unrelated applications.
            patterns.TryGetValue(rule.Pattern, out var detected);
            if (detected == null)
                paths.TryGetValue(rule.Path, out detected);
            string id = rule.GroupId.Length > 0 ? rule.GroupId : detected?.Rule.GroupId ?? "manual:" + rule.Pattern;
            string name = rule.GroupName.Length > 0 ? rule.GroupName : detected?.Rule.GroupName ?? rule.Name;
            string icon = resolveIcons ? File.Exists(rule.IconPath) ? rule.IconPath : detected?.Executable ?? (File.Exists(rule.Path) ? rule.Path : "") : "";
            if (!groups.TryGetValue(id, out var group))
                groups[id] = group = new()
                {
                    Id = id,
                    Name = name,
                    IconPath = icon
                };
            if (group.IconPath.Length == 0)
                group.IconPath = icon;
            group.Rules.Add(rule);
        }
        return groups.Values.ToList();
    }
    public static void Add(VpnProfile profile, IEnumerable<AppRule> rules)
    {
        var byPattern = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in profile.Apps)
            byPattern.TryAdd(existing.Pattern, existing);
        foreach (var rule in rules)
        {
            if (!byPattern.TryGetValue(rule.Pattern, out var existing))
            {
                var copy = rule.Copy();
                profile.Apps.Add(copy);
                byPattern.Add(copy.Pattern, copy);
            }
            else if (rule.GroupId.Length > 0)
            {
                existing.GroupId = rule.GroupId;
                existing.GroupName = rule.GroupName;
                existing.IconPath = rule.IconPath;
                existing.ComponentRole = rule.ComponentRole;
                existing.ScopeNote = rule.ScopeNote;
            }
        }
    }
}
