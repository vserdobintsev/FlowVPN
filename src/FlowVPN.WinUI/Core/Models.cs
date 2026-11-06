using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlowVPN;

public sealed class VpnSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "OpenVPN";
    public string DynamicKey { get; set; } = "";
    public string ConnectionJson { get; set; } = "{}";
    public string Origin { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public override string ToString() => $"{Name}  ·  {Kind}";
}
public sealed class AppRule
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string ComponentRole { get; set; } = "application";
    public string ScopeNote { get; set; } = "";
    public override string ToString() => Name;
    // All fields are immutable strings; each profile owns a separate mutable rule object.
    public AppRule Copy() => (AppRule)MemberwiseClone();
    public static AppRule File(string path) => File(path, null);
    public static AppRule File(string path, string? name) => new() { Name = name ?? System.IO.Path.GetFileNameWithoutExtension(path), Path = path, Pattern = "(?i)^" + Regex.Escape(path) + "$" };
    public static AppRule Folder(string path, string? name = null) => new() { Name = name ?? System.IO.Path.GetFileName(path), Path = path, Pattern = "(?i)^" + Regex.Escape(path.TrimEnd('\\') + "\\") + @".*\.exe$" };
}
public sealed class VpnProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый профиль";
    public string SourceId { get; set; } = "";
    public string Mode { get; set; } = "include";
    public string Icon { get; set; } = "auto";
    public int IconColor { get; set; } = -1;
    public List<AppRule> Apps { get; set; } = [];
    public List<string> Domains { get; set; } = [];
    public override string ToString() => Name;
}
public sealed class Settings
{
    public UpdatePreferences Updates { get; set; } = new();
    public int Version { get; set; } = 1;
    public string BaseDomainCategory { get; set; } = "ru-blocked";
    public bool BaseRulesEnabled { get; set; } = true;
    public bool AutoUpdateRules { get; set; } = true;
    public bool StartInTray { get; set; } = true;
    public int Appearance { get; set; } = 0;
    public string SelectedProfileId { get; set; } = "";
    public List<VpnSource> Sources { get; set; } = [];
    public List<VpnProfile> Profiles { get; set; } = [];
}
public sealed class UpdatePreferences
{
    public string Channel { get; set; } = "stable";
    public bool CheckWhileOpen { get; set; } = true;
    public DateTimeOffset? LastCheckedUtc
    {
        get; set;
    }
    public string LastCheckedChannel { get; set; } = "";
    public string PendingVersion { get; set; } = "";
}
public static class Store
{
#if FLOWVPN_PACKAGE
    public static string Root => AppContext.BaseDirectory;
#else
    public static string Root = AppContext.BaseDirectory;
#endif
    public static Action<Settings>? SeedCatalogProfiles;
    public static string? DataDirectory;
    public static string Data => DataDirectory ?? System.IO.Path.Combine(Root, "data");
    public static string Bin => System.IO.Path.Combine(Root, "core", "sing-box.exe");
    public static string Rules => System.IO.Path.Combine(Data, "rules");
    public static string Run => System.IO.Path.Combine(Data, "run");
    public static string ProfileLogs(string id) => System.IO.Path.Combine(Data, "logs", "profiles", Hash(id));
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static void Init()
    {
        Directory.CreateDirectory(Data);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        acl.SetOwner(sid);
        foreach (var s in new[] { sid, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
            acl.AddAccessRule(new FileSystemAccessRule(s, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Data).SetAccessControl(acl);
        Directory.CreateDirectory(Rules);
        Directory.CreateDirectory(Run);
        Directory.CreateDirectory(System.IO.Path.Combine(Data, "logs"));
    }
    public static Settings Load()
    {
        var f = System.IO.Path.Combine(Data, "settings.dat");
        return File.Exists(f) ? JsonSerializer.Deserialize<Settings>(Native.Unprotect(File.ReadAllBytes(f))) ?? throw new Exception("Не удалось прочитать настройки.") : new();
    }
    public static void Save(Settings s) => Atomic(System.IO.Path.Combine(Data, "settings.dat"), Native.Protect(JsonSerializer.SerializeToUtf8Bytes(s, JsonOptions)));
    public static void Atomic(string path, byte[] bytes)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public static void WriteJson(string path, JsonNode o) => Atomic(path, Encoding.UTF8.GetBytes(o.ToJsonString(JsonOptions)));
    public static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static void SeedProfiles(Settings settings) => SeedCatalogProfiles?.Invoke(settings);
    public static bool AddSource(Settings s, VpnSource source)
    {
        if (s.Sources.Any(x => x.Fingerprint == source.Fingerprint || (x.Kind == source.Kind && x.ConnectionJson == source.ConnectionJson)))
            return false;
        s.Sources.Add(source);
        return true;
    }
    public static string[] Tail(string path, int count = 150)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length > 100000)
                f.Seek(-100000, SeekOrigin.End);
            using var r = new StreamReader(f);
            return r.ReadToEnd().Split('\n').TakeLast(count).ToArray();
        }
        catch { return []; }
    }
}
