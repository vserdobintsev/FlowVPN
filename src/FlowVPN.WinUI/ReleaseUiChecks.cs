using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task ReleaseUiChecks()
    {
        timer.Stop();
        var passed = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
                throw new InvalidOperationException(name);
            passed.Add(name);
        }
        Check(!Entry.Preview, "Working build is not in preview mode");
        Check(Profile != null && settings.Sources.Any(s => s.Id == Profile.SourceId), "Selected profile has a connection source");
        UpdateState();
        Check(C<Button>("ConnectButton").IsEnabled, "Connect button is enabled");
        Check(C<TextBlock>("StatusText").Text == "Отключено", "Working disconnected status is displayed");
        Check(trayToggle?.Enabled == true, "Tray connection command is enabled");
        bool blocked = false;
        try
        {
            await Connect();
        }
        catch (InvalidOperationException e) when (e.Message == "Подключение недоступно во время проверки сборки.") { blocked = true; }
        Check(blocked && !engine.Running && !engine.Busy, "UI verification cannot elevate or start a tunnel");
        using (CoreIntegrity.Acquire())
            passed.Add("Bundled core matches the pinned hashes");
        if (settings.BaseRulesEnabled)
        {
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(RuleRepository.MetaPath))!.AsObject();
            Check(await RuleRepository.ValidCache(metadata), "GeoIP cache is intact");
            Check(DomainRepository.Current(settings.BaseDomainCategory) != null, "Selected domain category is available");
        }
        int validatedProfiles = 0;
        string configPath = Path.Combine(Store.Run, "release-validation.json");
        try
        {
            foreach (var profile in settings.Profiles)
            {
                Directory.CreateDirectory(Store.ProfileLogs(profile.Id));
                Store.WriteJson(configPath, await ConfigBuilder.Build(settings, profile, new("Release validation", "127.0.0.1"), 15876));
                // 'check' parses the actual profile, but does not create a TUN or contact its server.
                await CoreCli.Run("check", "-c", configPath);
                validatedProfiles++;
            }
            Check(validatedProfiles == settings.Profiles.Count, "All saved profiles pass the bundled core configuration check");
        }
        finally { File.Delete(configPath); }
        string folder = Path.Combine(Store.Root, "design-preview");
        Directory.CreateDirectory(folder);
        await Task.Delay(300);
        await CaptureElement(root, "working-release");
        Store.Atomic(Path.Combine(Store.Data, "release-ui-checks.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            Passed = passed.Count,
            ProfilesValidated = validatedProfiles,
            Preview = Entry.Preview,
            TUNStarted = engine.Running,
            Tests = passed
        }, Store.JsonOptions));
    }
}
