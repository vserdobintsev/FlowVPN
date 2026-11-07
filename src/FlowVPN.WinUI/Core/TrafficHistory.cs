namespace FlowVPN;

internal readonly record struct TrafficPoint(DateTimeOffset At, double? Uploaded, double? Downloaded)
{
    public bool Available => Uploaded.HasValue && Downloaded.HasValue;
}

// UI-owned, fixed-size history. Missing telemetry is a gap, never an invented zero.
internal sealed class TrafficHistory
{
    public const int Capacity = 900;
    readonly TrafficPoint[] points = new TrafficPoint[Capacity];
    int start, count;
    long sequence = -1;
    public string SessionId { get; private set; } = "";
    public string ProfileId { get; private set; } = "";
    public int Count => count;
    public bool Begin(string sessionId, string profileId)
    {
        if (SessionId == sessionId && ProfileId == profileId) return false;
        SessionId = sessionId;
        ProfileId = profileId;
        Array.Clear(points);
        start = count = 0;
        sequence = -1;
        return true;
    }
    public bool Add(long sampleSequence, DateTimeOffset at, double uploaded, double downloaded)
    {
        if (SessionId.Length == 0 || sampleSequence <= sequence || !double.IsFinite(uploaded) || !double.IsFinite(downloaded) || uploaded < 0 || downloaded < 0)
            return false;
        if (count > 0 && at <= Latest.At) return false;
        sequence = sampleSequence;
        Append(new(at, uploaded, downloaded));
        return true;
    }
    public bool Gap(DateTimeOffset at)
    {
        if (count == 0 || !Latest.Available || at <= Latest.At) return false;
        Append(new(at, null, null));
        return true;
    }
    TrafficPoint Latest => points[(start + count - 1) % Capacity];
    void Append(TrafficPoint point)
    {
        if (count == Capacity) { points[start] = point; start = (start + 1) % Capacity; }
        else points[(start + count++) % Capacity] = point;
    }
    public TrafficPoint[] Copy()
    {
        var copy = new TrafficPoint[count];
        for (int i = 0; i < count; i++) copy[i] = points[(start + i) % Capacity];
        return copy;
    }
    public void Prune(DateTimeOffset minimum)
    {
        while (count > 0 && points[start].At < minimum)
        {
            points[start] = default;
            start = (start + 1) % Capacity;
            count--;
        }
    }
    public void Clear()
    {
        Array.Clear(points);
        start = count = 0;
        sequence = -1;
        SessionId = ProfileId = "";
    }
    public static bool Continuous(TrafficPoint previous, TrafficPoint current) => previous.Available && current.Available &&
        current.At > previous.At && current.At - previous.At <= TimeSpan.FromMilliseconds(3000);
}

internal enum TrafficGrouping { Application, Address }
internal enum TrafficSort { Total, Downloaded, Uploaded }
internal sealed record TrafficConnectionGroup(string Key, string Name, string Detail, long Uploaded, long Downloaded, IReadOnlyList<CoreConnection> Connections);

internal static class TrafficConnectionGroups
{
    public static string Address(CoreConnection connection) => connection.Host.Length > 0 ? connection.Host :
        connection.DestinationIp.Length > 0 ? connection.DestinationIp : "Адрес не указан";
    public static string Application(CoreConnection connection)
    {
        if (connection.ProcessPath.Length == 0) return "Процесс не определён";
        string name = Path.GetFileNameWithoutExtension(connection.ProcessPath);
        return name.Length == 0 ? connection.ProcessPath : name;
    }
    public static long Sum(long left, long right)
    {
        left = Math.Max(0, left);
        right = Math.Max(0, right);
        return right > long.MaxValue - left ? long.MaxValue : left + right;
    }
    public static IReadOnlyList<TrafficConnectionGroup> Build(IReadOnlyList<CoreConnection> connections, TrafficGrouping grouping, TrafficSort sort, string search)
    {
        var result = connections.Where(c => search.Length == 0 || (c.Host + " " + c.DestinationIp + " " + c.ProcessPath + " " + c.Route)
            .Contains(search, StringComparison.OrdinalIgnoreCase)).GroupBy(c => grouping == TrafficGrouping.Application ? c.ProcessPath : Address(c), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.OrderByDescending(c => Sum(c.Uploaded, c.Downloaded)).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
                return new TrafficConnectionGroup(group.Key, grouping == TrafficGrouping.Application ? Application(rows[0]) : Address(rows[0]),
                    grouping == TrafficGrouping.Application ? rows[0].ProcessPath : "", rows.Aggregate(0L, (n, c) => Sum(n, c.Uploaded)),
                    rows.Aggregate(0L, (n, c) => Sum(n, c.Downloaded)), rows);
            });
        return result.OrderByDescending(g => sort switch { TrafficSort.Downloaded => g.Downloaded, TrafficSort.Uploaded => g.Uploaded, _ => Sum(g.Uploaded, g.Downloaded) })
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
