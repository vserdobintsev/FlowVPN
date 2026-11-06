namespace FlowVPN;

internal static class InstalledAppLocations
{
    static readonly Dictionary<string, List<string>> Packages = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> PackageDirectories(string name) => Packages.TryGetValue(name, out var paths) ? paths : [];
    public static void Initialize()
    {
        Store.SeedCatalogProfiles = settings => PresetProfileFactory.Seed(settings, AppPresets.Discover());
        Packages.Clear();
        var requested = AppPresets.Definitions.SelectMany(p => p.Components).Select(c => c.Package).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(""))
            {
                try
                {
                    string name = package.Id.Name;
                    if (!requested.Contains(name))
                        continue;
                    if (!Packages.TryGetValue(name, out var paths))
                        Packages[name] = paths = [];
                    paths.Add(package.InstalledLocation.Path);
                }
                catch { }
            }
        }
        catch { }
    }
}
