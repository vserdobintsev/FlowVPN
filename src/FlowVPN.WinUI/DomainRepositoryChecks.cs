using System.Text;
using System.Text.Json.Nodes;

namespace FlowVPN;

internal static class DomainRepositoryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var fixtureDirectory = Directory.CreateTempSubdirectory("FlowVPN-domain-cache-");
        string fixture = fixtureDirectory.FullName;
        string rules = Path.Combine(fixture, "rules"), run = Path.Combine(fixture, "run"), archive = Path.Combine(fixture, "geosite.dat");
        int compilations = 0;
        Task Compile(string input, string output, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            check(DomainCoverage.FromRuleSet(JsonNode.Parse(File.ReadAllBytes(input))).Count > 0, "Prepared cache validation receives a real, nonempty rule set");
            compilations++;
            return File.WriteAllTextAsync(output, "synthetic compiled fixture", token);
        }
        Task<bool> Prepare(string[] categories, CancellationToken token = default) => DomainRepository.Prepare(categories, archive, rules, run, Compile, token);
        string PathFor(params string[] categories) => DomainRepository.RulePath(rules, BaseDomainSelection.CanonicalCategories(categories));
        async Task Reject(Func<Task> action, string label)
        {
            bool rejected = false;
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or OperationCanceledException or IOException) { rejected = true; }
            check(rejected, label);
        }
        try
        {
            Directory.CreateDirectory(rules);
            Directory.CreateDirectory(run);
            byte[] original = Fixture(("ru-blocked", "alpha.example"), ("youtube", "video.example"), ("discord", "chat.example"));
            await File.WriteAllBytesAsync(archive, original);
            check(await Prepare(["ru-blocked"]), "First category preparation produces a usable cache");
            string a = PathFor("ru-blocked"), metaA = DomainRepository.MetaPath(rules, ["ru-blocked"]);
            var coverageA = DomainRepository.CurrentPath(a);
            byte[] savedA = File.ReadAllBytes(a), savedMetadataA = File.ReadAllBytes(metaA);
            DateTime at = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(a, at);
            File.SetLastWriteTimeUtc(metaA, at);
            coverageA = DomainRepository.CurrentPath(a);
            check(await Prepare(["youtube"]), "A second category prepares independently of the first");
            check(!await Prepare(["ru-blocked"]) && compilations == 2, "A to B to A reuses verified prepared rules without compiling again");
            check(File.GetLastWriteTimeUtc(a) == at && File.GetLastWriteTimeUtc(metaA) == at
                && File.ReadAllBytes(a).SequenceEqual(savedA) && File.ReadAllBytes(metaA).SequenceEqual(savedMetadataA), "Returning to a cached category does not rewrite payload or metadata");
            check(ReferenceEquals(coverageA, DomainRepository.CurrentPath(a)), "The two-entry coverage cache avoids parsing A again after B");

            var oldPrepared = JsonNode.Parse(File.ReadAllBytes(metaA))!.AsObject();
            oldPrepared["fetched"] = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
            Store.WriteJson(metaA, oldPrepared);
            byte[] oldPreparedBytes = File.ReadAllBytes(metaA);
            DateTime oldPreparedStamp = File.GetLastWriteTimeUtc(metaA);
            string archiveMetadataPath = Path.Combine(rules, "geosite-metadata.json");
            var archiveMetadata = new JsonObject
            {
                ["sha256"] = oldPrepared["sourceSha256"]!.GetValue<string>(),
                ["fetched"] = DateTimeOffset.UtcNow.ToString("O")
            };
            await File.WriteAllBytesAsync(Path.Combine(rules, "geosite.dat"), original);
            Store.WriteJson(archiveMetadataPath, archiveMetadata);
            check(!DomainRepository.Due(rules, ["ru-blocked"]), "An old prepared selection stays current when its matching source archive was recently checked");
            check(!await Prepare(["ru-blocked"]) && !DomainRepository.Due(rules, ["ru-blocked"])
                && File.GetLastWriteTimeUtc(metaA) == oldPreparedStamp && File.ReadAllBytes(metaA).SequenceEqual(oldPreparedBytes),
                "Repeated freshness checks and cache hits do not rewrite old prepared metadata");
            archiveMetadata["fetched"] = DateTimeOffset.UtcNow.AddHours(-7).ToString("O");
            Store.WriteJson(archiveMetadataPath, archiveMetadata);
            check(DomainRepository.Due(rules, ["ru-blocked"]), "A stale source archive makes the prepared selection due for refresh");
            archiveMetadata["fetched"] = DateTimeOffset.UtcNow.ToString("O");
            archiveMetadata["sha256"] = new string('0', 64);
            Store.WriteJson(archiveMetadataPath, archiveMetadata);
            check(DomainRepository.Due(rules, ["ru-blocked"]), "A changed source hash invalidates freshness even when the archive was just checked");
            archiveMetadata["sha256"] = oldPrepared["sourceSha256"]!.GetValue<string>();
            archiveMetadata["fetched"] = "invalid-date";
            Store.WriteJson(archiveMetadataPath, archiveMetadata);
            check(DomainRepository.Due(rules, ["ru-blocked"]), "Invalid source freshness metadata requests a refresh instead of hiding stale data");

            check(await Prepare(["youtube", "ru-blocked"]), "Multiple categories prepare one union");
            string union = PathFor("ru-blocked", "youtube");
            check(DomainRepository.CurrentPath(union) is { Count: 2 } merged && merged.Match("alpha.example") != null && merged.Match("video.example") != null,
                "Prepared union contains both selected categories");
            int before = compilations;
            check(!await Prepare(["ru-blocked", "youtube", "ru-blocked"]) && compilations == before
                && union == PathFor("youtube", "ru-blocked"), "Equivalent category order and duplicates share one prepared cache");
            await Prepare(["discord"]);
            before = compilations;
            check(!await Prepare(["youtube", "ru-blocked"]) && compilations == before, "Composite selection A to B to A also avoids compilation");

            byte[] corrupt = (byte[])savedA.Clone();
            corrupt[0] = (byte)'!';
            await File.WriteAllBytesAsync(a, corrupt);
            File.SetLastWriteTimeUtc(a, at);
            before = compilations;
            check(await Prepare(["ru-blocked"]) && compilations == before + 1 && File.ReadAllBytes(a).SequenceEqual(savedA),
                "Same-size payload corruption with preserved mtime is hash-detected and repaired");
            check(DomainRepository.CurrentPath(a)?.Match("alpha.example") != null, "Repair replaces any stale in-memory coverage");
            await File.WriteAllTextAsync(metaA, new string(' ', 16385));
            before = compilations;
            check(!await Prepare(["ru-blocked"]) && compilations == before + 1, "Oversized metadata is repaired while unchanged valid rules are retained");
            var malformedMetadata = JsonNode.Parse(File.ReadAllBytes(metaA))!.AsObject();
            malformedMetadata["schemaVersion"] = long.MaxValue;
            await File.WriteAllTextAsync(metaA, malformedMetadata.ToJsonString());
            before = compilations;
            check(!await Prepare(["ru-blocked"]) && compilations == before + 1, "An out-of-range metadata version is a cache miss rather than a blocked update");

            await File.WriteAllBytesAsync(archive, Fixture(("ru-blocked", "changed.example"), ("youtube", "video.example"), ("discord", "chat.example")));
            before = compilations;
            check(await Prepare(["ru-blocked"]) && compilations == before + 1 && DomainRepository.CurrentPath(a)?.Match("changed.example") != null,
                "Changed archive identity invalidates an otherwise valid prepared category");
            await Prepare(["youtube"]);
            check(compilations == before + 2, "An unchanged category payload is revalidated after its source archive changes");

            byte[] committed = File.ReadAllBytes(a), committedMeta = File.ReadAllBytes(metaA);
            await File.WriteAllBytesAsync(archive, original);
            await Reject(() => DomainRepository.Prepare(["ru-blocked"], archive, rules, run,
                (_, _, _) => throw new IOException("Synthetic compiler failure")), "Compile failure is propagated without publishing the candidate");
            check(File.ReadAllBytes(a).SequenceEqual(committed) && File.ReadAllBytes(metaA).SequenceEqual(committedMeta) && Directory.GetFiles(run).Length == 0,
                "Failed preparation preserves previous rules and cleans temporary files");
            using var cancelled = new CancellationTokenSource();
            await Reject(() => DomainRepository.Prepare(["ru-blocked"], archive, rules, run,
                (_, _, _) => { cancelled.Cancel(); return Task.CompletedTask; }, cancelled.Token), "Cancellation after compile prevents publication");
            check(File.ReadAllBytes(a).SequenceEqual(committed) && Directory.GetFiles(run).Length == 0, "Cancelled preparation leaves the prior cache intact");
            await File.WriteAllBytesAsync(archive, [255, 255, 255]);
            await Reject(() => Prepare(["ru-blocked"]), "A corrupted archive cannot reuse metadata from its previous identity");
            check(File.ReadAllBytes(a).SequenceEqual(committed), "Archive corruption preserves the last prepared rules");
            await File.WriteAllBytesAsync(archive, original);
            await Reject(() => Prepare(["missing"]), "A missing selected category fails rather than silently reducing the union");
            check(!await Prepare([]) && DomainRepository.Current(Array.Empty<string>()) == null && !DomainRepository.Due(Array.Empty<string>()), "No domain categories requires neither source access nor a domain rule file");

            var many = Enumerable.Range(0, 12).Select(index => ($"category-{index}", $"host{index}.example")).ToArray();
            await File.WriteAllBytesAsync(archive, Fixture(many));
            string unrelated = Path.Combine(rules, "user-notes.json");
            await File.WriteAllTextAsync(unrelated, "must remain");
            await Prepare([many[0].Item1]);
            string protectedPath = PathFor(many[0].Item1);
            for (int index = 1; index < many.Length; index++)
                await DomainRepository.Prepare([many[index].Item1], archive, rules, run, Compile, protectedPath: protectedPath);
            check(Directory.GetFiles(rules, "domain-cache-*.json").Length == DomainRepository.MaximumPreparedSelections,
                "Prepared selection cache is bounded to eight entries across repeated choices");
            check(File.Exists(PathFor("category-11")) && File.ReadAllText(unrelated) == "must remain" && Directory.GetFiles(run).Length == 0,
                "Cache pruning preserves the requested output, unrelated files and no compiler scratch data");
            check(File.Exists(protectedPath), "Pruning preserves the prior saved selection until the new settings are committed");
        }
        finally { fixtureDirectory.Delete(recursive: true); }
    }

    // Minimal genuine GeoSite protobuf fixture: entries contain one suffix each.
    static byte[] Fixture(params (string Category, string Domain)[] categories)
    {
        using var file = new MemoryStream();
        foreach (var (category, value) in categories)
        {
            using var domain = new MemoryStream();
            domain.WriteByte(8);
            domain.WriteByte(2);
            Field(domain, 18, Encoding.UTF8.GetBytes(value));
            using var entry = new MemoryStream();
            Field(entry, 10, Encoding.UTF8.GetBytes(category));
            Field(entry, 18, domain.ToArray());
            Field(file, 10, entry.ToArray());
        }
        return file.ToArray();
    }
    static void Field(Stream destination, byte tag, byte[] bytes)
    {
        destination.WriteByte(tag);
        uint size = (uint)bytes.Length;
        while (size >= 128) { destination.WriteByte((byte)(size | 128)); size >>= 7; }
        destination.WriteByte((byte)size);
        destination.Write(bytes);
    }
}
