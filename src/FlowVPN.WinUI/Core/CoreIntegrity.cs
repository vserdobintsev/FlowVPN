using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowVPN;

// Keep verified files open without write/delete sharing until the child starts.
// This detects an altered core; it does not make an otherwise writable app directory trusted.
internal sealed class CoreIntegrity : IDisposable
{
    readonly List<FileStream> files = [];
    public static CoreIntegrity Acquire()
    {
        var lease = new CoreIntegrity();
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlowVPN.engine.lock.json")!;
            using var document = JsonDocument.Parse(resource);
            foreach (var item in document.RootElement.GetProperty("Files").EnumerateObject())
            {
                if (Path.GetFileName(item.Name) != item.Name)
                    throw new InvalidDataException("Некорректное имя файла движка.");
                var file = new FileStream(Path.Combine(Store.Root, "core", item.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
                lease.files.Add(file);
                Verify(file, item.Value.GetString()!);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    internal static void Verify(Stream stream, string expected)
    {
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файлы sing-box изменены или повреждены. Восстановите движок из проверенного выпуска FlowVPN.");
    }
    public void Dispose()
    {
        foreach (var file in files)
            file.Dispose();
        files.Clear();
    }
}
