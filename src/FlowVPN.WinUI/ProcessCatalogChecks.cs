namespace FlowVPN;

internal static class ProcessCatalogChecks
{
    public static void Run(Action<bool, string> check)
    {
        var main = AppRule.Folder(@"E:\Apps\Player", "Player");
        main.GroupId = "preset:player";
        var tools = AppRule.File(@"F:\PlayerTools\engine.exe", "Engine");
        tools.GroupId = main.GroupId;
        var shared = AppRule.File(@"E:\Apps\Shared\host.exe", "Shared host");
        shared.GroupId = main.GroupId;
        var addon = AppRule.Folder(@"E:\Apps\Player\addons\one", "Addon");
        addon.GroupId = "preset:addon";
        List<DetectedPreset> catalog = [new(new() { Id = "player", Name = "Player" }, [new(main, main.Path + @"\player.exe"), new(tools, tools.Path), new(shared, shared.Path, false)]),
            new(new() { Id = "addon", Name = "Addon", ParentId = "player" }, [new(addon, addon.Path + @"\addon.exe")])];
        ProcessSnapshot P(int id, string path, string name, bool window = false) => new(id, path, name, "Example company", "1.2", window ? "Document title" : "", window);
        var processes = new[] { P(1, main.Path + @"\player.exe", "Player", true), P(2, main.Path + @"\player.exe", "Player"),
            P(3, tools.Path, "Engine"), P(4, addon.Path + @"\addon.exe", "Addon"), P(5, @"E:\Unrelated\player.exe", "Player"),
            P(6, @"C:\Windows\System32\system.exe", "System"), P(7, @"C:\WindowsCopy\app.exe", "Other"),
            P(8, @"D:\User\app.exe", "Documents", true), P(9, @"D:\User\helper.exe", "Documents") };
        var groups = ProcessCatalog.Group(processes, catalog, @"C:\Windows");
        var player = groups.Single(g => g.App.PresetId == "player");
        check(player.Processes.Count == 3 && player.App.Rules.Count == 3, "Multiple executable components and PIDs group into one known application, including idle available components");
        check(groups.Single(g => g.App.PresetId == "addon").Processes.Single().Id == 4, "A nested extension has priority over a broader host folder");
        check(groups.Single(g => g.App.Path == @"E:\Unrelated\player.exe").Category == ProcessCategory.Background, "An unrelated executable with the same product name never inherits a preset");
        check(groups.Count(g => g.Category == ProcessCategory.System) == 1, "Windows folder classification respects directory boundaries on any system drive");
        check(groups.Count(g => g.App.Name == "Documents") == 2, "Unknown executables remain separate even within one folder or with identical names");
        check(ProcessCatalog.Filter(groups, ProcessFilter.Applications, "").Count == 3, "Default process view contains known sets and windowed applications");
        check(ProcessCatalog.Filter(groups, ProcessFilter.All, "Example company").Count == groups.Count, "Process search includes company metadata");
        check(ProcessCatalog.Filter(groups, ProcessFilter.All, @"D:\User\helper.exe").Single().Processes.Single().Id == 9, "Process search finds a full executable path");
        check(ProcessCatalog.Filter(groups, ProcessFilter.All, "Document title").Count == 2, "Process search includes window titles");
        check(ProcessCatalog.Filter(groups, ProcessFilter.Known, "").Count == 2 && ProcessCatalog.Filter(groups, ProcessFilter.System, "").Count == 1, "Known and system filters do not mix categories");
        check(ProcessCatalog.Filter(groups, ProcessFilter.All, "9").Single().Processes.Single().Id == 9, "Process search includes PID");
        var selection = new ApplicationSelection(new VpnProfile());
        selection.SetApplication(player.App, true);
        check(selection.Components == 2 && !selection.Contains(player.App, shared), "Known running app selects the same suggested components as the installed-app picker");
        ProcessCatalog.Filter(groups, ProcessFilter.System, "");
        var refreshed = ProcessCatalog.Group(processes, catalog, @"C:\Windows");
        check(selection.Selected(refreshed.Single(g => g.App.Id == player.App.Id).App) == 2, "Filtering and rebuilding a process snapshot preserve selection by stable identity");
        check(ProcessCatalog.Group([], catalog, @"C:\Windows").Count == 0, "An empty process snapshot is valid");
        var changed = ProcessCatalog.Group([P(10, @"D:\User\helper.exe", "Renamed helper", true)], catalog, @"C:\Windows");
        check(ProcessCatalog.Filter(changed, ProcessFilter.All, "Renamed helper").Count == 1 && ProcessCatalog.Filter(groups, ProcessFilter.All, "Renamed helper").Count == 0,
            "The search index is rebuilt for a fresh process snapshot without changing the previous one");
    }
}
