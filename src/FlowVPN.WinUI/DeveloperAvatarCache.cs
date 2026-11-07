using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FlowVPN;

internal sealed class DeveloperAvatarMetadata
{
    public DateTimeOffset CheckedAt { get; set; }
    public string AvatarUrl { get; set; } = "";
    public string ProfileETag { get; set; } = "";
    public string ImageETag { get; set; } = "";
    public string ImageSha256 { get; set; } = "";
}

internal sealed record DeveloperAvatarEntry(DeveloperAvatarMetadata? Metadata, byte[]? Image);

// Public metadata only. The two fixed cache files never contain tokens or request credentials.
internal sealed class DeveloperAvatarCache(string directory, HttpClient client, TimeProvider? timeProvider = null,
    Func<byte[], CancellationToken, Task<byte[]>>? normalize = null)
{
    internal const int MaxJsonBytes = 64 * 1024, MaxImageBytes = 512 * 1024;
    internal static readonly Uri ProfileUri = new("https://api.github.com/users/vserdobintsev");
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    readonly Func<byte[], CancellationToken, Task<byte[]>> normalizeImage = normalize ?? NormalizeImage;
    internal string ImagePath => Path.Combine(directory, "developer-avatar.png");
    internal string MetadataPath => Path.Combine(directory, "developer-avatar.json");

    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<DeveloperAvatarEntry> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await using var metadataFile = new FileStream(MetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var bytes = await ReadBounded(metadataFile, 4096, token).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<DeveloperAvatarMetadata>(bytes, new JsonSerializerOptions { MaxDepth = 4 });
            if (metadata is null || metadata.ImageSha256 is not { Length: 64 }) return new(null, null);
            _ = ImageUri(metadata.AvatarUrl ?? "");
            if (!ValidETag(metadata.ProfileETag) || !ValidETag(metadata.ImageETag)) return new(null, null);
            byte[]? image = null;
            try
            {
                await using var imageFile = new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                image = await ReadBounded(imageFile, MaxImageBytes, token).ConfigureAwait(false);
                ValidateImageHeader(image, 128);
                if (!Hash(image).Equals(metadata.ImageSha256, StringComparison.OrdinalIgnoreCase)) image = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { image = null; }
            return new(metadata, image);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            return new(null, null);
        }
    }

    public async Task<byte[]?> RefreshAsync(DeveloperAvatarEntry cached, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previous = cached.Metadata;
        DateTimeOffset now = clock.GetUtcNow();
        if (cached.Image != null && previous != null && now >= previous.CheckedAt && now - previous.CheckedAt < TimeSpan.FromHours(24))
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        token = deadline.Token;

        using var profileRequest = Request(ProfileUri, previous?.ProfileETag, profile: true);
        using var profileResponse = await client.SendAsync(profileRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        string avatarUrl, profileETag;
        if (profileResponse.StatusCode == HttpStatusCode.NotModified && previous != null)
        {
            avatarUrl = ImageUri(previous.AvatarUrl).AbsoluteUri;
            profileETag = previous.ProfileETag;
        }
        else
        {
            profileResponse.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await ResponseBytes(profileResponse, MaxJsonBytes, token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Некорректный профиль разработчика.");
            string? url = null;
            foreach (var field in json.RootElement.EnumerateObject())
                if (field.NameEquals("avatar_url"))
                {
                    if (url != null || field.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Некорректный адрес аватарки.");
                    url = field.Value.GetString();
                }
            avatarUrl = ImageUri(url ?? "").AbsoluteUri;
            profileETag = ETag(profileResponse);
        }

        // GitHub can keep avatar_url unchanged after a new upload. Its image ETag is independent of the profile ETag.
        bool sameUrl = previous?.AvatarUrl == avatarUrl;
        using var imageRequest = Request(ImageUri(avatarUrl), sameUrl && cached.Image != null ? previous?.ImageETag : null, profile: false);
        using var imageResponse = await client.SendAsync(imageRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        byte[] image;
        string imageETag;
        if (imageResponse.StatusCode == HttpStatusCode.NotModified && sameUrl && cached.Image != null)
        {
            image = cached.Image;
            imageETag = previous!.ImageETag;
        }
        else
        {
            imageResponse.EnsureSuccessStatusCode();
            byte[] source = await ResponseBytes(imageResponse, MaxImageBytes, token).ConfigureAwait(false);
            ValidateImageHeader(source, 1024);
            image = await normalizeImage(source, token).ConfigureAwait(false);
            if (image.Length > MaxImageBytes) throw new InvalidDataException("Слишком большая аватарка.");
            ValidateImageHeader(image, 128);
            imageETag = ETag(imageResponse);
        }
        token.ThrowIfCancellationRequested();
        bool changed = cached.Image == null || !image.AsSpan().SequenceEqual(cached.Image);
        var metadata = new DeveloperAvatarMetadata
        {
            CheckedAt = now, AvatarUrl = avatarUrl, ProfileETag = profileETag, ImageETag = imageETag, ImageSha256 = Hash(image)
        };
        Directory.CreateDirectory(directory);
        if (changed) Store.Atomic(ImagePath, image);
        Store.Atomic(MetadataPath, JsonSerializer.SerializeToUtf8Bytes(metadata));
        return changed ? image : null;
    }

    static HttpRequestMessage Request(Uri uri, string? etag, bool profile)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("FlowVPN/1.5");
        if (profile)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        else request.Headers.Accept.ParseAdd("image/png, image/jpeg, image/gif");
        if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var parsed)) request.Headers.IfNoneMatch.Add(parsed);
        return request;
    }

    static string ETag(HttpResponseMessage response)
    {
        string value = response.Headers.ETag?.ToString() ?? "";
        return ValidETag(value) ? value : "";
    }
    static bool ValidETag(string? value) => value is { Length: <= 256 } && (value.Length == 0 || EntityTagHeaderValue.TryParse(value, out _));
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static Uri ImageUri(string text)
    {
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IdnHost.Equals("avatars.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Некорректный адрес аватарки.");
        var builder = new UriBuilder(uri);
        var fields = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(field => !Uri.UnescapeDataString(field.Split('=', 2)[0]).Equals("s", StringComparison.OrdinalIgnoreCase));
        builder.Query = string.Join('&', fields.Append("s=128"));
        return builder.Uri;
    }

    static async Task<byte[]> ResponseBytes(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Ответ аватарки превышает допустимый размер.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await ReadBounded(stream, maximum, token).ConfigureAwait(false);
    }
    static async Task<byte[]> ReadBounded(Stream stream, int maximum, CancellationToken token)
    {
        if (stream.CanSeek && stream.Length > maximum) throw new InvalidDataException("Кэш аватарки превышает допустимый размер.");
        using var result = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > maximum) throw new InvalidDataException("Аватарка превышает допустимый размер.");
            result.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return result.ToArray();
    }

    // Inspect dimensions before a native decoder allocates pixel buffers. Only static frame zero is retained.
    internal static void ValidateImageHeader(ReadOnlySpan<byte> bytes, int maximum)
    {
        uint width = 0, height = 0;
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
            height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        }
        else if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2));
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            int offset = 2;
            while (offset + 3 < bytes.Length && bytes[offset] == 0xff)
            {
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) break;
                byte marker = bytes[offset++];
                if (marker is 0xda or 0xd9) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (offset + 2 > bytes.Length) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                if (length < 2 || offset + length > bytes.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    if (length < 8) break;
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                    break;
                }
                offset += length;
            }
        }
        if (width is 0 || height is 0 || width > maximum || height > maximum)
            throw new InvalidDataException("Некорректный формат или размер аватарки.");
    }

    internal static async Task<byte[]> NormalizeImage(byte[] source, CancellationToken token)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(source);
            await writer.StoreAsync().AsTask(token).ConfigureAwait(false);
        }
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(token).ConfigureAwait(false);
        if (decoder.PixelWidth is 0 or > 1024 || decoder.PixelHeight is 0 or > 1024)
            throw new InvalidDataException("Некорректный размер аватарки.");
        double scale = Math.Min(1, 128d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)(decoder.PixelHeight * scale));
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask(token).ConfigureAwait(false);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(token).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask(token).ConfigureAwait(false);
        if (output.Size > MaxImageBytes) throw new InvalidDataException("Слишком большая аватарка.");
        byte[] bytes = new byte[checked((int)output.Size)];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)bytes.Length).AsTask(token).ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
