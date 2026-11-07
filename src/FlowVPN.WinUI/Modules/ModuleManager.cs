using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FlowVPN;

internal enum ModuleState { Missing, Installed, Corrupt, Unavailable, Installing, InUse }
internal enum ModuleProgressStage { Downloading, Verifying, Installing }
internal sealed record ModuleStatus(ModuleDefinition Definition, ModuleState State, string? Detail = null);
internal sealed record ModuleProgress(string ModuleId, long BytesReceived, long TotalBytes, ModuleProgressStage Stage = ModuleProgressStage.Downloading)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(BytesReceived * 100 / TotalBytes, 0, Stage == ModuleProgressStage.Downloading ? 99 : 100);
}

internal sealed class ModuleLease : IDisposable
{
    ModuleFiles? files;
    Action? released;
    public string ExecutablePath { get; }
    internal ModuleLease(string executablePath, ModuleFiles files, Action released) =>
        (ExecutablePath, this.files, this.released) = (executablePath, files, released);
    public void Dispose()
    {
        Interlocked.Exchange(ref files, null)?.Dispose();
        Interlocked.Exchange(ref released, null)?.Invoke();
    }
}

/// <summary>Downloads and verifies optional, unelevated modules. It never launches downloaded code.</summary>
internal sealed class ModuleManager
{
    static readonly Lazy<ModuleManager> current = new(() => new(Path.Combine(Store.Data, "modules"), ModuleCatalog.Current,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }));
    public static ModuleManager Current => current.Value;
    readonly string root;
    readonly HttpClient client;
    readonly SemaphoreSlim mutation = new(1, 1);
    readonly object sync = new();
    readonly Dictionary<string, int> active = new(StringComparer.Ordinal);
    ModuleProgress? downloadProgress;
    public IReadOnlyList<ModuleDefinition> Modules { get; }
    public event EventHandler? Changed;
    public ModuleProgress? DownloadProgress { get { lock (sync) return downloadProgress; } }

    internal ModuleManager(string moduleRoot, IModuleCatalog catalog, HttpClient client)
    {
        root = Path.GetFullPath(moduleRoot);
        // Revalidate and copy custom providers too; the runtime accepts no mutable or remote metadata.
        Modules = new ModuleCatalog(catalog.Modules).Modules;
        this.client = client;
    }
    public ModuleDefinition? FindByCapability(string capability) => Modules.FirstOrDefault(entry => entry.Capabilities.Contains(capability, StringComparer.Ordinal));
    ModuleDefinition Find(string id) => Modules.FirstOrDefault(entry => entry.Id == id)
        ?? throw new InvalidDataException("Неизвестный модуль.");
    string Folder(ModuleDefinition definition) => Path.Combine(root, definition.Id, definition.Version);
    bool InUse(string id) { lock (sync) return active.GetValueOrDefault(id) > 0; }
    void Notify()
    {
        // UI observers must not interrupt verified placement or resource cleanup.
        if (Changed is { } changed)
            foreach (EventHandler observer in changed.GetInvocationList())
                try { observer(this, EventArgs.Empty); } catch (Exception) { }
    }

    public async Task<ModuleStatus> GetStatusAsync(string id, CancellationToken token = default)
    {
        var definition = Find(id);
        if (!definition.CanDownload || !ModuleCatalog.IsCompatible(definition)) return new(definition, ModuleState.Unavailable);
        if (DownloadProgress?.ModuleId == id) return new(definition, ModuleState.Installing);
        if (InUse(id)) return new(definition, ModuleState.InUse);
        await mutation.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var files = ModuleFiles.Open(Folder(definition), false);
                files.Verify(definition, token);
                return new ModuleStatus(definition, ModuleState.Installed);
            }
            catch (FileNotFoundException) { return new(definition, ModuleState.Missing); }
            catch (DirectoryNotFoundException) { return new(definition, ModuleState.Missing); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { return new(definition, ModuleState.Corrupt, "Файл модуля повреждён или недоступен. Установите модуль повторно."); }
        }, token).ConfigureAwait(false); }
        finally { mutation.Release(); }
    }

    public async Task InstallAsync(string id, IProgress<ModuleProgress>? progress = null, CancellationToken token = default)
    {
        var definition = Find(id);
        if (!definition.CanDownload || !ModuleCatalog.IsCompatible(definition))
            throw new InvalidOperationException("Совместимый модуль пока недоступен для скачивания.");
        await mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (InUse(id)) throw new InvalidOperationException("Сначала отключите соединение, использующее этот модуль.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            await Task.Run(async () =>
            {
                Report(0);
                using var files = ModuleFiles.Open(Folder(definition), true);
                using var response = await OpenDownload(definition.DownloadUrl, deadline.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength is long length && length != definition.SizeBytes)
                    throw new InvalidDataException("Размер загрузки не совпадает с каталогом модуля.");
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var temporary = files.CreateTemporary();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[64 * 1024];
                long received = 0;
                int lastPercent = -1;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > definition.SizeBytes) throw new InvalidDataException("Загрузка превышает закреплённый размер модуля.");
                    await temporary.Stream.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    int percent = (int)(received * 100 / definition.SizeBytes);
                    if (percent != lastPercent) { Report(received); lastPercent = percent; }
                }
                Report(received, ModuleProgressStage.Verifying);
                if (received != definition.SizeBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(definition.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Модуль не прошёл проверку размера и SHA-256.");
                deadline.Token.ThrowIfCancellationRequested();
                Report(received, ModuleProgressStage.Installing);
                deadline.Token.ThrowIfCancellationRequested();
                temporary.Stream.Flush(true);
                files.PreparePlacement();
                temporary.Commit(Path.Combine(Folder(definition), definition.ExecutableName));
                files.FinishPlacement();
            }, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (sync) downloadProgress = null;
            mutation.Release();
            Notify();
        }
        void Report(long bytes, ModuleProgressStage stage = ModuleProgressStage.Downloading)
        {
            var value = new ModuleProgress(id, bytes, definition.SizeBytes, stage);
            lock (sync) downloadProgress = value;
            try { progress?.Report(value); } catch (Exception) { } // Progress is observational, not part of the transaction.
            Notify();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken token = default)
    {
        var definition = Find(id);
        await mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (InUse(id)) throw new InvalidOperationException("Модуль используется подключением. Сначала отключитесь.");
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var files = ModuleFiles.Open(Folder(definition), false);
                    // Delete only the known executable, by its checked handle; never recurse through user data.
                    files.Delete(definition.ExecutableName);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }, token).ConfigureAwait(false);
        }
        finally { mutation.Release(); Notify(); }
    }

    public async Task<ModuleLease> AcquireAsync(string id, CancellationToken token = default)
    {
        var definition = Find(id);
        if (!definition.CanDownload || !ModuleCatalog.IsCompatible(definition)) throw new InvalidOperationException("Модуль недоступен.");
        await mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var files = await Task.Run(() =>
            {
                var opened = ModuleFiles.Open(Folder(definition), false);
                try { opened.Verify(definition, token); return opened; }
                catch { opened.Dispose(); throw; }
            }, token).ConfigureAwait(false);
            lock (sync) active[id] = active.GetValueOrDefault(id) + 1;
            return new(Path.Combine(Folder(definition), definition.ExecutableName), files, () =>
            {
                lock (sync) if (--active[id] == 0) active.Remove(id);
                Notify();
            });
        }
        finally { mutation.Release(); Notify(); }
    }

    async Task<HttpResponseMessage> OpenDownload(string url, CancellationToken token)
    {
        var uri = new Uri(url);
        for (int redirect = 0; redirect < 5; redirect++)
        {
            ValidateDownloadUri(uri);
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = new Uri(uri, location);
                    response.Dispose();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Слишком много перенаправлений при скачивании модуля.");
    }
    internal static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("Сервер перенаправил загрузку за пределы разрешённых хранилищ GitHub.");
    }
}

// Every component is opened top-down with OPEN_REPARSE_POINT and without write/delete sharing.
// Handles, rather than preflight File.GetAttributes calls, define the checked filesystem objects.
internal sealed class ModuleFiles : IDisposable
{
    readonly List<SafeFileHandle> directories = [];
    readonly string folder;
    FileStream? executable;
    ModuleFiles(string folder) => this.folder = folder;
    internal static ModuleFiles Open(string folder, bool create)
    {
        var lease = new ModuleFiles(Path.GetFullPath(folder));
        try
        {
            string root = Path.GetPathRoot(lease.folder)!;
            if (root.Length != 3 || root[1] != ':' || new DriveInfo(root).DriveType != DriveType.Fixed)
                throw new InvalidDataException("Модули должны храниться на локальном фиксированном диске.");
            lease.directories.Add(OpenChecked(root, true));
            string current = root;
            foreach (string component in lease.folder[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (create) Directory.CreateDirectory(current); // The parent is already pinned against retargeting.
                lease.directories.Add(OpenChecked(current, true));
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    internal void Verify(ModuleDefinition definition, CancellationToken token)
    {
        var handle = OpenChecked(Path.Combine(folder, definition.ExecutableName), false);
        try { executable = new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
        if (executable.Length != definition.SizeBytes) throw new InvalidDataException("Размер модуля изменён.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int count;
        while ((count = executable.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        token.ThrowIfCancellationRequested();
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(definition.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 модуля изменён.");
    }
    internal TemporaryModule CreateTemporary() => new(Path.Combine(folder, ".download-" + Guid.NewGuid().ToString("N") + ".partial"));
    internal void PreparePlacement()
    {
        // Windows needs a writable immediate directory during rename. The exclusively open temporary
        // file pins its original parent against removal; native basename-only rename below cannot be
        // redirected by changing a pathname. All ancestors remain locked and the leaf is rechecked after.
        directories[^1].Dispose();
        directories.RemoveAt(directories.Count - 1);
    }
    internal void FinishPlacement() => directories.Add(OpenChecked(folder, true));
    internal void Delete(string name)
    {
        using var handle = OpenChecked(Path.Combine(folder, name), false, deleteAccess: true);
        MarkDeleted(handle);
    }
    internal static SafeFileHandle OpenChecked(string path, bool directory, bool deleteAccess = false, bool create = false)
    {
        const uint readAttributes = 0x80, genericRead = 0x80000000, genericWrite = 0x40000000, delete = 0x10000;
        // READ_ATTRIBUTES alone does not participate in Windows share-access checks. LIST_DIRECTORY
        // makes FileShare.Read actually reject another writer capable of FSCTL_SET_REPARSE_POINT.
        var handle = CreateFile(path, (directory ? readAttributes | 1u : genericRead) | (deleteAccess ? delete : 0) | (create ? genericWrite : 0),
            FileShare.Read, IntPtr.Zero, create ? 1u : 3u, 0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 2) throw new FileNotFoundException("Файл модуля отсутствует.");
            if (error == 3) throw new DirectoryNotFoundException("Каталог модуля отсутствует.");
            throw new IOException("Не удалось защитить путь модуля от изменения.", new Win32Exception(error));
        }
        try
        {
            if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeInfo>()))
                throw new IOException("Не удалось проверить путь модуля.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || directory != ((info.Attributes & FileAttributes.Directory) != 0))
                throw new InvalidDataException("Путь модуля не должен содержать ссылки или точки подключения.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static void MarkDeleted(SafeFileHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(handle, 4, buffer, 1))
                throw new IOException("Не удалось удалить файл модуля.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static void Rename(SafeFileHandle handle, string target)
    {
        // A basename and NULL RootDirectory mean the source handle's existing parent, not process CWD.
        // https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information
        byte[] name = Encoding.Unicode.GetBytes(Path.GetFileName(target));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        int size = nameOffset + name.Length + 8;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteByte(buffer, 1); // FILE_RENAME_INFO.ReplaceIfExists.
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            int status = NtSetInformationFile(handle, out _, buffer, (uint)size, 10);
            if (status != 0)
                throw new IOException("Не удалось атомарно разместить модуль.", new Win32Exception((int)RtlNtStatusToDosError(status)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose()
    {
        executable?.Dispose();
        executable = null;
        for (int index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
        directories.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] struct AttributeInfo { public FileAttributes Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr info, uint length, int informationClass);
    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr info, uint size);
}

internal sealed class TemporaryModule : IDisposable
{
    public FileStream Stream { get; }
    bool committed;
    internal TemporaryModule(string path)
    {
        var handle = ModuleFiles.OpenChecked(path, false, deleteAccess: true, create: true);
        try { Stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1); }
        catch { handle.Dispose(); throw; }
    }
    internal void Commit(string target) { ModuleFiles.Rename(Stream.SafeFileHandle, target); committed = true; }
    public void Dispose()
    {
        try { if (!committed) ModuleFiles.MarkDeleted(Stream.SafeFileHandle); }
        finally { Stream.Dispose(); }
    }
}
