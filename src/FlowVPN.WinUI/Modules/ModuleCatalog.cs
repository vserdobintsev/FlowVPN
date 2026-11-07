using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlowVPN;

internal sealed record ModuleDefinition(string Id, string Version, string Name, string Description,
    string ExecutableName, string DownloadUrl, long SizeBytes, string Sha256,
    IReadOnlyList<string> Capabilities, int ProtocolVersion = 1, string Architecture = "x64")
{
    public bool CanDownload => SizeBytes > 0 && Sha256.Length == 64 && DownloadUrl.Length > 0;
}

internal interface IModuleCatalog
{
    IReadOnlyList<ModuleDefinition> Modules { get; }
}

/// <summary>The installed application's embedded catalog is the trust anchor, never a remote manifest.</summary>
internal sealed class ModuleCatalog : IModuleCatalog
{
    static readonly Lazy<ModuleCatalog> current = new(LoadEmbedded);
    public static ModuleCatalog Current => current.Value;
    public IReadOnlyList<ModuleDefinition> Modules { get; }
    internal const long MaximumArtifactBytes = 128L * 1024 * 1024;

    internal ModuleCatalog(IEnumerable<ModuleDefinition> definitions)
    {
        var entries = definitions.Take(33).ToArray();
        if (entries.Length > 32 || entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("Каталог модулей содержит слишком много записей или повторяющиеся идентификаторы.");
        foreach (var entry in entries) Validate(entry);
        Modules = Array.AsReadOnly(entries.Select(entry => entry with
        {
            Capabilities = Array.AsReadOnly(entry.Capabilities.ToArray())
        }).ToArray());
    }

    static ModuleCatalog LoadEmbedded()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlowVPN.modules.lock.json")
            ?? throw new InvalidDataException("В поставке FlowVPN отсутствует каталог модулей.");
        if (resource.Length > 256 * 1024) throw new InvalidDataException("Каталог модулей слишком большой.");
        using var json = JsonDocument.Parse(resource, new() { MaxDepth = 8 });
        if (json.RootElement.GetProperty("SchemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Версия каталога модулей не поддерживается.");
        return new(json.RootElement.GetProperty("Modules").Deserialize<ModuleDefinition[]>()
            ?? throw new InvalidDataException("Некорректный каталог модулей."));
    }

    internal static void Validate(ModuleDefinition entry)
    {
        static bool Token(string? value, string pattern) => value != null && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant);
        if (!Token(entry.Id, @"\A[a-z0-9][a-z0-9-]{0,63}\z") ||
            !Token(entry.Version, @"\A[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}\z") ||
            !Token(entry.ExecutableName, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,100}\.exe\z") ||
            string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 128 || entry.Description == null || entry.Description.Length > 2048 ||
            entry.Capabilities == null || entry.Capabilities.Count is 0 or > 16 ||
            entry.Capabilities.Any(value => !Token(value, @"\A[a-z0-9][a-z0-9.-]{0,127}\z")) ||
            entry.Capabilities.Distinct(StringComparer.Ordinal).Count() != entry.Capabilities.Count ||
            entry.ProtocolVersion != 1 || entry.Architecture != "x64")
            throw new InvalidDataException("Некорректное или несовместимое описание модуля.");
        // An unpublished catalog entry is visible but cannot be installed. Partial pins are never accepted.
        if (entry.SizeBytes == 0 && entry.Sha256 == "" && entry.DownloadUrl == "") return;
        if (entry.SizeBytes is <= 0 or > MaximumArtifactBytes || !Token(entry.Sha256, @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("Размер или SHA-256 модуля не закреплён в каталоге.");
        var expected = $"https://github.com/vserdobintsev/FlowVPN/releases/download/{entry.Id}-v{entry.Version}/{entry.ExecutableName}";
        if (!string.Equals(entry.DownloadUrl, expected, StringComparison.Ordinal))
            throw new InvalidDataException("Артефакт модуля должен принадлежать закреплённому выпуску FlowVPN.");
    }

    internal static bool IsCompatible(ModuleDefinition definition) => definition.ProtocolVersion == 1 &&
        definition.Architecture == "x64" && RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
}
