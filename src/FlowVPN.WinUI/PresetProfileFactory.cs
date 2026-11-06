namespace FlowVPN;

internal sealed record ProfileSuggestion(string Id, string Name, string Description, string Icon,
    IReadOnlyList<AppRule> Apps, int AppCount)
{
    public int RuleCount => Apps.Count;
}

internal sealed record ProfileCreationResult(int Added, int Duplicates, int Unavailable, bool SourceMissing = false);

// Discovery proposes choices; only an explicit confirmation creates profiles.
internal static class PresetProfileFactory
{
    internal const int MaximumSuggestions = ProfileSetCatalog.MaximumSets + ProfileSetCatalog.MaximumMembers + 2;

    public static IReadOnlyList<ProfileSuggestion> Suggestions(Settings settings, IReadOnlyCollection<DetectedPreset> presets)
    {
        var entries = ProfileSetCatalog.Entries(settings, presets);
        var map = presets.DistinctBy(p => p.Definition.Id, StringComparer.Ordinal).ToDictionary(p => p.Definition.Id, StringComparer.Ordinal);
        var suggestions = new List<ProfileSuggestion>();
        foreach (var entry in entries.Where(entry => entry.PresetIds.Count > 0 && entry.MissingCount == 0))
        {
            var rules = CopyDistinct(entry.PresetIds.SelectMany(id => map[id].Components)
                .Where(component => component.Available && component.Suggested).Select(component => component.Rule));
            suggestions.Add(new(entry.Id, entry.Name, entry.Description, entry.Icon, rules, entry.PresetIds.Count));
        }
        if (settings.BaseRulesEnabled)
            suggestions.Add(new("base-only", "Только базовые правила", "Общая база блокировок без дополнительных приложений.", "rules", [], 0));
        return suggestions.AsReadOnly();
    }

    public static ProfileCreationResult CreateSelected(Settings settings, string sourceId, IEnumerable<string> selectedIds,
        IReadOnlyCollection<DetectedPreset> freshPresets)
    {
        string[] selected = selectedIds.Take(MaximumSuggestions).Distinct(StringComparer.Ordinal).ToArray();
        if (!settings.Sources.Any(source => source.Id == sourceId))
            return new(0, 0, selected.Length, SourceMissing: true);

        // Rebuild from fresh discovery instead of trusting mutable cards from an earlier dialog.
        var suggestions = Suggestions(settings, freshPresets).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var existingRules = settings.Profiles.Where(p => p.SourceId == sourceId && p.Mode == "include" && p.Domains.Count == 0)
            .Select(p => p.Apps.Select(a => a.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToList();
        var names = settings.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0, duplicates = 0, unavailable = 0;
        foreach (string id in selected)
        {
            if (!suggestions.TryGetValue(id, out var suggestion))
            {
                unavailable++;
                continue;
            }
            var patterns = suggestion.Apps.Select(a => a.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (existingRules.Any(patterns.SetEquals))
            {
                duplicates++;
                continue;
            }
            var profile = new VpnProfile
            {
                Name = UniqueNames.Reserve(suggestion.Name, names),
                SourceId = sourceId,
                Mode = "include",
                Icon = suggestion.Icon,
                Apps = suggestion.Apps.Select(rule => rule.Copy()).ToList()
            };
            settings.Profiles.Add(profile);
            settings.SelectedProfileId = profile.Id;
            existingRules.Add(patterns);
            added++;
        }
        return new(added, duplicates, unavailable);
    }

    static IReadOnlyList<AppRule> CopyDistinct(IEnumerable<AppRule> rules) => rules
        .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
        .DistinctBy(rule => rule.Pattern, StringComparer.OrdinalIgnoreCase)
        .Select(rule => rule.Copy()).ToList().AsReadOnly();
}
