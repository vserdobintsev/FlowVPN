using System.Text;
using System.Text.Json;

namespace FlowVPN;

public enum ConnectionInputKind { Unknown, OpenVpn, OutlineKey, DynamicOutlineKey, ShadowsocksJson }

// Detection is deliberately pure. A pasted subscription URL is never fetched for a preview.
public static class ConnectionInput
{
    const int MaxInputBytes = 4 * 1024 * 1024;
    static readonly string[] ShadowsocksFields = ["server", "server_port", "method", "password"];

    public static ConnectionInputKind Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxInputBytes || Encoding.UTF8.GetByteCount(text) > MaxInputBytes)
            return ConnectionInputKind.Unknown;
        var value = text.AsSpan().Trim().TrimStart('\uFEFF').TrimStart();
        if (value.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            return ConnectionInputKind.OutlineKey;
        if (value.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase))
            return ConnectionInputKind.DynamicOutlineKey;
        if (value.StartsWith("{"))
        {
            try
            {
                using var json = JsonDocument.Parse(value.ToString(), new JsonDocumentOptions { MaxDepth = 16 });
                return json.RootElement.ValueKind == JsonValueKind.Object && ShadowsocksFields.Any(field => json.RootElement.TryGetProperty(field, out _))
                    ? ConnectionInputKind.ShadowsocksJson : ConnectionInputKind.Unknown;
            }
            catch (JsonException)
            {
                // Keep a recognizable incomplete object in its format; validation on Save explains the error.
                foreach (string field in ShadowsocksFields)
                    if (value.Contains("\"" + field + "\"", StringComparison.Ordinal))
                        return ConnectionInputKind.ShadowsocksJson;
                return ConnectionInputKind.Unknown;
            }
        }
        foreach (var rawLine in value.EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty || line[0] is '#' or ';')
                continue;
            if (line.StartsWith("<ca>", StringComparison.OrdinalIgnoreCase) || line.StartsWith("<cert>", StringComparison.OrdinalIgnoreCase) || line.StartsWith("<key>", StringComparison.OrdinalIgnoreCase))
                return ConnectionInputKind.OpenVpn;
            int separator = line.IndexOfAny(' ', '\t');
            var directive = separator < 0 ? line : line[..separator];
            if (directive.StartsWith("--")) directive = directive[2..];
            if (directive.Equals("client", StringComparison.OrdinalIgnoreCase) || directive.Equals("remote", StringComparison.OrdinalIgnoreCase) ||
                directive.Equals("proto", StringComparison.OrdinalIgnoreCase) || directive.Equals("dev", StringComparison.OrdinalIgnoreCase))
                return ConnectionInputKind.OpenVpn;
        }
        return ConnectionInputKind.Unknown;
    }

    public static async Task<VpnSource> ImportAsync(string text, string? name = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateSize(text);
        text = text.Trim().TrimStart('\uFEFF').TrimStart();
        VpnSource source;
        try
        {
            source = Detect(text) switch
            {
                ConnectionInputKind.OpenVpn => Importers.OpenVpnText(text, name),
                ConnectionInputKind.OutlineKey => Importers.Outline(text, name),
                ConnectionInputKind.DynamicOutlineKey => await Importers.DynamicOutline(text, token).ConfigureAwait(false),
                ConnectionInputKind.ShadowsocksJson => Importers.DynamicPayload(text),
                _ => throw new InvalidDataException("Формат не распознан. Вставьте конфигурацию OpenVPN, ключ ss:// или ssconf:// либо JSON с параметрами Shadowsocks.")
            };
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("JSON подключения повреждён. Проверьте синтаксис и поля server, server_port, method и password.", error);
        }
        if (!string.IsNullOrWhiteSpace(name)) source.Name = name.Trim();
        token.ThrowIfCancellationRequested();
        return source;
    }

    public static async Task<ConnectionInputKind> DetectFileAsync(string path, CancellationToken token = default)
    {
        var input = await ReadFileAsync(path, token).ConfigureAwait(false);
        return await Task.Run(() => Detect(input.Text), token).ConfigureAwait(false);
    }

    public static async Task<VpnSource> ImportFileAsync(string path, string? name = null, CancellationToken token = default)
    {
        var input = await ReadFileAsync(path, token).ConfigureAwait(false);
        // Explicitly keep local parsing off a caller's UI context, including synchronously cached reads.
        var source = await Task.Run(() => Detect(input.Text) == ConnectionInputKind.OpenVpn ? Importers.OpenVpn(input.Path) : null, token).ConfigureAwait(false);
        if (source == null)
            return await ImportAsync(input.Text, name, token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(name)) source.Name = name.Trim();
        token.ThrowIfCancellationRequested();
        return source;
    }

    static async Task<(string Path, string Text)> ReadFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!LocalPathPolicy.TryFile(path, out string full))
            throw new InvalidDataException("Выберите существующий локальный файл подключения. Сетевые пути не поддерживаются.");
        string text;
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        {
            if (stream.Length is <= 0 or > MaxInputBytes)
                throw new InvalidDataException("Файл подключения должен быть непустым и не превышать 4 МиБ.");
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }
        ValidateSize(text);
        token.ThrowIfCancellationRequested();
        return (full, text);
    }

    static void ValidateSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxInputBytes || Encoding.UTF8.GetByteCount(text) > MaxInputBytes)
            throw new InvalidDataException("Введите непустые параметры подключения размером не больше 4 МиБ.");
    }
}
