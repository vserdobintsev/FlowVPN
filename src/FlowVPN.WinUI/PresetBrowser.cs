namespace FlowVPN;

internal sealed class PresetBrowser(IReadOnlyList<CatalogApp> presets)
{
    public string ParentId { get; private set; } = "";
    public CatalogApp? Parent => presets.FirstOrDefault(p => p.PresetId == ParentId);
    public bool Enter(string id)
    {
        var app = presets.FirstOrDefault(p => p.PresetId == id);
        if (app == null || !PresetHierarchy.HasChildren(app, presets))
            return false;
        ParentId = id;
        return true;
    }
    public void Back() => ParentId = Parent?.ParentId ?? "";
    public void Home() => ParentId = "";
    public List<CatalogApp> Visible(string query)
    {
        if (query.Trim().Length > 0)
            return presets.Where(p => PresetHierarchy.Matches(p, query.Trim(), presets)).ToList();
        var children = PresetHierarchy.Children(ParentId, presets);
        if (Parent == null)
            children = children.Where(p => !PresetHierarchy.HasChildren(p, presets) || p.Rules.Count > 0 || PresetHierarchy.Children(p.PresetId, presets).Any(c => c.Rules.Count > 0))
            .OrderByDescending(p => PresetHierarchy.HasChildren(p, presets)).ToList();
        return (Parent == null ? [] : new[] { Parent }).Concat(children).ToList();
    }
    public bool IsNavigation(CatalogApp app, string query) => PresetHierarchy.HasChildren(app, presets) && (app.PresetId != ParentId || query.Trim().Length > 0);
}
