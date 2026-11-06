using System.Text.Json;

namespace FlowVPN;

internal static class ProfileSetChecks
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, label);
        }
        static DetectedPreset Preset(string id)
        {
            var rule = AppRule.File(@"D:\PrivateDevice\" + id + ".exe", id);
            rule.GroupId = "preset:" + id;
            return new(new() { Id = id, Name = id, Description = id, RecommendedProfile = id is "alpha" or "beta" }, [new(rule, rule.Path)]);
        }
        DetectedPreset[] presets = [Preset("alpha"), Preset("beta"), Preset("gamma"), Preset("delta")];
        presets[1].Components.Add(new(AppRule.File(@"D:\PrivateDevice\shared-host.exe"), @"D:\PrivateDevice\shared-host.exe", Suggested: false));
        var settings = new Settings();
        var a = ProfileSetCatalog.Create(settings, "A", ["alpha", "beta", "alpha"]);
        var b = ProfileSetCatalog.Create(settings, "B", ["beta", "gamma"]);
        var c = ProfileSetCatalog.Create(settings, "C", ["alpha", "beta", "delta"]);
        var entries = ProfileSetCatalog.Entries(settings, presets);
        string EntryId(ApplicationSet set) => entries.Single(entry => entry.CustomSetId == set.Id).Id;
        string aId = EntryId(a), bId = EntryId(b), cId = EntryId(c);
        string parentsBefore = JsonSerializer.Serialize(settings.ApplicationSets);
        check(settings.Sources.Count == 0 && settings.Profiles.Count == 0 && a.PresetIds.SequenceEqual(new[] { "alpha", "beta" }),
            "Application sets save canonical unique identities without requiring a VPN source or creating profiles");
        string[][] permutations = [[aId, bId, cId], [aId, cId, bId], [bId, aId, cId], [bId, cId, aId], [cId, aId, bId], [cId, bId, aId]];
        foreach (var order in permutations)
        {
            check(ProfileSetCatalog.Evaluate(entries, order, ProfileSetOperation.Union).SequenceEqual(new[] { "alpha", "beta", "delta", "gamma" }),
                "Union is independent of operand selection order: " + Array.IndexOf(permutations, order));
            check(ProfileSetCatalog.Evaluate(entries, order, ProfileSetOperation.Intersection).SequenceEqual(new[] { "beta" }),
                "Intersection is independent of operand selection order: " + Array.IndexOf(permutations, order));
            check(ProfileSetCatalog.Evaluate(entries, order, ProfileSetOperation.Difference, cId).SequenceEqual(new[] { "delta" }),
                "Difference uses its explicit base rather than click order: " + Array.IndexOf(permutations, order));
            check(ProfileSetCatalog.Evaluate(entries, order, ProfileSetOperation.SymmetricDifference).SequenceEqual(new[] { "beta", "delta", "gamma" }),
                "N-ary symmetric difference retains odd parity including three occurrences: " + Array.IndexOf(permutations, order));
        }
        check(ProfileSetCatalog.Evaluate(entries, [aId, aId, bId], ProfileSetOperation.SymmetricDifference).SequenceEqual(new[] { "alpha", "gamma" }),
            "Duplicate operand selections do not alter set parity");
        check(JsonSerializer.Serialize(settings.ApplicationSets) == parentsBefore && a.PresetIds.Count == 2,
            "All algebra operations preserve their parent sets and normalized members");
        Reject(() => ProfileSetCatalog.Evaluate(entries, [aId, bId], ProfileSetOperation.Difference), "Difference rejects an unspecified base set");
        Reject(() => ProfileSetCatalog.Evaluate(entries, [aId, bId], ProfileSetOperation.Difference, cId), "Difference rejects a base outside the selected operands");
        Reject(() => ProfileSetCatalog.Evaluate(entries, [aId, aId], ProfileSetOperation.Union), "An operation requires two distinct operands");
        Reject(() => ProfileSetCatalog.Evaluate(entries, [aId, "base-only"], ProfileSetOperation.Union), "The shared blocklist is not an application-set operand");
        Reject(() => ProfileSetCatalog.Evaluate(entries, [aId, bId], (ProfileSetOperation)99), "Unknown algebra operations fail closed");
        var empty = ProfileSetCatalog.Evaluate(entries, ["preset:alpha", "preset:gamma"], ProfileSetOperation.Intersection);
        check(empty.Count == 0, "An empty intersection remains a valid preview");
        Reject(() => ProfileSetCatalog.Create(settings, "Empty", empty), "An empty algebra result cannot be saved as a set");

        var result = ProfileSetCatalog.Evaluate(entries, [aId, bId], ProfileSetOperation.Union);
        var composed = ProfileSetCatalog.Create(settings, "Combined", result);
        a.PresetIds[0] = "delta";
        check(composed.PresetIds.SequenceEqual(new[] { "alpha", "beta", "gamma" }) && composed.Id != a.Id && Guid.TryParse(composed.Id, out _),
            "Saving creates a standalone member snapshot with a fresh identity rather than references to operands");
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(composed)))
            check(document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(new[] { "Id", "Name", "Icon", "PresetIds" }), "Serialized reusable sets contain no machine paths, routing expressions or source binding");
        var reloaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        check(reloaded.ApplicationSets.Single(set => set.Id == composed.Id).PresetIds.SequenceEqual(composed.PresetIds) &&
            JsonSerializer.Deserialize<Settings>("{}")!.ApplicationSets.Count == 0,
            "Reusable sets round-trip portably and older settings default to an empty set list");

        var missingSettings = new Settings { Sources = [new() { Id = "vpn" }] };
        var missing = ProfileSetCatalog.Create(missingSettings, "Portable", ["alpha", "not-installed"]);
        var missingEntry = ProfileSetCatalog.Entries(missingSettings, presets).Single(entry => entry.CustomSetId == missing.Id);
        var missingMembers = ProfileSetCatalog.Members(missingEntry.PresetIds, presets);
        check(missingEntry.AvailableCount == 1 && missingEntry.MissingCount == 1 && missingMembers.Single(member => member.Id == "not-installed") is { Available: false, Executable: "" } &&
            missing.PresetIds.Contains("not-installed"), "Unavailable application identities are preserved and reported without invented local executable paths");
        check(PresetProfileFactory.Suggestions(missingSettings, presets).All(choice => choice.Id != missingEntry.Id) &&
            PresetProfileFactory.CreateSelected(missingSettings, "vpn", [missingEntry.Id], presets).Unavailable == 1 && missingSettings.Profiles.Count == 0,
            "A custom set with a missing member cannot silently create a truncated profile");
        var restoredPresets = presets.Append(Preset("not-installed")).ToArray();
        check(PresetProfileFactory.CreateSelected(missingSettings, "vpn", [missingEntry.Id], restoredPresets).Added == 1,
            "The same portable set becomes profile-ready when all its applications are discovered");

        var profileSettings = new Settings { Sources = [new() { Id = "vpn" }], ProfileOnboardingCompleted = true };
        var saved = ProfileSetCatalog.Create(profileSettings, "Owned set", ["alpha", "beta"]);
        var savedEntry = ProfileSetCatalog.Entries(profileSettings, presets).Single(entry => entry.CustomSetId == saved.Id);
        check(PresetProfileFactory.CreateSelected(profileSettings, "removed", [savedEntry.Id], presets).SourceMissing && profileSettings.Profiles.Count == 0,
            "A reusable set never bypasses the selected VPN source guard");
        check(PresetProfileFactory.CreateSelected(profileSettings, "vpn", [savedEntry.Id], presets).Added == 1 &&
            profileSettings.Profiles.Single().Apps.Count == 2 && profileSettings.Profiles.Single().Apps.All(rule => !rule.Path.Contains("shared-host", StringComparison.Ordinal)),
            "Set profiles resolve fresh suggested components and exclude optional shared host processes");
        var profile = profileSettings.Profiles.Single();
        string profileBefore = JsonSerializer.Serialize(profile);
        saved.PresetIds.Add("gamma");
        check(PresetProfileFactory.CreateSelected(profileSettings, "vpn", [savedEntry.Id], presets).Unavailable == 1,
            "A changed saved-set composition invalidates a stale profile confirmation");
        ProfileSetCatalog.Rename(profileSettings, saved.Id, "Renamed set");
        check(ProfileSetCatalog.Delete(profileSettings, saved.Id) && JsonSerializer.Serialize(profile) == profileBefore && profileSettings.ProfileOnboardingCompleted,
            "Renaming or deleting a saved set preserves existing profile snapshots and onboarding state");
        check(!ProfileSetCatalog.Delete(profileSettings, "preset:alpha") && ProfileSetCatalog.Entries(profileSettings, presets).Any(entry => entry.Id == "preset:alpha"),
            "Built-in sets cannot be deleted through the custom-set API");
        Reject(() => ProfileSetCatalog.Rename(profileSettings, "preset:alpha", "Changed"), "Built-in set names cannot be overwritten by custom-set editing");

        var naming = new Settings();
        var first = ProfileSetCatalog.Create(naming, "Name", ["alpha"]);
        var second = ProfileSetCatalog.Create(naming, "NAME", ["alpha"]);
        check(second.Name == "NAME (2)" && ProfileSetCatalog.Rename(naming, second.Id, "Name").Name == "Name (2)" && first.Name == "Name",
            "Saved-set naming reuses case-insensitive collision handling without changing other sets");
        string longName = new('x', ProfileSetCatalog.MaximumNameLength);
        _ = ProfileSetCatalog.Create(naming, longName, ["alpha"]);
        check(ProfileSetCatalog.Create(naming, longName, ["alpha"]).Name.Length <= ProfileSetCatalog.MaximumNameLength,
            "Collision suffixes keep saved-set names within the declared length limit");
        int count = naming.ApplicationSets.Count;
        Reject(() => ProfileSetCatalog.Create(naming, "Invalid", [@"C:\Private\app.exe"]), "Saved sets reject local paths instead of persisting device-specific rules");
        Reject(() => ProfileSetCatalog.Create(naming, "Invalid", ["preset:alpha"]), "Saved members are application identities rather than nested set expressions");
        Reject(() => ProfileSetCatalog.Create(naming, "", ["alpha"]), "Saved sets require a nonempty display name");
        Reject(() => ProfileSetCatalog.Create(naming, "Too many", Enumerable.Range(0, 129).Select(i => "app-" + i)), "Member count limits are enforced before saving");
        check(naming.ApplicationSets.Count == count, "Rejected set creation leaves settings unchanged");
        var bounded = new Settings();
        for (int i = 0; i < ProfileSetCatalog.MaximumSets; i++) ProfileSetCatalog.Create(bounded, "Set", ["alpha"]);
        Reject(() => ProfileSetCatalog.Create(bounded, "Overflow", ["alpha"]), "Saved-set count is bounded independently of the built-in catalog");
        var wide = new Settings();
        _ = ProfileSetCatalog.Create(wide, "Left", Enumerable.Range(0, 80).Select(i => "left-" + i));
        _ = ProfileSetCatalog.Create(wide, "Right", Enumerable.Range(0, 80).Select(i => "right-" + i));
        var wideEntries = ProfileSetCatalog.Entries(wide, presets);
        Reject(() => ProfileSetCatalog.Evaluate(wideEntries, wideEntries.Where(entry => entry.IsCustom).Select(entry => entry.Id), ProfileSetOperation.Union),
            "An oversized union is rejected instead of silently truncating application membership");
    }
}
