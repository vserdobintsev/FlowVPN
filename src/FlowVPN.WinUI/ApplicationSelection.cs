namespace FlowVPN;

// Selection is independent of visible rows, sorting, icons and asynchronous discovery.
internal sealed class ApplicationSelection(VpnProfile profile)
{
    readonly HashSet<string> existing = profile.Apps.Select(r => r.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (CatalogApp App, HashSet<string> Patterns)> chosen = new(StringComparer.OrdinalIgnoreCase);
    public int Count => chosen.Count;
    public IReadOnlyCollection<string> SelectedIds => chosen.Keys;
    public int Components => chosen.Values.Sum(v => v.Patterns.Count);
    public bool Existing(AppRule rule) => existing.Contains(rule.Pattern);
    public bool Contains(CatalogApp app, AppRule rule) => chosen.TryGetValue(app.Id, out var value) && value.Patterns.Contains(rule.Pattern);
    public int Selected(CatalogApp app) => chosen.TryGetValue(app.Id, out var value) ? value.Patterns.Count : 0;
    public void SetComponent(CatalogApp app, AppRule rule, bool selected)
    {
        if (Existing(rule))
            return;
        if (!chosen.TryGetValue(app.Id, out var value))
            value = (app, new(StringComparer.OrdinalIgnoreCase));
        if (selected)
            value.Patterns.Add(rule.Pattern);
        else
            value.Patterns.Remove(rule.Pattern);
        if (value.Patterns.Count > 0)
            chosen[app.Id] = value;
        else
            chosen.Remove(app.Id);
    }
    public void SetApplication(CatalogApp app, bool selected)
    {
        chosen.Remove(app.Id);
        if (!selected)
            return;
        var missing = app.Rules.Where(r => !Existing(r)).ToArray();
        var suggested = missing.Where(r => app.SuggestedPatterns == null || app.SuggestedPatterns.Contains(r.Pattern)).ToArray();
        foreach (var rule in suggested)
            SetComponent(app, rule, true);
    }
    public List<AppRule> Resolve(IReadOnlyCollection<DetectedPreset> fresh)
    {
        return chosen.Values.SelectMany(v => (v.App.PresetId.Length > 0
            ? fresh.FirstOrDefault(p => p.Definition.Id == v.App.PresetId)?.Rules ?? []
            : File.Exists(v.App.Path) ? v.App.Rules : [])
            .Where(r => v.Patterns.Contains(r.Pattern))).ToList();
    }
}
