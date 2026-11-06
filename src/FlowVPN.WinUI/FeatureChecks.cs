using System.Text.Json;
using System.Text.RegularExpressions;
namespace FlowVPN;

internal static class FeatureChecks
{
    public static void Run(List<DetectedPreset> presets)
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new Exception("Feature check failed: " + name);
            passed.Add(name);
        }
        SecurityChecks.Run(Check);
        ImportChecks.Run(Check);
        ManualConnectionChecks.Run(Check);
        LocalPathChecks.Run(Check);
        Check(TaskbarState.From(false, false, false, true) is { Running: false, Enabled: true } && TaskbarState.From(false, true, false, false) is { Running: true, Enabled: true }, "One taskbar command toggles between connect and disconnect");
        Check(!TaskbarState.From(true, false, false, true).Enabled && !TaskbarState.From(false, false, true, true).Enabled && !TaskbarState.From(false, false, false, false).Enabled, "Taskbar command is disabled in preview, during transitions and without a source");
        Check(System.Runtime.InteropServices.Marshal.SizeOf<TaskbarControls.ThumbButton>() == 552, "Native thumbnail button layout matches the Windows x64 ABI");
        var domains = DomainInput.Parse("HTTPS://WWW.Example.COM:8443/path?q=1#part\nwww.example.com\n*.sub.example.org\n//пример.рф/страница\n[Site](https://example.net/a)\n");
        Check(domains.InvalidLines.Count == 0 && domains.Domains.SequenceEqual(new[] { "www.example.com", "sub.example.org", "xn--e1afmkfd.xn--p1ai", "example.net" }), "URLs, ports, IDN, wildcard and Markdown normalize; duplicate removed");
        var invalid = DomainInput.Parse("not a domain\nhttps://good.example/path\nhttps://-bad.example\n127.0.0.1");
        Check(invalid.InvalidLines.SequenceEqual(new[] { 1, 3, 4 }) && invalid.Lines[0] == "not a domain", "Invalid lines stay visible and are numbered");
        Check(DomainInput.Parse("a.example.com\nb.example.com").Domains.Count == 2, "Different subdomains remain distinct");
        Check(DomainInput.Parse("").Domains.Count == 0 && DomainInput.Parse("example.com:99999").InvalidLines.Count == 1, "Empty editor allowed; invalid port rejected");

        var other = new Dictionary<string, string> { ["LocalAppData"] = @"E:\Users\Тест User\Local", ["UserProfile"] = @"D:\Пользователи\Example" };
        string path = AppPresets.Expand(@"{LocalAppData}\OpenAI\Codex", other);
        Check(path == @"E:\Users\Тест User\Local\OpenAI\Codex", "Known folder expansion supports other drive, Cyrillic and spaces");
        var folderRule = AppRule.Folder(path);
        Check(Regex.IsMatch(path + @"\v1\helper.exe", folderRule.Pattern) && !Regex.IsMatch(path + @"-other\helper.exe", folderRule.Pattern), "Portable component rule preserves directory boundaries");
        Check(presets.Select(p => p.Id).Distinct().Count() == presets.Count, "Preset identifiers are unique");
        Check(presets.SelectMany(p => p.Components).Where(c => c.Available).All(c => File.Exists(c.Executable)), "Every suggested component has an existing executable");
        Check(presets.Where(p => !p.Available).All(p => p.Rules.Count == 0), "Missing programs never produce suggested rules");
        var emptyDir = Path.Combine(Store.Run, "empty-preset-check");
        Directory.CreateDirectory(emptyDir);
        Check(AppPresets.FindExecutable(emptyDir, "", true).Length == 0, "An empty leftover directory is not an installed component");
        Directory.Delete(emptyDir);
        var definition = new PresetDefinition { Id = "test", Name = "Test application" };
        var a = AppRule.Folder(@"D:\Test App\Main", "Main");
        var b = AppRule.Folder(@"E:\Test App\Tools", "Tools");
        a.GroupId = b.GroupId = "preset:test";
        a.GroupName = b.GroupName = definition.Name;
        var fake = new DetectedPreset(definition, [new(a, ""), new(b, "")]);
        var legacyA = AppRule.Folder(a.Path, "Old label");
        var legacyB = AppRule.Folder(b.Path, "Other label");
        var unrelated = AppRule.File(@"E:\Elsewhere\Test.exe");
        unrelated.Name = definition.Name;
        var groups = ApplicationGroups.Create([legacyA, legacyB, unrelated], [fake]);
        Check(groups.Count == 2 && groups[0].Rules.Count == 2 && groups[1].Rules.Count == 1, "Legacy components grouped by known path, unrelated names kept separate");
        var profile = new VpnProfile { Apps = [legacyA] };
        string originalPattern = legacyA.Pattern;
        ApplicationGroups.Add(profile, [a, b]);
        ApplicationGroups.Add(profile, [a, b]);
        Check(profile.Apps.Count == 2 && legacyA.Pattern == originalPattern && legacyA.GroupId == "preset:test", "Adding a partial preset fills gaps and preserves existing routing");
        var restored = JsonSerializer.Deserialize<VpnProfile>(JsonSerializer.Serialize(profile))!;
        Check(ApplicationGroups.Create(restored.Apps, []).Count == 1, "Grouping survives saving and loading");
        restored.Apps.Remove(restored.Apps[0]);
        Check(ApplicationGroups.Create(restored.Apps, []).Single().Rules.Count == 1, "Removing one component preserves its group");
        Check(Store.ProfileLogs("profile-one") != Store.ProfileLogs("profile-two"), "Profile journals use separate directories");
        Check(Store.ProfileLogs(@"..\escape").StartsWith(Path.Combine(Store.Data, "logs", "profiles") + Path.DirectorySeparatorChar), "Profile log identifiers cannot escape the log directory");
        Check(new[] { "kilo", "opencode", "nvidia-app" }.All(id => AppPresets.Definitions.Any(p => p.Id == id)), "Three new application families exist in the catalogue");
        Check(presets.All(p => p.Components.Where(c => c.Available).Select(c => c.Executable.ToUpperInvariant() + c.Rule.ComponentRole).Distinct().Count() == p.Components.Count(c => c.Available)), "PATH and registration never duplicate the same detected component");
        var extensionComponent = new PresetComponent { Executable = "kilo.exe", FileOnly = true, Role = "engine" };
        var extensionRule = PresetDiscovery.Rule(@"D:\Users\Тест\.vscode\extensions\kilocode.kilo-code-*\bin", @"D:\Users\Тест\.vscode\extensions\kilocode.kilo-code-1\bin", @"D:\Users\Тест\.vscode\extensions\kilocode.kilo-code-1\bin\kilo.exe", extensionComponent);
        Check(Regex.IsMatch(@"D:\Users\Тест\.vscode\extensions\kilocode.kilo-code-2\bin\kilo.exe", extensionRule.Pattern)
            && !Regex.IsMatch(@"D:\Users\Тест\.vscode\extensions\unrelated\bin\kilo.exe", extensionRule.Pattern)
            && !Regex.IsMatch(@"D:\Users\Тест\.vscode\extensions\kilocode.kilo-code-2\bin\node.exe", extensionRule.Pattern), "Extension engine rule survives version changes without capturing other extensions or Node");
        Check(PresetDiscovery.NormalizeComponents([new(extensionRule, @"D:\v1\kilo.exe"), new(extensionRule, @"D:\v2\kilo.exe")]).Count == 1, "Several installed versions still produce one version-independent component");
        var onlyCli = PresetDiscovery.Rule(@"D:\Tools", @"D:\Tools", @"D:\Tools\opencode.exe", new PresetComponent { FileOnly = true, Executable = "opencode.exe" });
        Check(Regex.IsMatch(@"D:\Tools\opencode.exe", onlyCli.Pattern) && !Regex.IsMatch(@"D:\Tools\other.exe", onlyCli.Pattern), "CLI in shared PATH directory creates only a file rule");
        Check(!PresetDiscovery.RequirementsMet(new PresetComponent { RequiresAnyFile = [@"{UserProfile}\missing-test-12345\package.json"] }, other), "Extension integration requires an installed extension");
        var isolated = AppRule.File(@"D:\Test\kilo.exe");
        var shared = AppRule.File(@"D:\Test\Code.exe");
        shared.ComponentRole = "extension";
        shared.ScopeNote = "Entire application";
        var sampleApp = new CatalogApp { Name = "Kilo", Path = isolated.Path, PresetId = "selection-test", Rules = [isolated, shared], SuggestedPatterns = [isolated.Pattern] };
        var selection = new ApplicationSelection(new VpnProfile());
        selection.SetApplication(sampleApp, true);
        Check(selection.Components == 1 && selection.Contains(sampleApp, isolated) && !selection.Contains(sampleApp, shared), "Suggested engine does not silently select the optional host application");
        selection.SetComponent(sampleApp, shared, true);
        selection.SetComponent(sampleApp, isolated, false);
        Check(selection.Count == 1 && selection.Components == 1 && selection.Contains(sampleApp, shared), "Application components can be selected independently");
        Check(selection.Resolve([new(new PresetDefinition { Id = "selection-test" }, [new(isolated, isolated.Path)])]).Count == 0, "Removed components are rejected by fresh discovery before saving");
        selection.SetApplication(sampleApp, false);
        Check(selection.Count == 0, "Deselecting an application clears its components");
        var metadata = JsonSerializer.Deserialize<AppRule>(JsonSerializer.Serialize(shared))!;
        Check(metadata.ComponentRole == "extension" && metadata.ScopeNote == shared.ScopeNote && metadata.Pattern == shared.Pattern, "Component meaning survives persistence without changing routing");
        var fakeDefinitions = new List<PresetDefinition> { new() { Id = "some-player", Name = "Player" }, new() { Id = "some-addon", Name = "Addon", ParentId = "some-player" } };
        PresetHierarchy.Validate(fakeDefinitions);
        var fakeHost = new CatalogApp { PresetId = "some-player", Name = "Player", Rules = [shared] };
        var fakeAddon = new CatalogApp { PresetId = "some-addon", ParentId = "some-player", Name = "Addon", Rules = [isolated] };
        var browser = new PresetBrowser([fakeHost, fakeAddon]);
        Check(browser.Visible("").Count == 1 && browser.Enter(fakeHost.PresetId) && browser.Visible("").Count == 2, "Any application can contain extensions without product-specific navigation");
        browser.Back();
        Check(browser.ParentId.Length == 0 && browser.Visible("Addon").Single() == fakeAddon, "Back returns to root; global search finds nested extensions");
        Check(PresetHierarchy.QualifiedName(fakeDefinitions[1], fakeDefinitions) == "Addon · Player", "Saved group names preserve the parent application");
        void RejectHierarchy(List<PresetDefinition> definitions, string name)
        {
            try
            {
                PresetHierarchy.Validate(definitions);
            }
            catch (InvalidDataException) { passed.Add(name); return; }
            throw new Exception(name);
        }
        RejectHierarchy([new() { Id = "a", ParentId = "b" }, new() { Id = "b", ParentId = "a" }], "Cyclic catalogue rejected");
        RejectHierarchy([new() { Id = "a", ParentId = "missing" }], "Orphaned extension rejected");
        var codexDefinition = AppPresets.Definitions.Single(p => p.Id == "codex-vscode");
        var codexTemplate = AppPresets.Expand(codexDefinition.Components[0].Locations.Single(), new Dictionary<string, string> { ["UserProfile"] = @"E:\Users\Another User" });
        var codexRule = PresetDiscovery.Rule(codexTemplate, "", "", codexDefinition.Components[0]);
        Check(Regex.IsMatch(@"E:\Users\Another User\.vscode\extensions\openai.chatgpt-99\bin\windows-x86_64\codex.exe", codexRule.Pattern)
            && Regex.IsMatch(@"E:\Users\Another User\.vscode\extensions\openai.chatgpt-99\bin\windows-x86_64\codex-resources\voice\bin\codex-voice-host.exe", codexRule.Pattern)
            && !Regex.IsMatch(@"E:\Users\Another User\.vscode\extensions\other.addon-99\bin\windows-x86_64\codex.exe", codexRule.Pattern)
            && !Regex.IsMatch(@"C:\Apps\VS Code\Code.exe", codexRule.Pattern), "Codex extension includes its own tools across versions and excludes the host and other extensions");
        var migratedRule = AppRule.File(@"D:\old\engine.exe");
        migratedRule.GroupId = "preset:old-family";
        var original = migratedRule.Pattern;
        var replacement = AppRule.File(migratedRule.Path);
        replacement.GroupId = "preset:new-addon";
        replacement.GroupName = "Addon · Player";
        var migrationCatalog = new List<DetectedPreset> { new(new PresetDefinition { Id = "new-addon", LegacyGroupIds = ["preset:old-family"] }, [new(replacement, replacement.Path)]) };
        CatalogRuleMigration.Apply([migratedRule], migrationCatalog);
        var migrated = ApplicationGroups.Create([migratedRule], migrationCatalog);
        Check(migrated.Single().Name == "Addon · Player" && migratedRule.Pattern == original, "Legacy family moves to its new parent without changing network routing");
        OnboardingChecks.Run(Check);
        ProfileSetChecks.Run(Check);
        File.WriteAllText(Path.Combine(Store.Root, "design-preview", "preset-discovery.json"), JsonSerializer.Serialize(presets.Select(p => new { p.Definition.Name, p.Available, Components = p.Components.Where(c => c.Available).Select(c => new { c.Rule.Name, c.Rule.ComponentRole, c.Suggested, c.Rule.Path }) }), Store.JsonOptions));
        AuditChecks.Run(Check);
        DomainRuleChecks.Run(Check);
        ProcessCatalogChecks.Run(Check);
        File.WriteAllText(Path.Combine(Store.Root, "design-preview", "feature-checks.json"), JsonSerializer.Serialize(new
        {
            Passed = passed.Count,
            Tests = passed
        }, Store.JsonOptions));
    }
}
