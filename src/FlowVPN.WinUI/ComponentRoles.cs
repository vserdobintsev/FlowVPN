namespace FlowVPN;

internal static class ComponentRoles
{
    public static string Label(string role) => role switch
    {
        "application" => "Приложение",
        "engine" => "Движок",
        "extension" => "Расширение приложения",
        "shared-service" => "Общая служба",
        _ => "Компонент приложения"
    };
}
