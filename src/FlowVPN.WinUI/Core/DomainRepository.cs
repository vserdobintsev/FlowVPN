using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlowVPN;

public static class DomainRepository
{
    public const string Repository = "https://github.com/runetfreedom/russia-blocked-geosite";
    public static string RulePath(string category) => Path.Combine(Store.Rules, "domains-" + CategoryId(category) + ".json");
    static string MetaPath(string category) => Path.Combine(Store.Rules, "domains-" + CategoryId(category) + "-metadata.json");
    public static string CategoryId(string id) => Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9_!@.-]{0,100}\z") ? id : throw new InvalidDataException("Некорректная категория базы.");
    static readonly object cacheGate = new();
    static (string Path, DateTime Modified, long Size, DomainCoverage Coverage)? cache;
    public static DomainCoverage? Current(string category)
    {
        lock (cacheGate)
        {
            var file = new FileInfo(RulePath(category));
            if (!file.Exists)
                return null;
            if (cache is { } hit && hit.Path == RulePath(category) && hit.Modified == file.LastWriteTimeUtc && hit.Size == file.Length)
                return hit.Coverage;
            var json = JsonNode.Parse(File.ReadAllText(RulePath(category)));
            var coverage = DomainCoverage.FromRuleSet(json);
            cache = (RulePath(category), file.LastWriteTimeUtc, file.Length, coverage);
            return coverage;
        }
    }
    public static string Description(string category)
    {
        try
        {
            return Current(category) is { } data ? $"Доменные правила: {data.Count:N0} · категория {category}" : "Доменная база ещё не загружена";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException) { return "Доменная база недоступна. Обновите её."; }
    }
    public static bool Due(string category)
    {
        try
        {
            var meta = JsonNode.Parse(File.ReadAllText(MetaPath(category)));
            return !File.Exists(RulePath(category)) || !DateTimeOffset.TryParse(meta?["fetched"]?.ToString(), out var at) || DateTimeOffset.UtcNow - at > TimeSpan.FromHours(6);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return true; }
    }
    public static async Task<bool> Update(HttpClient client, string category)
    {
        category = CategoryId(category);
        await GeoSiteArchive.Update(client, false);
        var prepared = await Task.Run(() =>
        {
            var archive = GeoSiteReader.Read(File.ReadAllBytes(GeoSiteArchive.DataPath), category);
            var json = DomainCoverage.ToRuleSet(archive.Selected);
            return (Json: Encoding.UTF8.GetBytes(json.ToJsonString(Store.JsonOptions)), Coverage: DomainCoverage.FromRuleSet(json));
        });
        string candidate = Path.Combine(Store.Run, "domains-" + Guid.NewGuid().ToString("N") + ".json"), binary = candidate + ".srs";
        bool changed = !File.Exists(RulePath(category)) || !File.ReadAllBytes(RulePath(category)).SequenceEqual(prepared.Json);
        try
        {
            await File.WriteAllBytesAsync(candidate, prepared.Json);
            await CoreCli.Run("rule-set", "compile", candidate, "-o", binary);
            if (changed)
                Store.Atomic(RulePath(category), prepared.Json);
            var file = new FileInfo(RulePath(category));
            lock (cacheGate)
                cache = (RulePath(category), file.LastWriteTimeUtc, file.Length, prepared.Coverage);
            Store.WriteJson(MetaPath(category), new JsonObject
            {
                ["repository"] = Repository,
                ["category"] = category,
                ["fetched"] = DateTimeOffset.UtcNow.ToString("O"),
                ["rules"] = prepared.Coverage.Count,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(prepared.Json))
            });
            return changed;
        }
        finally { File.Delete(candidate); File.Delete(binary); }
    }
}
