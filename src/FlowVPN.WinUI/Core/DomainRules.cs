using System.Globalization;
using System.Text.Json.Nodes;

namespace FlowVPN;

// The editor and config generator share exactly the same suffix semantics, including label boundaries.
public sealed class DomainCoverage
{
    readonly HashSet<string> suffixes;
    readonly HashSet<string> exact = new(StringComparer.OrdinalIgnoreCase);
    readonly string[] keywords = [];
    readonly string[] expressions = [];
    public int Count => suffixes.Count + exact.Count + keywords.Length + expressions.Length;
    public DomainCoverage(IEnumerable<string> domains) => suffixes = domains.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public DomainCoverage(IReadOnlyList<DomainTerm> terms)
    {
        suffixes = terms.Where(t => t.Kind == DomainTermKind.Suffix).Select(t => Normalize(t.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        exact = terms.Where(t => t.Kind == DomainTermKind.Exact).Select(t => Normalize(t.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        keywords = terms.Where(t => t.Kind == DomainTermKind.Keyword).Select(t => t.Value).Distinct(StringComparer.Ordinal).ToArray();
        expressions = terms.Where(t => t.Kind == DomainTermKind.Regex).Select(t => t.Value).Distinct(StringComparer.Ordinal).ToArray();
    }
    public static string Normalize(string domain)
    {
        string value = domain.Trim().TrimEnd('.');
        foreach (char c in value)
            if (!char.IsAscii(c))
            {
                value = new IdnMapping().GetAscii(value);
                break;
            }
        if (value.Length is 0 or > 253)
            throw new InvalidDataException("Некорректный домен: " + domain);
        int labelStart = 0;
        for (int i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] != '.')
            {
                if (!char.IsAsciiLetterOrDigit(value[i]) && value[i] is not '-' and not '_')
                    throw new InvalidDataException("Некорректный домен: " + domain);
                continue;
            }
            if (i - labelStart is 0 or > 63 || value[labelStart] == '-' || value[i - 1] == '-')
                throw new InvalidDataException("Некорректный домен: " + domain);
            labelStart = i + 1;
        }
        return value.ToLowerInvariant();
    }
    public string? Match(string domain)
    {
        return MatchNormalized(Normalize(domain));
    }
    string? MatchNormalized(string suffix)
    {
        foreach (string keyword in keywords)
            if (suffix.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return "keyword:" + keyword;
        return FindSuffix(suffix, suffixes);
    }
    static string? FindSuffix(ReadOnlySpan<char> suffix, HashSet<string> set)
    {
        var lookup = set.GetAlternateLookup<ReadOnlySpan<char>>();
        while (true)
        {
            if (lookup.TryGetValue(suffix, out var found))
                return found;
            int dot = suffix.IndexOf('.');
            if (dot < 0)
                return null;
            suffix = suffix[(dot + 1)..];
        }
    }
    public static List<string> Effective(IEnumerable<string> domains, DomainCoverage? activeBase)
    {
        var normalized = domains.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string domain in normalized)
        {
            if (activeBase?.MatchNormalized(domain) != null)
                continue;
            int dot = domain.IndexOf('.');
            if (dot < 0 || FindSuffix(domain.AsSpan(dot + 1), normalized) == null)
                result.Add(domain);
        }
        return result;
    }
    public static JsonObject ToRuleSet(IReadOnlyList<DomainTerm> terms)
    {
        var suffixes = Effective(terms.Where(t => t.Kind == DomainTermKind.Suffix).Select(t => t.Value), null);
        var suffixCoverage = new DomainCoverage(suffixes);
        var rule = new JsonObject();
        void Add(string key, IEnumerable<string> values)
        {
            var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length > 0)
                rule[key] = new JsonArray(distinct.Select(v => JsonValue.Create(v)).ToArray());
        }
        Add("domain_suffix", suffixes);
        Add("domain", terms.Where(t => t.Kind == DomainTermKind.Exact).Select(t => Normalize(t.Value)).Where(d => suffixCoverage.Match(d) == null));
        Add("domain_keyword", terms.Where(t => t.Kind == DomainTermKind.Keyword).Select(t => t.Value));
        Add("domain_regex", terms.Where(t => t.Kind == DomainTermKind.Regex).Select(t => t.Value));
        if (rule.Count == 0)
            throw new InvalidDataException("Категория не содержит доменных правил.");
        return new JsonObject { ["version"] = 3, ["rules"] = new JsonArray(rule) };
    }
    public static DomainCoverage FromRuleSet(JsonNode? json)
    {
        var terms = new List<DomainTerm>();
        if (json?["rules"] is not JsonArray rules)
            throw new InvalidDataException("Некорректная доменная база.");
        foreach (var rule in rules.OfType<JsonObject>())
        {
            foreach (var pair in new[] { ("domain_suffix", DomainTermKind.Suffix), ("domain", DomainTermKind.Exact), ("domain_keyword", DomainTermKind.Keyword), ("domain_regex", DomainTermKind.Regex) })
                if (rule[pair.Item1] is JsonArray values)
                    foreach (var value in values)
                        terms.Add(new(pair.Item2, value!.GetValue<string>()));
        }
        if (terms.Count == 0)
            throw new InvalidDataException("Пустая доменная база.");
        return new DomainCoverage(terms);
    }
}
