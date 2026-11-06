namespace FlowVPN;

internal static class LocalPathChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (string path in new[] { @"\\server.invalid\share\app.exe", @"//server.invalid/share/app.exe", @"\\?\C:\app.exe", @"\\.\C:\app.exe", @"\??\C:\app.exe", @"C:app.exe", @"\app.exe", "app.exe", @"C:\app.exe:stream", @"C:\NUL.txt", @"C:\COM1", @"C:\COM¹", @"C:\NUL .txt", @"C:\app?.exe" })
        {
            check(!LocalPathPolicy.TryNormalize(path, out _), "Local discovery rejects unsafe path syntax: " + path);
            check(!LocalPathPolicy.TryFile(path, out _) && !LocalPathPolicy.TryDirectory(path, out _), "Unsafe paths are rejected before file or directory discovery: " + path);
        }
        check(!LocalPathPolicy.IsLocalDriveType(DriveType.Network) && !LocalPathPolicy.IsLocalDriveType(DriveType.Unknown)
            && !LocalPathPolicy.IsLocalDriveType(DriveType.NoRootDirectory), "Mapped network and unavailable drive types are rejected");

        string root = Path.Combine(Store.Run, "local-path-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string folder = Path.Combine(root, "Приложение with spaces");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "application.exe");
            File.WriteAllText(file, "Synthetic discovery fixture; never executed.");
            check(LocalPathPolicy.TryFile(file, out string actualFile) && actualFile == file, "Normal local executable paths with Unicode and spaces are preserved");
            check(LocalPathPolicy.TryDirectory(folder, out string actualDirectory, allowLocalLinks: false) && actualDirectory == folder, "Normal local discovery roots remain available");
            check(LocalPathPolicy.TryFile(Path.Combine(folder, ".", "application.exe"), out actualFile) && actualFile == file, "Local paths are canonicalized before metadata reads");
            check(!LocalPathPolicy.TryFile(folder, out _) && !LocalPathPolicy.TryDirectory(file, out _), "File and directory discovery do not accept the wrong entry type");
            check(!LocalPathPolicy.TryFile(Path.Combine(folder, "missing.exe"), out _), "Missing applications do not become available presets");
            check(LocalPathPolicy.TryFile(Environment.ProcessPath, out _), "The current installed or developer executable passes local discovery");
        }
        finally { Directory.Delete(root, true); }
    }
}
