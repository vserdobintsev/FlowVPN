namespace FlowVPN;

internal enum ProfileSetOperation { Union, Intersection, Difference, SymmetricDifference }

internal sealed record ProfileSetEntry(string Id, string Name, string Description, string Icon,
    IReadOnlyList<string> PresetIds, int AvailableCount, string CustomSetId = "")
{
    public int MissingCount => PresetIds.Count - AvailableCount;
    public bool IsCustom => CustomSetId.Length > 0;
}

internal sealed record ProfileSetMember(string Id, string Name, string Icon, string Executable, bool Available);

// Saved sets contain application identities only. Machine-specific routing rules are resolved later.
internal static class ProfileSetCatalog
{
    public const int MaximumSets = 128;
    public const int MaximumMembers = 128;
    public const int MaximumNameLength = 180;

    public static IReadOnlyList<ProfileSetEntry> Entries(Settings settings, IReadOnlyCollection<DetectedPreset> presets)
    {
        var map = Catalog(presets);
        var available = map.Values.Where(Eligible).Select(p => p.Definition.Id).ToHashSet(StringComparer.Ordinal);
        var entries = new List<ProfileSetEntry>();
        var recommended = map.Values.Where(p => p.Definition.ParentId.Length == 0 && p.Definition.RecommendedProfile && available.Contains(p.Definition.Id))
            .OrderBy(p => p.Definition.Id, StringComparer.Ordinal).ToArray();
        if (recommended.Length >= 2)
        {
            var ids = recommended.Select(p => p.Definition.Id).ToArray();
            entries.Add(new("recommended:" + string.Join("|", ids), "Рекомендуемые приложения",
                string.Join(" + ", recommended.Select(p => p.Definition.Name)), "star", Array.AsReadOnly(ids), ids.Length));
        }
        if (settings.ApplicationSets.Count > MaximumSets)
            throw new InvalidDataException("Можно сохранить не больше 128 наборов приложений.");
        var customIds = new HashSet<Guid>();
        foreach (var set in settings.ApplicationSets)
        {
            if (!Guid.TryParse(set.Id, out var id) || !customIds.Add(id))
                throw new InvalidDataException("Сохранённый набор имеет некорректный или повторяющийся идентификатор.");
            var members = NormalizeMembers(set.PresetIds, allowEmpty: false);
            // Content-versioned IDs prevent a stale dialog from silently selecting a changed set.
            entries.Add(new("set:" + id.ToString("N") + ":" + Store.Hash(string.Join("\n", members)),
                ValidateName(set.Name), "Сохранённый состав приложений. Профиль доступен, когда найдены все его приложения.",
                ValidateIcon(set.Icon), members, members.Count(available.Contains), set.Id));
        }
        foreach (var preset in map.Values.OrderBy(p => p.Definition.Id, StringComparer.Ordinal))
        {
            var definition = preset.Definition;
            entries.Add(new("preset:" + definition.Id, QualifiedName(preset, map), definition.Description, definition.ProfileIcon,
                Array.AsReadOnly(new[] { definition.Id }), available.Contains(definition.Id) ? 1 : 0));
        }
        return entries.AsReadOnly();
    }

    public static IReadOnlyList<ProfileSetMember> Members(IEnumerable<string> presetIds, IReadOnlyCollection<DetectedPreset> presets)
    {
        var map = Catalog(presets);
        return NormalizeMembers(presetIds, allowEmpty: true).Select(id => map.TryGetValue(id, out var preset)
            ? new ProfileSetMember(id, QualifiedName(preset, map), preset.Definition.ProfileIcon,
                preset.Components.FirstOrDefault(c => c.Available && c.Suggested && !string.IsNullOrWhiteSpace(c.Rule.Pattern))?.Executable ?? "", Eligible(preset))
            : new ProfileSetMember(id, id, "network", "", false)).ToList().AsReadOnly();
    }

    public static IReadOnlyList<string> Evaluate(IReadOnlyCollection<ProfileSetEntry> entries, IEnumerable<string> selectedEntryIds,
        ProfileSetOperation operation, string? baseSetId = null)
    {
        string[] selected = selectedEntryIds.Take(MaximumSets + MaximumMembers + 2).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length < 2 || selected.Length > MaximumSets + MaximumMembers + 1)
            throw new InvalidDataException("Выберите не меньше двух различных наборов.");
        if (!Enum.IsDefined(operation))
            throw new InvalidDataException("Выберите допустимую операцию над наборами.");
        var catalog = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        if (selected.Any(id => !catalog.ContainsKey(id)))
            throw new InvalidDataException("Один из выбранных наборов изменился или удалён. Обновите выбор.");
        if (operation == ProfileSetOperation.Difference && (baseSetId == null || !selected.Contains(baseSetId, StringComparer.Ordinal)))
            throw new InvalidDataException("Выберите исходный набор, из которого нужно вычесть остальные.");
        string first = operation == ProfileSetOperation.Difference ? baseSetId! : selected[0];
        var result = NormalizeMembers(catalog[first].PresetIds, allowEmpty: true).ToHashSet(StringComparer.Ordinal);
        foreach (string id in selected.Where(id => id != first))
        {
            var operand = NormalizeMembers(catalog[id].PresetIds, allowEmpty: true);
            switch (operation)
            {
                case ProfileSetOperation.Union: result.UnionWith(operand); break;
                case ProfileSetOperation.Intersection: result.IntersectWith(operand); break;
                case ProfileSetOperation.Difference: result.ExceptWith(operand); break;
                case ProfileSetOperation.SymmetricDifference: result.SymmetricExceptWith(operand); break;
            }
        }
        return NormalizeMembers(result, allowEmpty: true);
    }

    public static ApplicationSet Create(Settings settings, string name, IEnumerable<string> presetIds, string icon = "network")
    {
        if (settings.ApplicationSets.Count >= MaximumSets)
            throw new InvalidDataException("Можно сохранить не больше 128 наборов приложений.");
        var members = NormalizeMembers(presetIds, allowEmpty: false);
        string requestedName = ValidateName(name), requestedIcon = ValidateIcon(icon);
        var names = settings.ApplicationSets.Select(set => set.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new ApplicationSet { Name = UniqueNames.Reserve(requestedName, names, MaximumNameLength), Icon = requestedIcon, PresetIds = members.ToList() };
        settings.ApplicationSets.Add(created);
        return created;
    }

    public static ApplicationSet Rename(Settings settings, string customSetId, string name)
    {
        string requestedName = ValidateName(name);
        var set = Find(settings, customSetId) ?? throw new InvalidDataException("Сохранённый набор уже удалён.");
        var names = settings.ApplicationSets.Where(item => !ReferenceEquals(item, set)).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        set.Name = UniqueNames.Reserve(requestedName, names, MaximumNameLength);
        return set;
    }

    public static bool Delete(Settings settings, string customSetId)
    {
        var set = Find(settings, customSetId);
        return set != null && settings.ApplicationSets.Remove(set);
    }

    static ApplicationSet? Find(Settings settings, string id) => Guid.TryParse(id, out var value)
        ? settings.ApplicationSets.FirstOrDefault(set => Guid.TryParse(set.Id, out var candidate) && candidate == value) : null;

    static Dictionary<string, DetectedPreset> Catalog(IReadOnlyCollection<DetectedPreset> presets)
    {
        var map = new Dictionary<string, DetectedPreset>(StringComparer.Ordinal);
        foreach (var preset in presets)
        {
            string id = preset.Definition.Id;
            if (!ValidId(id)) throw new InvalidDataException("Каталог содержит некорректный идентификатор приложения.");
            map.TryAdd(id, preset);
            if (map.Count > MaximumMembers)
                throw new InvalidDataException("Каталог превышает допустимые 128 приложений.");
        }
        return map;
    }

    static bool Eligible(DetectedPreset preset) => preset.Components.Any(component =>
        component.Available && component.Suggested && !string.IsNullOrWhiteSpace(component.Rule.Pattern));

    static string QualifiedName(DetectedPreset preset, IReadOnlyDictionary<string, DetectedPreset> map) =>
        preset.Definition.ParentId.Length == 0 ? preset.Definition.Name : preset.Definition.Name + " · " +
            (map.TryGetValue(preset.Definition.ParentId, out var parent) ? parent.Definition.Name : preset.Definition.ParentId);

    static IReadOnlyList<string> NormalizeMembers(IEnumerable<string> values, bool allowEmpty)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        int examined = 0;
        foreach (string value in values)
        {
            // Persisted input cannot force unbounded duplicate enumeration either.
            if (++examined > MaximumMembers * MaximumMembers || !ValidId(value))
                throw new InvalidDataException("Набор должен содержать только идентификаторы приложений из каталога.");
            members.Add(value);
            if (members.Count > MaximumMembers)
                throw new InvalidDataException("В одном наборе допускается не больше 128 приложений.");
        }
        if (!allowEmpty && members.Count == 0)
            throw new InvalidDataException("Пустой результат нельзя сохранить как набор приложений.");
        return Array.AsReadOnly(members.Order(StringComparer.Ordinal).ToArray());
    }

    static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    static string ValidateName(string name)
    {
        string value = name?.Trim() ?? "";
        return value.Length is > 0 and <= MaximumNameLength && !value.Any(char.IsControl) ? value :
            throw new InvalidDataException("Укажите название набора длиной от 1 до 180 символов.");
    }
    static string ValidateIcon(string icon) => ValidId(icon) && icon.Length <= 64 ? icon :
        throw new InvalidDataException("Выберите значок из каталога приложения.");
}
