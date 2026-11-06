using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FlowVPN;

// Pin both files and their directory chain: a file handle alone cannot stop a junction swap.
// Portable distributions still require a trusted application directory and executable.
internal sealed class CoreIntegrity : IDisposable
{
    readonly List<FileStream> files = [];
    readonly List<SafeFileHandle> directories = [];
    Process? process;
    public Process Process => process ?? throw new InvalidOperationException("Движок ещё не запущен.");

    public static CoreIntegrity Start(ProcessStartInfo start)
    {
        if (start.UseShellExecute || start.Arguments.Length != 0)
            throw new InvalidOperationException("Движок запускается напрямую с отдельными аргументами.");
        var lease = Acquire();
        try
        {
            start.FileName = Store.Bin;
            start.WorkingDirectory = Store.CoreDirectory;
            lease.process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить sing-box.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public static CoreIntegrity Acquire()
    {
        var lease = new CoreIntegrity();
        try
        {
            lease.LockDirectoryChain(Store.CoreDirectory);
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlowVPN.engine.lock.json")!;
            using var document = JsonDocument.Parse(resource);
            foreach (var item in document.RootElement.GetProperty("Files").EnumerateObject())
            {
                if (Path.GetFileName(item.Name) != item.Name)
                    throw new InvalidDataException("Некорректное имя файла движка.");
                var handle = OpenChecked(Path.Combine(Store.CoreDirectory, item.Name), false);
                FileStream file;
                try { file = new FileStream(handle, FileAccess.Read); }
                catch { handle.Dispose(); throw; }
                lease.files.Add(file);
                Verify(file, item.Value.GetString()!);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    internal static CoreIntegrity LockDirectories(string path)
    {
        var lease = new CoreIntegrity();
        try { lease.LockDirectoryChain(path); return lease; }
        catch { lease.Dispose(); throw; }
    }
    void LockDirectoryChain(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        if (root.Length != 3 || root[1] != ':' || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new InvalidDataException("Движок должен находиться на локальном фиксированном диске.");
        directories.Add(OpenChecked(root, true));
        string current = root;
        foreach (string component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            directories.Add(OpenChecked(current, true));
        }
    }
    static SafeFileHandle OpenChecked(string path, bool directory)
    {
        const uint readAttributes = 0x80, genericRead = 0x80000000;
        const uint openExisting = 3, backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
        var handle = CreateFile(path, directory ? readAttributes : genericRead, FileShare.Read, IntPtr.Zero,
            openExisting, openReparsePoint | (directory ? backupSemantics : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Не удалось защитить путь движка от изменения.", new Win32Exception(error));
        }
        try
        {
            if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                throw new IOException("Не удалось проверить путь движка.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || directory != ((info.Attributes & FileAttributes.Directory) != 0))
                throw new InvalidDataException("Путь движка не должен содержать ссылки, точки подключения или подменённые файлы.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct FileAttributeTagInfo { public FileAttributes Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileAttributeTagInfo info, uint size);
    internal static void Verify(Stream stream, string expected)
    {
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файлы sing-box изменены или повреждены. Восстановите движок из проверенного выпуска FlowVPN.");
    }
    public void Dispose()
    {
        try
        {
            if (process != null && !process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            process?.Dispose();
            process = null;
            foreach (var file in files)
                file.Dispose();
            files.Clear();
            for (int i = directories.Count - 1; i >= 0; i--)
                directories[i].Dispose();
            directories.Clear();
        }
    }
}
