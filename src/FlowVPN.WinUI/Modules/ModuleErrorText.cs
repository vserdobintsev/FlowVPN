using System.Net;

namespace FlowVPN;

internal static class ModuleErrorText
{
    // Never display an exception's raw text: HTTP errors can contain URLs or proxy credentials.
    public static string Describe(Exception error) => error switch
    {
        DownloadInterruptedException => "Не удалось завершить загрузку. Полученные данные сохранены. Нажмите «Повторить»: при поддержке сервера загрузка продолжится, иначе начнётся заново.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } => "Файл модуля пока недоступен на сервере выпуска. Повторите попытку позже или проверьте обновления FlowVPN.",
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout } or TimeoutException or OperationCanceledException => "Сервер загрузки не ответил вовремя. Повторите попытку позже.",
        HttpRequestException { StatusCode: not null } => "Сервер выпуска не смог выдать файл модуля. Повторите попытку позже.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError } => "Не удалось соединиться с сервером загрузки. Проверьте подключение к интернету и повторите попытку.",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "Не удалось установить защищённое соединение с сервером загрузки. Повторите попытку позже.",
        HttpRequestException => "Не удалось завершить загрузку с сервера выпуска. Повторите попытку позже.",
        UnauthorizedAccessException => "Windows не разрешила сохранить файлы модуля. Проверьте доступ к папке данных FlowVPN.",
        InvalidDataException => "Модуль не прошёл проверку. Повторите загрузку или проверьте обновления FlowVPN.",
        _ => "Не удалось завершить операцию с модулем. Повторите попытку."
    };
}
