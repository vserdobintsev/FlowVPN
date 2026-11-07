namespace FlowVPN;

// The primary blocking list and optional GeoSite categories are presentation choices over one rule union.
public sealed class BaseDomainSelection
{
    public const int MaxAdditionalCategories = 32;
    public string BaseCategory { get; }
    public IReadOnlyList<string> AdditionalCategories { get; }
    public IReadOnlyList<string> Categories { get; }
    public string Identity { get; }
    public string RuleIdentity { get; }

    BaseDomainSelection(string category, string[] additional)
    {
        BaseCategory = category;
        AdditionalCategories = Array.AsReadOnly(additional);
        Categories = Array.AsReadOnly(CanonicalCategories(category == "none" ? additional : additional.Prepend(category)));
        Identity = Store.Hash(category + "\n" + string.Join("\n", additional));
        RuleIdentity = CategoryIdentity(Categories);
    }

    public static BaseDomainSelection From(Settings settings) => Create(settings.BaseDomainCategory,
        settings.AdditionalDomainCategories ?? throw new InvalidDataException("Некорректный список дополнительных категорий."));

    public static BaseDomainSelection Create(string baseCategory, IEnumerable<string>? additional = null)
    {
        string category = NormalizeId(baseCategory);
        var extras = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (string value in additional ?? [])
        {
            if (++count > MaxAdditionalCategories) throw new InvalidDataException($"Можно выбрать не больше {MaxAdditionalCategories} дополнительных категорий.");
            string id = NormalizeId(value);
            if (id == "none") throw new InvalidDataException("Пустая база не является дополнительной категорией.");
            extras.Add(id);
        }
        // A legacy custom category represented the entire domain policy. Never add ru-blocked during migration.
        if (category is not ("ru-blocked" or "ru-blocked-all" or "none"))
        {
            extras.Add(category);
            category = "none";
        }
        if (extras.Count > MaxAdditionalCategories) throw new InvalidDataException($"Можно выбрать не больше {MaxAdditionalCategories} дополнительных категорий.");
        return new(category, extras.Order(StringComparer.Ordinal).ToArray());
    }

    public void ApplyTo(Settings settings)
    {
        settings.BaseDomainCategory = BaseCategory;
        settings.AdditionalDomainCategories = AdditionalCategories.ToList();
    }

    public static string[] CanonicalCategories(IEnumerable<string> categories)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (string value in categories)
        {
            if (++count > MaxAdditionalCategories + 1) throw new InvalidDataException("Слишком много доменных категорий.");
            string category = NormalizeId(value);
            if (category == "none") throw new InvalidDataException("Пустая база не является категорией GeoSite.");
            result.Add(category);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    public static string CategoryIdentity(IEnumerable<string> categories) => Store.Hash(string.Join("\n", CanonicalCategories(categories)));
    static string NormalizeId(string value) => DomainRepository.CategoryId(
        !string.IsNullOrWhiteSpace(value) ? value.Trim().ToLowerInvariant() : throw new InvalidDataException("Не указана категория базы."));
}
