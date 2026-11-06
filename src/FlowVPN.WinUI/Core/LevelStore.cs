using System.Buffers.Binary;
using System.Text;

namespace FlowVPN;
// Read-only Chromium Local Storage snapshot. Handles LevelDB WAL, SST and raw Snappy.
internal static class LevelStore
{
    internal sealed class ReadBudget(long limit = 128L * 1024 * 1024)
    {
        long remaining = limit;
        public void Consume(long bytes)
        {
            if (bytes < 0 || bytes > remaining)
                throw new InvalidDataException("Превышен бюджет разбора хранилища Outline.");
            remaining -= bytes;
        }
    }
    sealed record Entry(ulong Sequence, byte Type, byte[] Value);
    static ulong Var(byte[] b, ref int p)
    {
        ulong v = 0;
        for (int s = 0; s < 64; s += 7)
        {
            if (p >= b.Length)
                throw new InvalidDataException();
            byte c = b[p++];
            if (s == 63 && (c & 0xfe) != 0)
                throw new InvalidDataException();
            v |= (ulong)(c & 127) << s;
            if (c < 128)
                return v;
        }
        throw new InvalidDataException();
    }
    static byte[] Slice(byte[] b, ref int p, int n, ReadBudget budget)
    {
        if (n < 0 || n > b.Length - p)
            throw new InvalidDataException();
        budget.Consume(n);
        var r = b.AsSpan(p, n).ToArray();
        p += n;
        return r;
    }
    static byte[] Read(string p, ReadBudget budget)
    {
        using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (f.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("Слишком большой файл хранилища Outline.");
        budget.Consume(f.Length);
        var b = new byte[(int)f.Length];
        f.ReadExactly(b);
        return b;
    }
    internal static byte[] Snappy(byte[] input, ReadBudget budget)
    {
        int position = 0;
        ulong declared = Var(input, ref position);
        if (declared > 64 * 1024 * 1024)
            throw new InvalidDataException("Слишком большой распакованный блок Outline.");
        int length = (int)declared;
        budget.Consume(length);
        var output = new byte[length];
        int written = 0;
        void Need(int count)
        {
            if (count < 0 || count > input.Length - position)
                throw new InvalidDataException("Оборванный блок Snappy.");
        }
        while (written < length)
        {
            Need(1);
            int tag = input[position++], kind = tag & 3, count, offset;
            if (kind == 0)
            {
                count = (tag >> 2) + 1;
                if (count > 60)
                {
                    int bytes = count - 60;
                    Need(bytes);
                    uint encoded = 0;
                    for (int i = 0; i < bytes; i++)
                        encoded |= (uint)input[position++] << (8 * i);
                    if (encoded >= int.MaxValue)
                        throw new InvalidDataException("Некорректная длина Snappy.");
                    count = (int)encoded + 1;
                }
                Need(count);
                if (count > length - written)
                    throw new InvalidDataException();
                input.AsSpan(position, count).CopyTo(output.AsSpan(written));
                position += count;
                written += count;
                continue;
            }
            if (kind == 1)
            {
                Need(1);
                count = 4 + ((tag >> 2) & 7);
                offset = ((tag & 224) << 3) | input[position++];
            }
            else if (kind == 2)
            {
                Need(2);
                count = 1 + (tag >> 2);
                offset = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(position));
                position += 2;
            }
            else
            {
                Need(4);
                count = 1 + (tag >> 2);
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(position));
                position += 4;
                if (value > int.MaxValue)
                    throw new InvalidDataException();
                offset = (int)value;
            }
            if (offset <= 0 || offset > written || count > length - written)
                throw new InvalidDataException();
            // Snappy back-references may overlap; copying the whole span at once would change the result.
            for (int i = 0; i < count; i++)
            {
                output[written] = output[written - offset];
                written++;
            }
        }
        if (position != input.Length)
            throw new InvalidDataException("Лишние данные после блока Snappy.");
        return output;
    }
    static byte[] Block(byte[] file, ulong offset, ulong length, ReadBudget budget)
    {
        int a = checked((int)offset), n = checked((int)length);
        if (a < 0 || n < 0 || (long)a + n + 5 > file.Length)
            throw new InvalidDataException();
        budget.Consume(n);
        var b = file.AsSpan(a, n).ToArray();
        return file[a + n] switch
        {
            0 => b,
            1 => Snappy(b, budget),
            _ => throw new InvalidDataException("Неизвестное сжатие LevelDB.")
        };
    }
    static IEnumerable<(byte[] Key, byte[] Value)> Pairs(byte[] block, ReadBudget budget)
    {
        if (block.Length < 4)
            yield break;
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4)));
        if (count > (block.Length - 4) / 4)
            throw new InvalidDataException();
        int end = block.Length - 4 - count * 4, p = 0;
        byte[] prev = [];
        while (p < end)
        {
            int shared = checked((int)Var(block, ref p)), fresh = checked((int)Var(block, ref p)), size = checked((int)Var(block, ref p));
            if (shared > prev.Length || fresh > end - p)
                throw new InvalidDataException();
            if ((long)shared + fresh > 4096 || size > end - p - fresh)
                throw new InvalidDataException();
            budget.Consume(shared + fresh);
            var key = new byte[shared + fresh];
            prev.AsSpan(0, shared).CopyTo(key);
            Slice(block, ref p, fresh, budget).CopyTo(key, shared);
            var value = Slice(block, ref p, size, budget);
            prev = key;
            yield return (key, value);
        }
    }
    static void Put(Dictionary<string, Entry> map, byte[] key, ulong seq, byte type, byte[] value)
    {
        if (key.Length > 4096)
            throw new InvalidDataException("Ключ хранилища Outline слишком большой.");
        string decoded = Encoding.UTF8.GetString(key).Replace("\0", "");
        if (!System.Text.RegularExpressions.Regex.IsMatch(decoded, @"servers(?:_v\d+)?\z", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50)))
            return;
        if (value.Length > 4 * 1024 * 1024 || map.Count > 4096)
            throw new InvalidDataException("Хранилище серверов Outline слишком большое.");
        var k = Convert.ToBase64String(key);
        if (!map.TryGetValue(k, out var old) || seq >= old.Sequence)
            map[k] = new(seq, type, value);
    }
    static void Table(byte[] b, Dictionary<string, Entry> map, ReadBudget budget)
    {
        if (b.Length < 48 || BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(b.Length - 8)) != 0xdb4775248b80fb57UL)
            return;
        int p = b.Length - 48;
        Var(b, ref p);
        Var(b, ref p);
        var offset = Var(b, ref p);
        var size = Var(b, ref p);
        foreach (var index in Pairs(Block(b, offset, size, budget), budget))
        {
            int z = 0;
            var a = Var(index.Value, ref z);
            var n = Var(index.Value, ref z);
            foreach (var pair in Pairs(Block(b, a, n, budget), budget))
            {
                if (pair.Key.Length < 8)
                    continue;
                ulong footer = BinaryPrimitives.ReadUInt64LittleEndian(pair.Key.AsSpan(pair.Key.Length - 8));
                Put(map, pair.Key[..^8], footer >> 8, (byte)footer, pair.Value);
            }
        }
    }
    static void Batch(byte[] b, Dictionary<string, Entry> map, ReadBudget budget)
    {
        if (b.Length < 12)
            return;
        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(b);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        int p = 12;
        for (uint i = 0; i < count && p < b.Length; i++)
        {
            byte type = b[p++];
            if (type > 1)
                throw new InvalidDataException();
            var key = Slice(b, ref p, checked((int)Var(b, ref p)), budget);
            var val = type == 1 ? Slice(b, ref p, checked((int)Var(b, ref p)), budget) : [];
            Put(map, key, seq + i, type, val);
        }
    }
    static void Log(byte[] b, Dictionary<string, Entry> map, ReadBudget budget)
    {
        int p = 0;
        using var parts = new MemoryStream();
        while (p + 7 <= b.Length)
        {
            int remain = 32768 - p % 32768;
            if (remain < 7)
            {
                p += remain;
                continue;
            }
            int n = b[p + 4] | b[p + 5] << 8;
            int type = b[p + 6];
            p += 7;
            if (n == 0 && type == 0)
            {
                p += remain - 7;
                continue;
            }
            if (n > b.Length - p || n > remain - 7)
                break;
            budget.Consume(n * 2L);
            var chunk = b.AsSpan(p, n);
            p += n;
            if (type == 1)
            {
                parts.SetLength(0);
                Batch(chunk.ToArray(), map, budget);
            }
            else if (type == 2)
            {
                parts.SetLength(0);
                parts.Write(chunk);
            }
            else if (type == 3)
            {
                parts.Write(chunk);
            }
            else if (type == 4)
            {
                parts.Write(chunk);
                Batch(parts.ToArray(), map, budget);
                parts.SetLength(0);
            }
        }
    }
    static string Decode(byte[] b)
    {
        if (b.Length == 0)
            return "";
        return b[0] switch
        {
            0 => Encoding.Unicode.GetString(b, 1, b.Length - 1),
            1 => Encoding.Latin1.GetString(b, 1, b.Length - 1),
            _ => Encoding.UTF8.GetString(b)
        };
    }
    public static List<string> ServerValues(string directory)
    {
        if (!LocalPathPolicy.TryDirectory(directory, out string localDirectory, allowLocalLinks: false))
            throw new InvalidDataException("Хранилище Outline должно находиться в существующей локальной папке без перенаправлений.");
        var map = new Dictionary<string, Entry>();
        var budget = new ReadBudget();
        long totalBytes = 0;
        int fileCount = 0;
        foreach (var file in Directory.EnumerateFiles(localDirectory, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(f => f.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
        {
            if (!LocalPathPolicy.TryFile(file, out string localFile, allowLocalLinks: false))
                continue;
            totalBytes += new FileInfo(localFile).Length;
            if (++fileCount > 256 || totalBytes > 256L * 1024 * 1024)
                throw new InvalidDataException("Хранилище Outline слишком большое для импорта.");
            var b = Read(localFile, budget);
            if (localFile.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase))
                Table(b, map, budget);
            else
                Log(b, map, budget);
        }
        return map.Values.Where(e => e.Type == 1).Select(e => Decode(e.Value)).ToList();
    }
}
