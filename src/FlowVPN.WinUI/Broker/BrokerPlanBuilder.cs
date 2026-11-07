using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlowVPN;

// Runs only in the unelevated UI. The broker receives rule values, never cache paths.
internal static class BrokerPlanBuilder
{
    const int MaxSourceBytes = 32 * 1024 * 1024;
    const int MaxTerms = 2_000_000;

    public static Task<BrokerPlan> Create(Settings settings, VpnProfile profile, CancellationToken token = default, BrokerLocalTransport? transport = null) =>
        // Parsing and normalizing the shared lists must not resume on the WinUI thread.
        Task.Run(() => CreateCore(settings, profile, token, transport), token);

    static async Task<BrokerPlan> CreateCore(Settings settings, VpnProfile profile, CancellationToken token, BrokerLocalTransport? transport)
    {
        token.ThrowIfCancellationRequested();
        var source = settings.Sources.FirstOrDefault(s => s.Id == profile.SourceId)
            ?? throw new InvalidOperationException("Выберите подключение.");
        string? required = ModuleTransport.RequiredModule(source);
        if (required != transport?.ModuleId)
            throw new InvalidOperationException("Для проверки этого подключения требуется запущенный модуль Outline. Установите модуль и подключите профиль.");
        var plan = new BrokerPlan
        {
            Kind = source.Kind,
            ConnectionJson = source.ConnectionJson,
            LocalTransport = transport,
            Mode = profile.Mode,
            ProcessPatterns = profile.Apps.Select(a => a.Pattern).Distinct(StringComparer.Ordinal).ToArray(),
            BaseEnabled = settings.BaseRulesEnabled
        };
        DomainCoverage? coverage = null;
        if (plan.BaseEnabled)
        {
            string candidate = Path.Combine(Store.Run, "broker-rules-" + Guid.NewGuid().ToString("N") + ".srs");
            string decoded = candidate + ".json";
            try
            {
                byte[] binary = await ReadBounded(Path.Combine(Store.Rules, "ru-blocked.srs"), 16 * 1024 * 1024, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(candidate, binary, token).ConfigureAwait(false);
                await CoreCli.Run(token, "rule-set", "decompile", candidate, "-o", decoded).ConfigureAwait(false);
                var ips = await ReadRules(decoded, token).ConfigureAwait(false);
                plan.IpCidrs = RuleRepository.ParseIpRules(ips, token);
            }
            finally { File.Delete(candidate); File.Delete(decoded); }

            var categories = BaseDomainSelection.From(settings).Categories;
            if (categories.Count > 0)
            {
                var rules = await ReadRules(DomainRepository.RulePath(categories), token).ConfigureAwait(false);
                var exact = new List<string>();
                var suffix = new List<string>();
                var keyword = new List<string>();
                var regex = new List<string>();
                foreach (var rule in rules)
                {
                    token.ThrowIfCancellationRequested();
                    if (rule is not JsonObject fields)
                        throw new InvalidDataException("Некорректная локальная доменная база. Обновите её.");
                    foreach (var field in fields)
                    {
                        var target = field.Key switch
                        {
                            "domain" => exact,
                            "domain_suffix" => suffix,
                            "domain_keyword" => keyword,
                            "domain_regex" => regex,
                            _ => throw new InvalidDataException("Неподдерживаемые правила в локальной доменной базе.")
                        };
                        if (field.Value is not JsonArray array)
                            throw new InvalidDataException("Некорректный формат доменного правила.");
                        Append(target, array, token);
                    }
                }
                plan.BaseDomains = new() { Domain = exact.Distinct(StringComparer.Ordinal).ToArray(), Suffix = suffix.Distinct(StringComparer.Ordinal).ToArray(), Keyword = keyword.Distinct(StringComparer.Ordinal).ToArray(), Regex = regex.Distinct(StringComparer.Ordinal).ToArray() };
                coverage = DomainCoverage.FromRuleSet(new JsonObject { ["rules"] = rules }, token);
            }
        }
        plan.Domains = DomainCoverage.Effective(profile.Domains, coverage, token).ToArray();
        token.ThrowIfCancellationRequested();
        return plan;
    }

    static void Append(List<string> target, JsonArray values, CancellationToken token)
    {
        if ((long)target.Count + values.Count > MaxTerms)
            throw new InvalidDataException("База превышает допустимое число правил.");
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            if (value is not JsonValue item || !item.TryGetValue<string>(out var text) || text.Length is 0 or > 4096)
                throw new InvalidDataException("Некорректное значение в локальной базе.");
            target.Add(text);
        }
    }
    static async Task<JsonArray> ReadRules(string path, CancellationToken token)
    {
        var data = await ReadBounded(path, MaxSourceBytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var json = JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { MaxDepth = 8 });
        token.ThrowIfCancellationRequested();
        if (json is not JsonObject root || root.Any(p => p.Key is not ("version" or "rules")) || root["rules"] is not JsonArray rules || rules.Count == 0)
            throw new InvalidDataException("Некорректная локальная база. Загрузите её заново в настройках.");
        // Detach so the same array can be used to construct coverage without another JSON copy.
        root.Remove("rules");
        return rules;
    }
    static async Task<byte[]> ReadBounded(string path, int limit, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length is <= 0 || stream.Length > limit)
            throw new InvalidDataException("Локальная база пуста или превышает допустимый размер.");
        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Локальная база изменилась во время чтения. Повторите подключение.");
        return bytes;
    }
}
