using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
namespace FlowVPN;

internal sealed class UpdateChannel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public override string ToString() => Name;
}
internal sealed class UpdateChannels
{
    public int SchemaVersion
    {
        get; set;
    }
    public List<UpdateChannel> Channels { get; set; } = [];
    public static UpdateChannels Load()
    {
        var value = JsonSerializer.Deserialize<UpdateChannels>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UpdateChannels.json")))!;
        if (value.SchemaVersion != 1 || value.Channels.Count == 0 || value.Channels.Select(c => c.Id).Distinct().Count() != value.Channels.Count)
            throw new InvalidDataException("Неверный каталог каналов обновления.");
        foreach (var channel in value.Channels)
        {
            if (channel.Id is not ("stable" or "preview") || channel.PackageName.Length == 0)
                throw new InvalidDataException("Неизвестный канал обновления.");
            if (channel.FeedUrl.Length > 0)
                UpdateProtocol.Https(channel.FeedUrl);
        }
        return value;
    }
}
internal sealed class UpdateRelease
{
    public int SchemaVersion
    {
        get; set;
    }
    public string Channel { get; set; } = "";
    public string Version { get; set; } = "";
    public string CoreVersion { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int MinimumWindowsBuild
    {
        get; set;
    }
    public string PackageUrl { get; set; } = "";
    public string AppInstallerUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes
    {
        get; set;
    }
    public string Notes { get; set; } = "";
}
internal sealed record UpdateIdentity(string Name, string Publisher, string Architecture, Version Version);
internal static class UpdateProtocol
{
    public const long MaxPackageBytes = 700L * 1024 * 1024;
    public static Uri Https(string text) => BoundedHttp.Https(text);
    public static Version Version4(string text)
    {
        if (!Regex.IsMatch(text, @"^\d+\.\d+\.\d+\.\d+$") || !Version.TryParse(text, out var version)
            || new[] { version.Major, version.Minor, version.Build, version.Revision }.Any(n => n < 0 || n > 65535))
            throw new InvalidDataException("Неверная версия пакета.");
        return version;
    }
    public static UpdateRelease Parse(string json, UpdateChannel channel, UpdateIdentity current, int windowsBuild)
    {
        var release = JsonSerializer.Deserialize<UpdateRelease>(json) ?? throw new InvalidDataException("Пустое описание обновления.");
        if (release.SchemaVersion != 1 || release.Channel != channel.Id)
            throw new InvalidDataException("Канал обновления не совпадает.");
        if (release.PackageName != channel.PackageName || release.PackageName != current.Name || release.Publisher != current.Publisher)
            throw new InvalidDataException("Обновление предназначено для другого приложения или издателя.");
        if (!release.Architecture.Equals(current.Architecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Архитектура обновления не подходит этому приложению.");
        Version4(release.Version);
        if (release.MinimumWindowsBuild < 19041 || release.MinimumWindowsBuild > windowsBuild)
            throw new InvalidDataException("Обновление требует другой версии Windows.");
        if (release.SizeBytes <= 0 || release.SizeBytes > MaxPackageBytes || !Regex.IsMatch(release.Sha256, @"\A[0-9A-Fa-f]{64}\z"))
            throw new InvalidDataException("Неверный размер или контрольная сумма обновления.");
        if (!Https(release.PackageUrl).AbsolutePath.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ожидался пакет MSIX.");
        if (!Https(release.AppInstallerUrl).AbsolutePath.EndsWith(".appinstaller", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Неверный адрес App Installer.");
        if (release.Notes.Length > 16000 || release.CoreVersion.Length is < 1 or > 80)
            throw new InvalidDataException("Неверное описание выпуска.");
        return release;
    }
    public static bool CanStage(bool preview, bool packaged, bool busy, UpdateIdentity identity, UpdateRelease? release) =>
        !preview && packaged && !busy && release != null && release.PackageName == identity.Name && release.Publisher == identity.Publisher
        && Version4(release.Version) > identity.Version;
    public static void ValidatePackage(Stream stream, UpdateRelease release)
    {
        if (!stream.CanSeek || stream.Length != release.SizeBytes)
            throw new InvalidDataException("Размер пакета не совпадает с описанием выпуска.");
        stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма пакета не совпадает.");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
        var manifests = zip.Entries.Where(e => e.FullName.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1 || manifests[0].Length > 1024 * 1024 || zip.GetEntry("AppxSignature.p7x") == null)
            throw new InvalidDataException("Пакет не содержит единственный манифест и подпись MSIX.");
        using var entry = manifests[0].Open();
        using var reader = XmlReader.Create(entry, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var doc = XDocument.Load(reader);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = doc.Root?.Element(ns + "Identity") ?? throw new InvalidDataException("В пакете нет идентификатора приложения.");
        if ((string?)identity.Attribute("Name") != release.PackageName || (string?)identity.Attribute("Publisher") != release.Publisher
            || (string?)identity.Attribute("Version") != release.Version || (string?)identity.Attribute("ProcessorArchitecture") != release.Architecture)
            throw new InvalidDataException("Содержимое MSIX не совпадает с проверенным выпуском.");
        // Windows validates the actual signature and publisher trust when staging. Never add certificates here.
    }
    public static UpdateIdentity Identity()
    {
        try
        {
            var id = Windows.ApplicationModel.Package.Current.Id;
            var v = id.Version;
            return new(id.Name, id.Publisher, id.Architecture.ToString().ToLowerInvariant(), new(v.Major, v.Minor, v.Build, v.Revision));
        }
        catch { return new(Entry.Preview ? "FlowVPN.Preview" : "FlowVPN", Entry.Preview ? "CN=FlowVPN Development" : "CN=FlowVPN", "x64", Assembly.GetExecutingAssembly().GetName().Version ?? new(1, 0, 0, 0)); }
    }
    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current.Id;
                return true;
            }
            catch { return false; }
        }
    }
    public static string ExpectedCoreVersion()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlowVPN.engine.lock.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("Version").GetString()!;
    }
}
