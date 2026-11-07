using System.Text;

namespace FlowVPN;

internal static class BaseDomainSelectionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var defaults = BaseDomainSelection.From(new Settings());
        check(defaults.BaseCategory == "ru-blocked" && defaults.Categories.SequenceEqual(new[] { "ru-blocked" }), "Default domain selection remains the recommended blocking list");
        var legacy = new Settings { BaseDomainCategory = "youtube", BaseRulesEnabled = false };
        var migrated = BaseDomainSelection.From(legacy);
        check(legacy.BaseDomainCategory == "youtube" && migrated.BaseCategory == "none" && migrated.AdditionalCategories.SequenceEqual(new[] { "youtube" }) &&
            migrated.Categories.SequenceEqual(new[] { "youtube" }), "Legacy custom categories migrate without implicitly adding a blocking list or mutating the settings while reading");
        migrated.ApplyTo(legacy);
        check(!legacy.BaseRulesEnabled && BaseDomainSelection.From(legacy).Identity == migrated.Identity, "Explicit migration preserves the global enable switch and is idempotent");
        var combined = BaseDomainSelection.Create("ru-blocked-all", ["youtube", "github", "YouTube"]);
        check(combined.Categories.SequenceEqual(new[] { "github", "ru-blocked-all", "youtube" }) && combined.AdditionalCategories.Count == 2,
            "Effective category membership is normalized, unique and deterministic");
        var sameRules = BaseDomainSelection.Create("none", ["ru-blocked-all", "github", "youtube"]);
        check(combined.RuleIdentity == sameRules.RuleIdentity && combined.Identity != sameRules.Identity,
            "Rule cache identity depends on membership while settings identity also preserves the primary-list choice");
        check(BaseDomainSelection.Create("none").Categories.Count == 0, "An explicit empty domain selection does not remove the independently enabled GeoIP policy");
        bool tooMany = false;
        try { _ = BaseDomainSelection.Create("none", Enumerable.Range(0, 33).Select(i => "category-" + i)); }
        catch (InvalidDataException) { tooMany = true; }
        check(tooMany && BaseDomainSelection.Create("ru-blocked", Enumerable.Range(0, 32).Select(i => "category-" + i)).Categories.Count == 33,
            "Additional categories have an explicit maximum of 32 plus the optional primary list");
        foreach (string invalid in new[] { "../private", "", "https://example.org", "none" })
        {
            bool rejected = false;
            try { _ = BaseDomainSelection.Create("ru-blocked", [invalid]); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Additional category rejects unsafe or reserved identity: " + invalid);
        }

        byte[] archive = [.. Entry("one", new(DomainTermKind.Exact, "shared.example"), new(DomainTermKind.Suffix, "EXAMPLE.COM"),
                new(DomainTermKind.Keyword, "Ad"), new(DomainTermKind.Regex, "^api$")),
            .. Entry("two", new(DomainTermKind.Exact, "SHARED.EXAMPLE"), new(DomainTermKind.Suffix, "example.com"),
                new(DomainTermKind.Keyword, "AD"), new(DomainTermKind.Regex, "^api$"), new(DomainTermKind.Regex, "^API$"), new(DomainTermKind.Exact, "other.example"))];
        var union = GeoSiteReader.ReadMany(archive, ["two", "one"]);
        check(union.Categories.SequenceEqual(new[] { "one", "two" }) && union.Selected.Count == 7,
            "One GeoSite pass unions selected categories and removes typed duplicate terms");
        var prepared = DomainCoverage.ToRuleSet(union.Selected);
        var rule = prepared["rules"]![0]!;
        check(rule["domain_keyword"]!.AsArray().Count == 2 && rule["domain_regex"]!.AsArray().Count == 2 && rule["domain_suffix"]!.AsArray().Single()!.ToString() == "example.com",
            "Union preserves case-sensitive keyword and regex values while canonicalizing domain and suffix terms");
        check(GeoSiteReader.Read(archive, "one").Selected.Count == 4 && GeoSiteReader.Read(archive).Selected.Count == 0,
            "Single-category and catalogue-only GeoSite reads remain compatible");
        bool missing = false;
        try { _ = GeoSiteReader.ReadMany(archive, ["one", "missing"]); }
        catch (InvalidDataException) { missing = true; }
        check(missing, "A missing selected category rejects the whole union instead of silently applying a partial policy");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { _ = GeoSiteReader.ReadMany(archive, ["one"], cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "GeoSite union parsing honours cancellation without preparing partial rules");
        var coverage = DomainCoverage.FromRuleSet(prepared);
        check(DomainCoverage.Effective(["example.com", "safe.invalid"], coverage).SequenceEqual(new[] { "safe.invalid" }),
            "Additional profile domains are deduplicated against the full effective category union");
    }

    static byte[] Entry(string id, params DomainTerm[] terms)
    {
        var fields = new List<byte>(Field(10, Encoding.UTF8.GetBytes(id)));
        foreach (var term in terms) fields.AddRange(Field(18, [8, (byte)term.Kind, .. Field(18, Encoding.UTF8.GetBytes(term.Value))]));
        return Field(10, fields.ToArray());
    }
    static byte[] Field(byte tag, byte[] value)
    {
        var bytes = new List<byte> { tag };
        uint size = (uint)value.Length;
        do { bytes.Add((byte)((size & 0x7fu) | (size > 0x7fu ? 0x80u : 0u))); size >>= 7; } while (size != 0);
        bytes.AddRange(value);
        return bytes.ToArray();
    }
}
