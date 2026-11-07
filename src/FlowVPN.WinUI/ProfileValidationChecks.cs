using System.Text.Json.Nodes;

namespace FlowVPN;

internal static class ProfileValidationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var source = Importers.Outline("ss://YWVzLTEyOC1nY206dGVzdA==@unreachable.invalid:12345");
        source.DynamicKey = "https://unused.invalid/not-fetched-by-validation";
        var profile = new VpnProfile { Name = "Validation fixture", SourceId = source.Id, Domains = ["first.example"], Apps = [AppRule.File(@"D:\Validation fixture\app.exe")] };
        var settings = new Settings { BaseRulesEnabled = false, Sources = [source], Profiles = [profile] };
        var snapshot = ProfileValidator.Capture(settings, profile.Id);
        profile.Domains.Add("later.example");
        profile.Mode = "all";
        settings.BaseRulesEnabled = true;
        settings.Sources.Clear();
        check(snapshot.Profile.Domains.SequenceEqual(new[] { "first.example" }) && snapshot.Profile.Mode == "include"
            && !snapshot.Settings.BaseRulesEnabled && snapshot.Settings.Sources.Count == 1,
            "Validation captures a complete independent settings/profile snapshot before asynchronous work");
        bool missing = false;
        try { ProfileValidator.Capture(settings, "missing-profile"); }
        catch (InvalidOperationException) { missing = true; }
        check(missing, "Validation rejects a profile that no longer exists");

        var uplink = new Uplink("Validation fixture uplink", "127.0.0.1");
        var plain = await ProfileValidator.BuildConfiguration(snapshot, uplink);
        await CoreCli.Check(plain);
        check(plain["outbounds"]!.AsArray().Any(item => item?["type"]?.ToString() == "shadowsocks")
            && snapshot.Settings.Sources[0].DynamicKey == source.DynamicKey,
            "A saved dynamic source validates without fetching its key, resolving its unreachable server or starting VPN");

        var moduleSource = ModuleTransport.WithOutlinePrefix(Importers.Outline("ss://YWVzLTEyOC1nY206dGVzdA==@unreachable.invalid:12345"), "POST ");
        var moduleProfile = new VpnProfile { SourceId = moduleSource.Id };
        var moduleSettings = new Settings { BaseRulesEnabled = false, Sources = [moduleSource], Profiles = [moduleProfile] };
        var moduleSnapshot = ProfileValidator.Capture(moduleSettings, moduleProfile.Id);
        bool guarded = false;
        try { await BrokerPlanBuilder.Create(moduleSnapshot.Settings, moduleSnapshot.Profile); }
        catch (InvalidOperationException) { guarded = true; }
        check(guarded, "Production plan creation still requires a started transport for a module-dependent source");
        var module = await ProfileValidator.BuildConfiguration(moduleSnapshot, uplink);
        var outbound = module["outbounds"]!.AsArray().OfType<JsonObject>().Single(item => item["tag"]?.ToString() == "vpn");
        check(outbound["type"]?.ToString() == "socks" && outbound["server"]?.ToString() == "127.0.0.1"
            && outbound["bind_interface"] != null && BrokerWire.IsNonce(outbound["password"]!.GetValue<string>()),
            "Module validation retains the loopback-only authenticated SOCKS configuration without starting a module");
        var before = Directory.GetFiles(Store.Run).Order(StringComparer.Ordinal).ToArray();
        await Task.WhenAll(CoreCli.Check(plain), CoreCli.Check(module));
        check(Directory.GetFiles(Store.Run).Order(StringComparer.Ordinal).SequenceEqual(before),
            "Concurrent stdin validation accepts independent configs without writing or deleting shared runtime files");
        bool rejected = false;
        try { await CoreCli.Check(new JsonObject { ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "flowvpn-invalid-outbound" }) }); }
        catch (Exception) { rejected = true; }
        check(rejected, "Stdin validation propagates actual sing-box configuration rejection");

        moduleSnapshot.Settings.Sources[0].Transport = moduleSource.Transport! with { Outline = new() { PrefixBase64 = "not base64" } };
        rejected = false;
        try { await ProfileValidator.BuildConfiguration(moduleSnapshot, uplink); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "Validation-only transport descriptors never bypass typed module option validation");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { await CoreCli.Check(plain, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "A cancelled validation does not launch or complete a new core operation");
    }
}
