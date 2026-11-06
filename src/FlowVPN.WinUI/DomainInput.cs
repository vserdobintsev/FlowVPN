using System.Text.RegularExpressions;
namespace FlowVPN;

internal sealed record ParsedDomains(List<string> Domains, List<string> Lines, List<int> InvalidLines)
{
    public string Text => string.Join(Environment.NewLine, Lines);
}
internal static class DomainInput
{
    public static ParsedDomains Parse(string input)
    {
        var domains = new List<string>();
        var lines = new List<string>();
        var invalid = new List<int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            string? host = Host(line);
            if (host == null)
            {
                lines.Add(line);
                invalid.Add(lines.Count);
            }
            else if (seen.Add(host))
            {
                domains.Add(host);
                lines.Add(host);
            }
        }
        return new(domains, lines, invalid);
    }
    static string? Host(string value)
    {
        value = value.Trim(' ', '\t', '"', '\'', '<', '>', '«', '»');
        var markdown = Regex.Match(value, @"^\[[^\]]*\]\(([^\s]+)\)$");
        if (markdown.Success)
            value = markdown.Groups[1].Value;
        if (value.StartsWith("*."))
            value = value[2..];
        if (value.StartsWith("//"))
            value = "https:" + value;
        else if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns)
            return null;
        string host;
        try
        {
            host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        }
        catch { return null; }
        if (host.Length > 253 || !host.Contains('.'))
            return null;
        var labels = host.Split('.');
        if (labels.Any(label => label.Length is < 1 or > 63 || !Regex.IsMatch(label, @"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")))
            return null;
        return host;
    }
}
