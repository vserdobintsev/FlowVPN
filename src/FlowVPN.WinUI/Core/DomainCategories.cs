using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlowVPN;

public enum DomainCategoryGroup { SitesAndOther, Themes, Regions, Technical, Blocklists }

public sealed record DomainCategory(string Id, string Name, string Description)
{
    public DomainCategoryGroup Group => DomainCategories.GroupOf(Id);
    public override string ToString() => Name + " · " + Id;
}

public static class DomainCategories
{
    // Upstream naming conventions: https://github.com/v2fly/domain-list-community#how-to-organize-domains
    // Regional exceptions are confirmed source entries. Two-letter ids also name brands, so ISO-code inference is unsafe.
    public static DomainCategoryGroup GroupOf(string id) => id switch
    {
        "cn" or "category-ru" or "category-ir" or "category-tm" or "ru-available-only-inside" => DomainCategoryGroup.Regions,
        _ when id.StartsWith("geolocation-", StringComparison.Ordinal) => DomainCategoryGroup.Regions,
        "private" or "category-doh" or "category-ddns" or "category-ntp" or "category-stun" or "category-ipfs" => DomainCategoryGroup.Technical,
        _ when id.StartsWith("tld-", StringComparison.Ordinal) || id.StartsWith("win-", StringComparison.Ordinal) || id.StartsWith("category-ntp-", StringComparison.Ordinal) => DomainCategoryGroup.Technical,
        "ru-blocked" or "ru-blocked-all" or "refilter" => DomainCategoryGroup.Blocklists,
        _ when id.StartsWith("antifilter-", StringComparison.Ordinal) => DomainCategoryGroup.Blocklists,
        _ when id.StartsWith("category-", StringComparison.Ordinal) => DomainCategoryGroup.Themes,
        _ => DomainCategoryGroup.SitesAndOther
    };

    static string CatalogPath => Path.Combine(Store.Rules, "domain-categories.json");
    public static DomainCategory Describe(string id) => id switch
    {
        "ru-blocked" => new(id, "Рекомендуемый список блокировок", "Домены из объединённого списка RunetFreedom. Вариант по умолчанию."),
        "ru-blocked-all" => new(id, "Расширенный список блокировок", "Все известные заблокированные домены: более 700 тысяч записей. Загрузка и обработка требуют больше памяти."),
        "antifilter-download" => new(id, "Антифильтр · полный список", "Большой список доменов из antifilter.download."),
        "antifilter-download-community" => new(id, "Антифильтр · сообщество", "Список, поддерживаемый сообществом antifilter.download."),
        "refilter" => new(id, "re:filter", "Отдельный список re:filter."),
        "google" => new(id, "Google", "Домены сервисов Google."),
        "youtube" => new(id, "YouTube", "Домены YouTube."),
        "discord" => new(id, "Discord", "Домены Discord."),
        "category-ads-all" => new(id, "Рекламные домены", "Направляет домены из рекламного списка через VPN. Это маршрутизация, а не блокировщик рекламы."),
        "win-spy" => new(id, "Телеметрия Windows", "Домены телеметрии Windows из списка источника. Выбор направляет их через VPN и не блокирует запросы."),
        "win-update" => new(id, "Обновления Windows", "Домены служб обновления Windows."),
        "win-extra" => new(id, "Другие сервисы Windows", "Дополнительные домены Windows из списка источника."),
        "private" => new(id, "Частные домены", "Служебные и частные имена из списка источника. Их маршрутизация через VPN может повлиять на доступ к локальным сервисам."),
        "ru-available-only-inside" => new(id, "Доступные только из России", "Зарубежный VPN может мешать доступу к этим сайтам."),
        _ => new(id, id, "Категория, опубликованная RunetFreedom. Её домены направляются через VPN.")
    };
    public static IReadOnlyList<DomainCategory> Read()
    {
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(CatalogPath)) ?? [];
            return ids.Select(DomainRepository.CategoryId).Distinct().Select(Describe).OrderBy(c => c.Id == "ru-blocked" ? 0 : c.Id == "ru-blocked-all" ? 1 : 2).ThenBy(c => c.Name).ToArray();
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException) { return [Describe("ru-blocked"), Describe("ru-blocked-all")]; }
    }
    public static Task Update(HttpClient client, CancellationToken token = default, IProgress<TransferProgress>? progress = null) => GeoSiteArchive.Update(client, true, token, progress);
}
