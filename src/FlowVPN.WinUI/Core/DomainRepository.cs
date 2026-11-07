using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlowVPN;

public static class DomainRepository
{
    public const string Repository = "https://github.com/runetfreedom/russia-blocked-geosite";
    internal const int MaximumPreparedBytes = 32 * 1024 * 1024;
    internal const int MaximumPreparedSelections = 8;
    const long MaximumCacheBytes = 256L * 1024 * 1024;
    const int SchemaVersion = 2;
    static readonly SemaphoreSlim updateGate = new(1, 1);
    static readonly object cacheGate = new();
    static readonly LinkedList<CoverageEntry> coverageCache = new();
    static readonly Dictionary<string, DateTimeOffset> accesses = new(StringComparer.OrdinalIgnoreCase);
    sealed record CoverageEntry(string Path, DateTime Modified, long Size, DomainCoverage Coverage);
    sealed record PreparedEntry(string Path, string MetadataPath, long Size, DateTimeOffset Accessed);

    public static string CategoryId(string id) => Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9_!@.-]{0,100}\z") ? id : throw new InvalidDataException("Некорректная категория базы.");
    public static string RulePath(string category) => RulePath([category]);
    public static string RulePath(IReadOnlyCollection<string> categories) => RulePath(Store.Rules, BaseDomainSelection.CanonicalCategories(categories));
    internal static string RulePath(string directory, string[] categories) => Path.Combine(directory, categories.Length switch
    {
        0 => throw new InvalidOperationException("Не выбраны доменные категории."),
        1 => "domains-" + categories[0] + ".json",
        _ => "domain-selection-" + BaseDomainSelection.CategoryIdentity(categories) + ".json"
    });
    internal static string MetaPath(string directory, string[] categories) => Path.Combine(directory, "domain-cache-" + BaseDomainSelection.CategoryIdentity(categories) + ".json");

    public static DomainCoverage? Current(string category) => Current([category]);
    public static DomainCoverage? Current(IReadOnlyCollection<string> categories)
    {
        var canonical = BaseDomainSelection.CanonicalCategories(categories);
        return canonical.Length == 0 ? null : CurrentPath(RulePath(Store.Rules, canonical));
    }
    internal static DomainCoverage? CurrentPath(string path)
    {
        lock (cacheGate)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (file.Length > MaximumPreparedBytes) throw new InvalidDataException("Доменные правила превышают допустимый размер.");
            for (var node = coverageCache.First; node != null; node = node.Next)
                if (node.Value.Path == path && node.Value.Modified == file.LastWriteTimeUtc && node.Value.Size == file.Length)
                {
                    var hit = node.Value.Coverage;
                    coverageCache.Remove(node);
                    coverageCache.AddFirst(node);
                    RememberAccess(path);
                    return hit;
                }
            var coverage = DomainCoverage.FromRuleSet(JsonNode.Parse(ReadBounded(path, MaximumPreparedBytes)));
            RememberCoverage(path, coverage);
            return coverage;
        }
    }
    public static string Description(string category) => Description([category]);
    public static string Description(IReadOnlyCollection<string> categories)
    {
        if (categories.Count == 0) return "Доменные категории отключены";
        try
        {
            return Current(categories) is { } data ? $"Доменные правила: {data.Count:N0} · категорий: {BaseDomainSelection.CanonicalCategories(categories).Length}" : "Доменная база ещё не загружена";
        }
        catch (Exception e) when (CacheError(e)) { return "Доменная база недоступна. Обновите её."; }
    }
    public static bool Due(string category) => Due([category]);
    public static bool Due(IReadOnlyCollection<string> categories)
    {
        var canonical = BaseDomainSelection.CanonicalCategories(categories);
        if (canonical.Length == 0) return false;
        return Due(Store.Rules, canonical);
    }
    internal static bool Due(string directory, string[] categories)
    {
        if (categories.Length == 0) return false;
        try
        {
            var prepared = ReadMetadata(MetaPath(directory, categories));
            var archive = ReadMetadata(Path.Combine(directory, "geosite-metadata.json"));
            string sourceHash = archive?["sha256"]?.GetValue<string>() ?? "";
            // Freshness belongs to the checked source archive. A reusable prepared selection
            // can be much older and must not be rewritten just to advance its timestamp.
            if (!File.Exists(RulePath(directory, categories)) || !File.Exists(Path.Combine(directory, "geosite.dat"))
                || !MatchesSelection(prepared, categories) || !Regex.IsMatch(sourceHash, @"\A[a-fA-F0-9]{64}\z")
                || !sourceHash.Equals(prepared?["sourceSha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)
                || !DateTimeOffset.TryParse(archive?["fetched"]?.ToString(), out var at)) return true;
            return DateTimeOffset.UtcNow - at >= TimeSpan.FromHours(6);
        }
        catch (Exception e) when (CacheError(e)) { return true; }
    }
    public static Task<bool> Update(HttpClient client, string category, CancellationToken token = default, IProgress<TransferProgress>? progress = null,
        IReadOnlyCollection<string>? protectedCategories = null) => Update(client, [category], token, progress, protectedCategories);
    public static async Task<bool> Update(HttpClient client, IReadOnlyCollection<string> categories, CancellationToken token = default, IProgress<TransferProgress>? progress = null,
        IReadOnlyCollection<string>? protectedCategories = null)
    {
        var canonical = BaseDomainSelection.CanonicalCategories(categories);
        if (canonical.Length == 0) return false;
        var preserve = BaseDomainSelection.CanonicalCategories(protectedCategories ?? []);
        await updateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await GeoSiteArchive.Update(client, false, token, progress).ConfigureAwait(false);
            return await Prepare(canonical, GeoSiteArchive.DataPath, Store.Rules, Store.Run,
                async (input, output, cancellation) => { await CoreCli.Run(cancellation, "rule-set", "compile", input, "-o", output).ConfigureAwait(false); }, token, progress,
                preserve.Length == 0 ? null : RulePath(Store.Rules, preserve)).ConfigureAwait(false);
        }
        finally { updateGate.Release(); }
    }

    // Explicit paths and compiler allow integrity/failure tests without a VPN or user data.
    internal static async Task<bool> Prepare(IReadOnlyCollection<string> categories, string archivePath, string rulesDirectory, string scratchDirectory,
        Func<string, string, CancellationToken, Task> compile, CancellationToken token = default, IProgress<TransferProgress>? progress = null, string? protectedPath = null)
    {
        string[] canonical = BaseDomainSelection.CanonicalCategories(categories);
        if (canonical.Length == 0) return false;
        string path = RulePath(rulesDirectory, canonical), metadataPath = MetaPath(rulesDirectory, canonical);
        progress?.Report(new("Доменные правила", TransferStage.Verifying));
        // The handle prevents replacement between hashing and parsing; hits do not allocate the archive byte array.
        await using var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length is 0 or > 128 * 1024 * 1024) throw new InvalidDataException("Некорректный размер каталога GeoSite.");
        string sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        if (await ValidPrepared(path, metadataPath, canonical, sourceHash, token).ConfigureAwait(false))
        {
            lock (cacheGate) RememberAccess(path);
            return false;
        }
        lock (cacheGate) ForgetCoverage(path);
        source.Position = 0;
        byte[] archiveBytes = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(archiveBytes, token).ConfigureAwait(false);
        var prepared = await Task.Run(() =>
        {
            var archive = GeoSiteReader.ReadMany(archiveBytes, canonical, token);
            token.ThrowIfCancellationRequested();
            var json = DomainCoverage.ToRuleSet(archive.Selected);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(json, Store.JsonOptions);
            if (bytes.Length > MaximumPreparedBytes) throw new InvalidDataException("Выбранные категории содержат слишком много доменных правил. Уменьшите их количество.");
            return (Json: bytes, Coverage: DomainCoverage.FromRuleSet(json, token));
        }, token).ConfigureAwait(false);
        string candidate = Path.Combine(scratchDirectory, "domains-" + Guid.NewGuid().ToString("N") + ".json"), binary = candidate + ".srs";
        string hash = Convert.ToHexString(SHA256.HashData(prepared.Json));
        bool changed = !await MatchesFile(path, prepared.Json.Length, hash, token).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(candidate, prepared.Json, token).ConfigureAwait(false);
            await compile(candidate, binary, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Доменные правила", TransferStage.Applying));
            Prune(rulesDirectory, path, prepared.Json.Length, protectedPath);
            if (changed) Store.Atomic(path, prepared.Json);
            Store.WriteJson(metadataPath, new JsonObject
            {
                ["schemaVersion"] = SchemaVersion, ["repository"] = Repository,
                ["categories"] = new JsonArray(canonical.Select(value => JsonValue.Create(value)).ToArray()),
                ["sourceSha256"] = sourceHash, ["sha256"] = hash, ["size"] = prepared.Json.Length,
                ["fetched"] = DateTimeOffset.UtcNow.ToString("O"), ["rules"] = prepared.Coverage.Count
            });
            lock (cacheGate) RememberCoverage(path, prepared.Coverage);
            return changed;
        }
        finally { File.Delete(candidate); File.Delete(binary); }
    }

    static async Task<bool> ValidPrepared(string path, string metadataPath, string[] categories, string sourceHash, CancellationToken token)
    {
        try
        {
            var metadata = ReadMetadata(metadataPath);
            return MatchesSelection(metadata, categories) && metadata!["sourceSha256"]?.GetValue<string>() == sourceHash
                && metadata["size"] is JsonValue size && size.TryGetValue<long>(out long length)
                && await MatchesFile(path, length, metadata["sha256"]?.GetValue<string>() ?? "", token).ConfigureAwait(false);
        }
        catch (Exception error) when (CacheError(error)) { return false; }
    }
    static bool MatchesSelection(JsonObject? metadata, string[] categories) => metadata?["schemaVersion"]?.GetValue<int>() == SchemaVersion
        && metadata["categories"] is JsonArray entries && entries.Count == categories.Length
        && entries.Select(entry => entry?.GetValue<string>()).SequenceEqual(categories, StringComparer.Ordinal);
    static async Task<bool> MatchesFile(string path, long expectedSize, string expectedHash, CancellationToken token)
    {
        if (expectedSize is <= 0 or > MaximumPreparedBytes || expectedHash.Length != 64) return false;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return file.Length == expectedSize && Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)) == expectedHash;
        }
        catch (IOException) { return false; }
    }
    static JsonObject? ReadMetadata(string path) => JsonNode.Parse(ReadBounded(path, 16384), documentOptions: new JsonDocumentOptions { MaxDepth = 8 }) as JsonObject;
    static byte[] ReadBounded(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Файл доменных правил превышает допустимый размер.");
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }
    static bool CacheError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException;
    static void RememberAccess(string path)
    {
        accesses[path] = DateTimeOffset.UtcNow;
        if (accesses.Count > MaximumPreparedSelections * 2)
            accesses.Remove(accesses.MinBy(item => item.Value).Key);
    }
    static void ForgetCoverage(string path)
    {
        for (var node = coverageCache.First; node != null;)
        {
            var next = node.Next;
            if (node.Value.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) coverageCache.Remove(node);
            node = next;
        }
    }
    static void RememberCoverage(string path, DomainCoverage coverage)
    {
        ForgetCoverage(path);
        var file = new FileInfo(path);
        coverageCache.AddFirst(new CoverageEntry(path, file.LastWriteTimeUtc, file.Length, coverage));
        while (coverageCache.Count > 2) coverageCache.RemoveLast();
        RememberAccess(path);
    }
    static void Prune(string directory, string keep, long incomingBytes, string? protectedPath)
    {
        // Only exact v2 metadata/payload pairs are eligible. Never recurse or follow reparse points.
        if (!LocalPathPolicy.TryDirectory(directory, out _, allowLocalLinks: false))
            throw new IOException("Каталог кэша доменных правил недоступен или является ссылкой.");
        var entries = new List<PreparedEntry>();
        foreach (string metadataPath in Directory.EnumerateFiles(directory, "domain-cache-*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if ((File.GetAttributes(metadataPath) & FileAttributes.ReparsePoint) != 0) continue;
                var metadata = ReadMetadata(metadataPath);
                if (metadata?["categories"] is not JsonArray values) continue;
                string[] categories = BaseDomainSelection.CanonicalCategories(values.Select(value => value?.GetValue<string>() ?? throw new InvalidDataException("Некорректная категория кэша.")));
                if (categories.Length == 0 || !MatchesSelection(metadata, categories) || metadataPath != MetaPath(directory, categories)) continue;
                string path = RulePath(directory, categories);
                if (path.Equals(keep, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var file = new FileInfo(path);
                DateTimeOffset.TryParse(metadata["fetched"]?.ToString(), out var accessed);
                lock (cacheGate) if (accesses.TryGetValue(path, out var recent)) accessed = recent;
                entries.Add(new(path, metadataPath, file.Length, accessed));
            }
            catch (Exception error) when (CacheError(error)) { }
        }
        long bytes = entries.Sum(entry => entry.Size) + incomingBytes;
        int count = entries.Count + 1;
        foreach (var entry in entries.OrderBy(entry => entry.Accessed))
        {
            if (count <= MaximumPreparedSelections && bytes <= MaximumCacheBytes) break;
            if (entry.Path.Equals(protectedPath, StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(entry.Path);
            File.Delete(entry.MetadataPath);
            bytes -= entry.Size;
            count--;
            lock (cacheGate) { ForgetCoverage(entry.Path); accesses.Remove(entry.Path); }
        }
        if (count > MaximumPreparedSelections || bytes > MaximumCacheBytes)
            throw new IOException("Не удалось освободить место в кэше доменных правил.");
    }
}
