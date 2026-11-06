namespace FlowVPN;

// A versioned catalogue can rename/split a group, but must never change its saved routing scope.
// Run explicitly at the load/command boundary. Rendering and icon selection stay read-only.
internal static class CatalogRuleMigration
{
    public static bool Apply(IEnumerable<AppRule> rules, IReadOnlyList<DetectedPreset> presets)
    {
        bool changed = false;
        var known = presets.SelectMany(p => p.Components.Select(c => (Preset: p, Component: c))).ToArray();
        foreach (var rule in rules)
        {
            var match = known.FirstOrDefault(c => c.Component.Rule.Pattern.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase)
                || c.Component.Rule.Path.Equals(rule.Path, StringComparison.OrdinalIgnoreCase));
            if (match.Component == null)
                continue;
            var detected = match.Component.Rule;
            if (match.Preset.Definition.LegacyGroupIds.Contains(rule.GroupId, StringComparer.OrdinalIgnoreCase))
            {
                rule.GroupId = detected.GroupId;
                rule.GroupName = detected.GroupName;
                rule.Name = detected.Name;
                rule.IconPath = detected.IconPath;
                changed = true;
            }
            if (rule.ComponentRole != detected.ComponentRole || rule.ScopeNote != detected.ScopeNote)
            {
                rule.ComponentRole = detected.ComponentRole;
                rule.ScopeNote = detected.ScopeNote;
                changed = true;
            }
        }
        return changed;
    }
}
