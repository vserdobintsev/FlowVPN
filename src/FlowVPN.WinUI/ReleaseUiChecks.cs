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
            var categories = BaseDomainSelection.From(settings).Categories;
            Check(categories.Count == 0 || DomainRepository.Current(categories) != null, "Selected domain categories are available or domain policy is explicitly empty");
        }
        int validatedProfiles = 0;
        foreach (var profile in settings.Profiles)
        {
            Directory.CreateDirectory(Store.ProfileLogs(profile.Id));
            var snapshot = ProfileValidator.Capture(settings, profile.Id);
            var configuration = await ProfileValidator.BuildConfiguration(snapshot, new("Release validation", "127.0.0.1"), windowLifetime.Token);
            // Check the saved profile over stdin; never create a TUN or contact its server.
            await CoreCli.Check(configuration, windowLifetime.Token);
            validatedProfiles++;
        }
        Check(validatedProfiles == settings.Profiles.Count, "All saved profiles pass the bundled core configuration check");
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
