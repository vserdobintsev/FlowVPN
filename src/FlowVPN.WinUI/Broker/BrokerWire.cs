using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowVPN;

internal sealed class BrokerHandshake
{
    public int Version { get; set; } = 1;
    public string Nonce { get; set; } = "";
}

// One request/reply at a time; callers own serialization and per-operation deadlines.
internal static class BrokerWire
{
    public const int MaxFrameBytes = 64 * 1024 * 1024;
    public const int MaxReplyBytes = 4 * 1024 * 1024;
    public const int MaxControlBytes = 64 * 1024;
    public const int MaxHandshakeBytes = 1024;
    static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token, int maxBytes = MaxFrameBytes)
    {
        if (maxBytes is <= 0 or > MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        try
        {
            if (data.Length == 0 || data.Length > maxBytes)
                throw new InvalidDataException("Сообщение VPN превышает допустимый размер.");
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(data, token);
            await stream.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token, int maxBytes = MaxFrameBytes)
    {
        if (maxBytes is <= 0 or > MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxBytes)
            throw new InvalidDataException("Некорректный размер сообщения VPN.");
        byte[] data = GC.AllocateUninitializedArray<byte>(length);
        try
        {
            await stream.ReadExactlyAsync(data, token);
            RejectDuplicateProperties(data);
            return JsonSerializer.Deserialize<T>(data, Options) ?? throw new InvalidDataException("Пустое сообщение VPN.");
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    static void RejectDuplicateProperties(ReadOnlySpan<byte> data)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { MaxDepth = 32 });
        var objects = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: objects.Push(new(StringComparer.Ordinal)); break;
                case JsonTokenType.StartArray: objects.Push(null); break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray: objects.Pop(); break;
                case JsonTokenType.PropertyName:
                    if (!objects.Peek()!.Add(reader.GetString()!))
                        throw new InvalidDataException("Повторяющиеся поля в сообщении VPN недопустимы.");
                    break;
            }
        }
    }

    public static void ValidateHandshake(BrokerHandshake value, string expectedNonce)
    {
        if (value.Version != 1 || !IsNonce(value.Nonce) || !IsNonce(expectedNonce) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(value.Nonce), Convert.FromHexString(expectedNonce)))
            throw new InvalidDataException("Не удалось проверить сеанс управления VPN.");
    }

    public static bool IsNonce(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
