using System.Text;

namespace FlowVPN;

public enum DomainTermKind
{
    Keyword = 0, Regex = 1, Suffix = 2, Exact = 3
}
public sealed record DomainTerm(DomainTermKind Kind, string Value);
public sealed record GeoSiteData(IReadOnlyList<string> Categories, IReadOnlyList<DomainTerm> Selected);

// Minimal reader for the upstream GeoSiteList protobuf. Unknown fields are skipped; no external runtime or executable is required.
public static class GeoSiteReader
{
    static readonly UTF8Encoding Utf8 = new(false, true);
    public static GeoSiteData Read(ReadOnlySpan<byte> bytes, string? selected = null, CancellationToken token = default) =>
        ReadMany(bytes, selected == null ? [] : [selected], token);

    public static GeoSiteData ReadMany(ReadOnlySpan<byte> bytes, IReadOnlyCollection<string> selected, CancellationToken token = default)
    {
        if (bytes.Length > 128 * 1024 * 1024)
            throw new InvalidDataException("Каталог GeoSite больше ожидаемого размера.");
        var root = new WireReader(bytes);
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = BaseDomainSelection.CanonicalCategories(selected).ToHashSet(StringComparer.Ordinal);
        var uniqueTerms = new HashSet<DomainTerm>();
        var terms = new List<DomainTerm>();
        while (!root.End)
        {
            token.ThrowIfCancellationRequested();
            uint tag = root.Tag();
            if (tag != 10)
            {
                root.Skip(tag);
                continue;
            }
            var entryBytes = root.Bytes();
            var entry = new WireReader(entryBytes);
            string id = "";
            while (!entry.End)
            {
                token.ThrowIfCancellationRequested();
                tag = entry.Tag();
                if (tag == 10)
                {
                    id = DomainRepository.CategoryId(Utf8.GetString(entry.Bytes()).ToLowerInvariant());
                    break;
                }
                else
                    entry.Skip(tag);
            }
            if (id.Length == 0)
                throw new InvalidDataException("Категория GeoSite не имеет имени.");
            if (!categories.Add(id))
                throw new InvalidDataException("Повторяющееся имя категории GeoSite.");
            if (categories.Count > 20000)
                throw new InvalidDataException("Слишком много категорий GeoSite.");
            if (!wanted.Contains(id))
                continue;
            entry = new WireReader(entryBytes);
            while (!entry.End)
            {
                token.ThrowIfCancellationRequested();
                tag = entry.Tag();
                if (tag != 18)
                {
                    entry.Skip(tag);
                    continue;
                }
                var domain = new WireReader(entry.Bytes());
                int type = 0;
                string value = "";
                while (!domain.End)
                {
                    tag = domain.Tag();
                    if (tag == 8)
                        type = checked((int)domain.Number());
                    else if (tag == 18)
                        value = Utf8.GetString(domain.Bytes());
                    else
                        domain.Skip(tag);
                }
                if (type is < 0 or > 3 || value.Length is 0 or > 16384)
                    throw new InvalidDataException("Неизвестный формат доменного правила GeoSite.");
                if (type == (int)DomainTermKind.Exact || type == (int)DomainTermKind.Suffix) value = DomainCoverage.Normalize(value);
                var term = new DomainTerm((DomainTermKind)type, value);
                if (uniqueTerms.Add(term)) terms.Add(term);
                if (terms.Count > 2000000)
                    throw new InvalidDataException("Слишком много записей в категории GeoSite.");
            }
        }
        if (categories.Count == 0 || !wanted.IsSubsetOf(categories))
            throw new InvalidDataException("Выбранной категории нет в каталоге GeoSite.");
        return new(categories.OrderBy(c => c, StringComparer.Ordinal).ToArray(), terms);
    }

    ref struct WireReader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> data = data;
        int position = 0;
        public bool End => position == data.Length;
        public ulong Number()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (position >= data.Length)
                    throw new InvalidDataException("Оборванный файл GeoSite.");
                byte next = data[position++];
                if (shift == 63 && (next & 0xfe) != 0)
                    throw new InvalidDataException("Некорректное число GeoSite.");
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0)
                    return value;
            }
            throw new InvalidDataException("Некорректное число GeoSite.");
        }
        public uint Tag()
        {
            uint tag = checked((uint)Number());
            if (tag >> 3 == 0)
                throw new InvalidDataException("Некорректное поле GeoSite.");
            return tag;
        }
        public ReadOnlySpan<byte> Bytes()
        {
            int length = checked((int)Number());
            if (length > data.Length - position)
                throw new InvalidDataException("Оборванный блок GeoSite.");
            var result = data.Slice(position, length);
            position += length;
            return result;
        }
        public void Skip(uint tag)
        {
            switch (tag & 7)
            {
                case 0:
                    Number();
                    break;
                case 1:
                    Advance(8);
                    break;
                case 2:
                    Bytes();
                    break;
                case 5:
                    Advance(4);
                    break;
                default:
                    throw new InvalidDataException("Неподдерживаемый тип поля GeoSite.");
            }
        }
        void Advance(int count)
        {
            if (count > data.Length - position)
                throw new InvalidDataException("Оборванный файл GeoSite.");
            position += count;
        }
    }
}
