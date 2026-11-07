using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlowVPN;

internal sealed record ProfileValidationSnapshot(Settings Settings, VpnProfile Profile);

// Builds a configuration only for `sing-box check`. It never starts a tunnel/transport or refreshes a dynamic key.
internal static class ProfileValidator
{
    public static ProfileValidationSnapshot Capture(Settings settings, string profileId)
    {
        var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.SerializeToUtf8Bytes(settings))
            ?? throw new InvalidOperationException("Не удалось прочитать настройки профиля.");
        var profile = copy.Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException("Выбранный профиль больше не существует.");
        return new(copy, profile);
    }

    public static async Task<JsonObject> BuildConfiguration(ProfileValidationSnapshot snapshot, Uplink uplink, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var source = snapshot.Settings.Sources.FirstOrDefault(item => item.Id == snapshot.Profile.SourceId)
            ?? throw new InvalidOperationException("Выберите подключение.");
        string? required = ModuleTransport.RequiredModule(source);
        BrokerLocalTransport? transport = required == null ? null : new()
        {
            // Diagnostic endpoints are never dialed. This preserves the actual SOCKS routing shape
            // while checking the typed module options without downloading or starting its executable.
            ModuleId = required, ProtocolVersion = 1, InterfaceName = uplink.Name, Port = 15877,
            Username = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
        };
        var plan = await BrokerPlanBuilder.Create(snapshot.Settings, snapshot.Profile, token, transport).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var control = new CoreControl(15876);
        return BrokerConfig.Build(plan, uplink, 15876, control, new ProxyAccess());
    }
}
