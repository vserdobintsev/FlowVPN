using System.Text.Json;

namespace FlowVPN;

internal static class OnboardingChecks
{
    public static void Run(Action<bool, string> check)
    {
        static DetectedPreset Preset(string id, bool recommended = true, string parent = "")
        {
            var rule = AppRule.File(@"D:\Fixture\" + id + ".exe", id);
            rule.GroupId = "preset:" + id;
            rule.GroupName = id;
            rule.IconPath = rule.Path;
            rule.ComponentRole = "engine";
            rule.ScopeNote = "Fixture scope";
            return new(new() { Id = id, Name = id, Description = id + " description", RecommendedProfile = recommended, ParentId = parent, ProfileIcon = "code" }, [new(rule, rule.Path)]);
        }
        static Settings SettingsWithSources() => new()
        {
            Sources = [new() { Id = "first", Kind = "OpenVPN" }, new() { Id = "second", Kind = "Outline" }]
        };
        var first = Preset("alpha");
        var second = Preset("beta");
        var addon = Preset("addon", parent: "alpha");
        var missing = Preset("missing");
        missing.Components[0] = missing.Components[0] with { Executable = "" };
        var optionalOnly = Preset("optional");
        optionalOnly.Components[0] = optionalOnly.Components[0] with { Suggested = false };
        var duplicate = first.Components[0].Rule.Copy();
        duplicate.Pattern = duplicate.Pattern.ToUpperInvariant();
        first.Components.Add(new(duplicate, duplicate.Path));
        var worker = AppRule.File(@"D:\Fixture\alpha-worker.exe", "worker");
        worker.GroupId = "preset:alpha";
        first.Components.Add(new(worker, worker.Path));
        first.Components.Add(new(AppRule.File(@"D:\Fixture\shared-host.exe"), @"D:\Fixture\shared-host.exe", Suggested: false));
        first.Components.Add(new(AppRule.File(@"D:\Fixture\not-installed.exe"), ""));
        DetectedPreset[] catalog = [first, second, addon, missing, optionalOnly];
        var settings = SettingsWithSources();
        settings.SelectedProfileId = "unchanged";
        string before = JsonSerializer.Serialize(settings);
        var choices = PresetProfileFactory.Suggestions(settings, catalog);
        check(JsonSerializer.Serialize(settings) == before, "Profile suggestions never create profiles, alter selection or complete onboarding");
        check(choices.Any(p => p.Id == "base-only") && choices.Count(p => p.Id.StartsWith("recommended:", StringComparison.Ordinal)) == 1,
            "Enabled base and two recommended root applications produce separate opt-in choices");
        var combined = choices.Single(p => p.Id.StartsWith("recommended:", StringComparison.Ordinal));
        check(combined.AppCount == 2 && combined.RuleCount == 3 && !combined.Apps.Any(a => a.GroupId == "preset:addon"),
            "Recommended profile counts applications separately and does not silently include host extensions");
        var firstChoice = choices.Single(p => p.Id == "preset:alpha");
        check(firstChoice.RuleCount == 2 && !firstChoice.Apps.Any(a => a.Path.Contains("shared-host", StringComparison.Ordinal) || a.Path.Contains("not-installed", StringComparison.Ordinal)),
            "Suggestions include only available suggested components and deduplicate routing patterns");
        check(choices.All(p => p.Id is not ("preset:missing" or "preset:optional")) && choices.Single(p => p.Id == "preset:addon").Name == "addon · alpha",
            "Unavailable or entirely optional sets are omitted and extensions name their host");
        firstChoice.Apps[0].Name = "Changed card";
        check(first.Components[0].Rule.Name == "alpha" && combined.Apps.Single(a => a.Pattern == first.Components[0].Rule.Pattern).Name == "alpha",
            "Suggestion cards own independent rule objects without mutating discovery");
        check(PresetProfileFactory.Suggestions(settings, catalog.Reverse().ToArray()).Select(p => p.Id).SequenceEqual(choices.Select(p => p.Id)),
            "Suggestion identifiers and ordering are deterministic across discovery order");
        check(!PresetProfileFactory.Suggestions(settings, [first, addon]).Any(p => p.Id.StartsWith("recommended:", StringComparison.Ordinal)),
            "An extension does not count as a second independent recommended application");

        var none = PresetProfileFactory.CreateSelected(settings, "second", [], catalog);
        check(none.Added == 0 && settings.Profiles.Count == 0 && settings.SelectedProfileId == "unchanged" && !settings.ProfileOnboardingCompleted,
            "Empty confirmation creates nothing and does not change onboarding state");
        var absentSource = PresetProfileFactory.CreateSelected(settings, "missing-source", ["base-only", "preset:alpha"], catalog);
        check(absentSource.SourceMissing && absentSource.Added == 0 && absentSource.Unavailable == 2 && settings.Profiles.Count == 0,
            "A removed selected source never falls back to another connection");
        var result = PresetProfileFactory.CreateSelected(settings, "second", ["base-only", "preset:alpha", combined.Id, "preset:alpha", "unknown"], catalog);
        check(result.Added == 3 && result.Duplicates == 0 && result.Unavailable == 1 && settings.Profiles.All(p => p.SourceId == "second" && p.Mode == "include") &&
            settings.SelectedProfileId == settings.Profiles[^1].Id, "Explicit confirmation creates only selected available profiles on the chosen connection");
        var singleProfile = settings.Profiles.Single(p => p.Name == "alpha");
        var combinedProfile = settings.Profiles.Single(p => p.Name == combined.Name);
        check(singleProfile.Apps[0].Name == "alpha" && singleProfile.Apps[0].IconPath == first.Components[0].Rule.IconPath &&
            singleProfile.Apps[0].ComponentRole == "engine" && singleProfile.Apps[0].ScopeNote == "Fixture scope",
            "Creation rebuilds fresh discovery and copies every rule field rather than trusting edited cards");
        singleProfile.Apps[0].Name = "Only this profile";
        singleProfile.Name = "My renamed profile";
        singleProfile.Icon = "chat";
        singleProfile.IconColor = 4;
        check(combinedProfile.Apps.Single(a => a.Pattern == first.Components[0].Rule.Pattern).Name == "alpha" && first.Components[0].Rule.Name == "alpha",
            "Created profiles and discovery retain independent mutable rule objects");
        string selection = settings.SelectedProfileId;
        var repeated = PresetProfileFactory.CreateSelected(settings, "second", ["preset:alpha", combined.Id, "base-only"], catalog);
        check(repeated.Added == 0 && repeated.Duplicates == 3 && settings.Profiles.Count == 3 && settings.SelectedProfileId == selection &&
            singleProfile.Name == "My renamed profile" && singleProfile.Icon == "chat" && singleProfile.IconColor == 4,
            "Semantic duplicates preserve existing names, appearance, rules and selection");
        var anotherSource = PresetProfileFactory.CreateSelected(settings, "first", ["preset:alpha"], catalog);
        check(anotherSource.Added == 1 && settings.Profiles[^1].SourceId == "first", "Identical application rules on another connection remain a distinct profile");

        var collisions = SettingsWithSources();
        collisions.Profiles.Add(new() { Name = "ALPHA", SourceId = "first" });
        collisions.Profiles.Add(new() { Name = "alpha (2)", SourceId = "first" });
        check(PresetProfileFactory.CreateSelected(collisions, "second", ["preset:alpha"], catalog).Added == 1 &&
            collisions.Profiles[^1].Name == "alpha (3)" && collisions.Profiles[0].Name == "ALPHA" && collisions.Profiles[1].Name == "alpha (2)",
            "New profile names avoid case-insensitive collisions and occupied suffixes without renaming existing profiles");
        check(PresetProfileFactory.CreateSelected(collisions, "first", ["preset:alpha"], catalog).Added == 1 &&
            collisions.Profiles[^1].Name == "alpha (4)" && collisions.Profiles[^2].Name == "alpha (3)",
            "Equivalent suggestions on another source receive distinct sidebar names");

        var scoped = SettingsWithSources();
        scoped.Profiles.Add(new() { SourceId = "second", Mode = "exclude", Apps = firstChoice.Apps.Select(a => a.Copy()).ToList() });
        scoped.Profiles.Add(new() { SourceId = "second", Apps = firstChoice.Apps.Select(a => a.Copy()).ToList(), Domains = ["example.org"] });
        check(PresetProfileFactory.CreateSelected(scoped, "second", ["preset:alpha"], catalog).Added == 1,
            "Different routing modes or additional domains do not suppress a requested profile");
        var disappeared = PresetProfileFactory.CreateSelected(SettingsWithSources(), "first", ["preset:alpha", combined.Id], [second]);
        check(disappeared.Added == 0 && disappeared.Unavailable == 2, "Fresh discovery rejects disappeared applications and a no-longer-valid combined choice");
        var expanded = PresetProfileFactory.CreateSelected(SettingsWithSources(), "first", [combined.Id], [first, second, Preset("third")]);
        check(expanded.Added == 0 && expanded.Unavailable == 1, "Newly discovered recommended applications cannot silently broaden a previously selected combined profile");
        var baseDisabled = SettingsWithSources();
        baseDisabled.BaseRulesEnabled = false;
        check(PresetProfileFactory.Suggestions(baseDisabled, catalog).All(p => p.Id != "base-only") &&
            PresetProfileFactory.CreateSelected(baseDisabled, "first", ["base-only"], catalog).Unavailable == 1,
            "Disabling the shared base removes its suggestion and invalidates stale confirmation");
        var skipped = SettingsWithSources();
        skipped.ProfileOnboardingCompleted = true;
        string saved = JsonSerializer.Serialize(skipped);
        var restored = JsonSerializer.Deserialize<Settings>(saved)!;
        check(restored.ProfileOnboardingCompleted && PresetProfileFactory.Suggestions(restored, catalog).Count > 0 && restored.Profiles.Count == 0 &&
            restored.ProfileOnboardingCompleted, "Skipping persists across settings reload while manual reopening remains available without creating profiles");
        check(!JsonSerializer.Deserialize<Settings>("{}")!.ProfileOnboardingCompleted,
            "Older settings deserialize the onboarding marker without implicit profile creation");
        var many = Enumerable.Range(0, ProfileSetCatalog.MaximumMembers + 1).Select(i => Preset("item-" + i.ToString("D3"))).ToArray();
        bool rejected = false;
        try { _ = PresetProfileFactory.Suggestions(SettingsWithSources(), many); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "An oversized future catalog is rejected explicitly instead of silently hiding profile choices");
    }
}
