using System.Diagnostics;

namespace FlowVPN;

internal static class CoreLaunchChecks
{
    public static void Run(Action<bool, string> check)
    {
        bool shellRejected = false, commandRejected = false;
        try { using var _ = CoreIntegrity.Start(new ProcessStartInfo { UseShellExecute = true }); }
        catch (InvalidOperationException) { shellRejected = true; }
        try { using var _ = CoreIntegrity.Start(new ProcessStartInfo { Arguments = "version" }); }
        catch (InvalidOperationException) { commandRejected = true; }
        check(shellRejected && commandRejected, "Core launcher rejects shell execution and unstructured command lines before opening files");

        string fixture = Path.Combine(Store.Run, "core-path-check-" + Guid.NewGuid().ToString("N"));
        string child = Path.Combine(fixture, "core"), moved = fixture + "-moved";
        Directory.CreateDirectory(child);
        try
        {
            using (CoreIntegrity.LockDirectories(child))
            {
                bool blocked = false;
                try { Directory.Move(fixture, moved); }
                catch (IOException) { blocked = true; }
                check(blocked, "A leased core ancestor cannot be renamed while a child process uses its files");
                string data = Path.Combine(fixture, "ordinary-data.txt");
                File.WriteAllText(data, "fixture");
                check(File.ReadAllText(data) == "fixture", "Directory leases allow ordinary child-file creation and access");
                File.Delete(data);
            }
            Directory.Move(fixture, moved);
            Directory.Move(moved, fixture);
            check(true, "Disposing a core lease releases directory rename protection");
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
            if (Directory.Exists(moved)) Directory.Delete(moved, true);
        }

        var handoff = Entry.ElevationArguments(123, "S-1-5-21-1-2-3-1001");
        check(handoff.Contains("--handoff-user-sid") && handoff.Contains("S-1-5-21-1-2-3-1001"), "Elevation retains the same-user identity check");
#if FLOWVPN_PACKAGE
        bool rootRejected = false;
        try { Entry.ConfigureRoot(["--root", Path.GetTempPath()]); }
        catch (ArgumentException) { rootRejected = true; }
        check(rootRejected && Store.Root == AppContext.BaseDirectory && !handoff.Contains("--root"), "MSIX rejects engine-root overrides and does not forward them across UAC");
#else
        check(handoff.Take(2).SequenceEqual(new[] { "--root", Store.Root }), "Portable isolated checks retain their explicitly selected root across elevation");
        string originalRoot = Store.Root, originalBinary = Store.Bin, originalCoreDirectory = Store.CoreDirectory;
        string dataRoot = Path.Combine(Store.Run, "data-root-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "core"));
        File.WriteAllText(Path.Combine(dataRoot, "core", "sing-box.exe"), "This fixture must never become an executable path.");
        try
        {
            Entry.ConfigureRoot(["--root", dataRoot]);
            check(Store.Data == Path.Combine(dataRoot, "data") && Store.Bin == originalBinary && Store.CoreDirectory == originalCoreDirectory,
                "Portable root overrides isolate data without changing the bundled engine path");
            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("version");
            using var core = CoreIntegrity.Start(start);
            bool exited = core.Process.WaitForExit(5000);
            check(exited && core.Process.ExitCode == 0 && start.FileName == originalBinary && start.WorkingDirectory == originalCoreDirectory,
                "A fake engine in the data root cannot replace the bundled executable or its working directory");
        }
        finally
        {
            Store.Root = originalRoot;
            Directory.Delete(dataRoot, true);
        }
#endif
    }
}
