using System.Text.Json;
namespace FlowVPN;

internal static class PresetProfileFactory
{
    static List<AppRule> Copy(IEnumerable<AppRule> rules) => JsonSerializer.Deserialize<List<AppRule>>(JsonSerializer.Serialize(rules))!;
    static List<DetectedPreset> Defaults(IReadOnlyCollection<DetectedPreset> presets) => presets.Where(p => p.Available && p.Definition.DefaultSelection).ToList();
    static string DefaultName(IReadOnlyCollection<DetectedPreset> presets) => presets.Count == 0 ? "Мои приложения" : string.Join(" + ", presets.Select(p => p.Definition.Name));
    public static void Seed(Settings settings, IReadOnlyCollection<DetectedPreset> presets)
    {
        if (settings.Profiles.Count != 0 || settings.Sources.Count == 0)
            return;
        var defaults = Defaults(presets);
        var source = settings.Sources[0];
        settings.Profiles.Add(new()
        {
            Name = DefaultName(defaults),
            SourceId = source.Id,
            Apps = Copy(defaults.SelectMany(p => p.Rules))
        });
        settings.Profiles.Add(new()
        {
            Name = "Только базовые правила",
            SourceId = source.Id
        });
        settings.SelectedProfileId = settings.Profiles[0].Id;
        Complete(settings, presets);
    }
    public static void Complete(Settings settings, IReadOnlyCollection<DetectedPreset> presets)
    {
        if (settings.Sources.Count == 0)
            return;
        var defaults = Defaults(presets);
        foreach (var source in settings.Sources.Where(s => settings.Profiles.All(p => p.SourceId != s.Id)))
            settings.Profiles.Add(new()
            {
                Name = source.Name + " · " + DefaultName(defaults),
                SourceId = source.Id,
                Apps = Copy(defaults.SelectMany(p => p.Rules))
            });
        foreach (var preset in presets.Where(p => p.Available && p.Definition.CreateProfile))
        {
            if (settings.Profiles.Any(p => p.Apps.Any(a => preset.Rules.Any(r => r.Pattern.Equals(a.Pattern, StringComparison.OrdinalIgnoreCase)))))
                continue;
            settings.Profiles.Add(new()
            {
                Name = preset.Definition.Name,
                SourceId = settings.Sources[0].Id,
                Apps = Copy(preset.Rules)
            });
        }
    }
}
