namespace FlowVPN;

// Data describes relationships. The picker has no branches for VS Code or a particular extension.
internal static class PresetHierarchy
{
    public static void Validate(IReadOnlyList<PresetDefinition> definitions)
    {
        if (definitions.Any(d => string.IsNullOrWhiteSpace(d.Id)) || definitions.Select(d => d.Id).Distinct().Count() != definitions.Count)
            throw new InvalidDataException("Неверные идентификаторы готовых наборов.");
        var map = definitions.ToDictionary(d => d.Id);
        foreach (var item in definitions)
        {
            var visited = new HashSet<string> { item.Id };
            var parent = item.ParentId;
            while (parent.Length > 0)
            {
                if (!map.TryGetValue(parent, out var ancestor) || !visited.Add(parent))
                    throw new InvalidDataException("Неверная иерархия приложений и расширений.");
                parent = ancestor.ParentId;
            }
        }
    }
    public static string QualifiedName(PresetDefinition definition, IReadOnlyList<PresetDefinition> definitions) =>
        definition.ParentId.Length == 0 ? definition.Name : definition.Name + " · " + definitions.First(d => d.Id == definition.ParentId).Name;
    public static bool HasChildren(CatalogApp app, IEnumerable<CatalogApp> presets) => presets.Any(p => p.ParentId == app.PresetId && app.PresetId.Length > 0);
    public static List<CatalogApp> Children(string parent, IEnumerable<CatalogApp> presets) => presets.Where(p => p.ParentId == parent).ToList();
    public static string Context(CatalogApp app, IEnumerable<CatalogApp> presets) => presets.FirstOrDefault(p => p.PresetId == app.ParentId)?.Name ?? "";
    public static bool Matches(CatalogApp app, string query, IEnumerable<CatalogApp> presets) =>
        (app.Name + " " + Context(app, presets) + " " + app.Path + " " + string.Join(" ", app.Rules.Select(r => r.Name)))
            .Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
