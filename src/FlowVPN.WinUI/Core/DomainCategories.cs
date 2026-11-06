using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlowVPN;

public sealed record DomainCategory(string Id, string Name, string Description)
{
    public override string ToString() => Name + " · " + Id;
}

public static class DomainCategories
{
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
    public static Task Update(HttpClient client) => GeoSiteArchive.Update(client, true);
}
