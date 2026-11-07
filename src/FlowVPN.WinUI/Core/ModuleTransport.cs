using System.Text;
using System.Text.Json;

namespace FlowVPN;

// Persist only typed connection options. Executable paths and download URLs never belong to a source.
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ModuleTransportDescriptor
{
    public string ModuleId { get; init; } = "";
    public int SchemaVersion { get; init; } = 1;
    public OutlinePrefixOptions? Outline { get; init; }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record OutlinePrefixOptions
{
    public string PrefixBase64 { get; init; } = "";
}

internal sealed record OutlineTransportParameters(string Server, int ServerPort, string Method, string Password, string PrefixBase64);

internal static class ModuleTransport
{
    public const string OutlineModuleId = "outline-transport";
    public const string OutlineCapability = "outline.ss-prefix.tcp.v1";

    public static string? RequiredModule(VpnSource source)
    {
        if (source.Transport == null) return null;
        _ = OutlineParameters(source);
        return OutlineModuleId;
    }

    public static VpnSource WithOutlinePrefix(VpnSource source, string prefix)
    {
        if (prefix.Length == 0) return source;
        if (prefix.Length > 16 || prefix.Any(c => c > byte.MaxValue))
            throw new InvalidDataException("Параметры маскировки этого ключа Outline не поддерживаются. Нужен префикс не длиннее 16 байт без символов за пределами U+00FF.");
        byte[] bytes = new byte[prefix.Length];
        for (int i = 0; i < prefix.Length; i++) bytes[i] = (byte)prefix[i];
        source.Transport = new() { ModuleId = OutlineModuleId, Outline = new() { PrefixBase64 = Convert.ToBase64String(bytes) } };
        _ = OutlineParameters(source);
        source.Fingerprint = Store.Hash(source.ConnectionJson + "\n" + JsonSerializer.Serialize(source.Transport));
        return source;
    }

    public static OutlineTransportParameters OutlineParameters(VpnSource source)
    {
        var transport = source.Transport;
        if (source.Kind != "Outline" || transport is not { ModuleId: OutlineModuleId, SchemaVersion: 1, Outline: not null })
            throw new InvalidDataException("Подключение требует неподдерживаемый транспортный модуль. Обновите FlowVPN.");
        var node = BrokerConfig.Source(source.Kind, source.ConnectionJson);
        string method = node["method"]!.GetValue<string>();
        int limit = method switch
        {
            "aes-128-gcm" => 8,
            "aes-256-gcm" or "chacha20-ietf-poly1305" => 16,
            _ => throw new InvalidDataException("Модуль Outline пока не поддерживает маскировку для выбранного метода шифрования.")
        };
        string encoded = transport.Outline.PrefixBase64;
        Span<byte> bytes = stackalloc byte[16];
        if (encoded == null || encoded.Length > 24 || !Convert.TryFromBase64String(encoded, bytes, out int count) ||
            count < 1 || count > limit || Convert.ToBase64String(bytes[..count]) != encoded)
            throw new InvalidDataException("Параметры маскировки Outline повреждены или оставляют недостаточно случайных байтов. Ключ не был изменён; запросите совместимый ключ у поставщика.");
        return new(node["server"]!.GetValue<string>(), node["server_port"]!.GetValue<int>(), method, node["password"]!.GetValue<string>(), encoded);
    }

    internal static string DecodeQuery(string value)
    {
        // URI prefixes use percent-encoded UTF-8, then U+0000..U+00FF map one-to-one to salt bytes.
        var utf8 = new UTF8Encoding(false, true);
        using var bytes = new MemoryStream();
        try
        {
            for (int i = 0; i < value.Length;)
            {
                if (value[i] == '%')
                {
                    if (i + 2 >= value.Length || !byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out byte decoded))
                        throw new InvalidDataException("Некорректное кодирование параметра ключа Outline.");
                    bytes.WriteByte(decoded);
                    i += 3;
                }
                else if (value[i] == '+') { bytes.WriteByte(32); i++; }
                else
                {
                    int end = i + 1;
                    while (end < value.Length && value[end] is not ('%' or '+')) end++;
                    bytes.Write(utf8.GetBytes(value[i..end]));
                    i = end;
                }
            }
            return utf8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
        }
        catch (Exception error) when (error is EncoderFallbackException or DecoderFallbackException)
        {
            throw new InvalidDataException("Некорректное кодирование параметра ключа Outline.");
        }
    }
}
