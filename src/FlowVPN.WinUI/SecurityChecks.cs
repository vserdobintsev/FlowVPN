using System.Security.Cryptography;
using System.Text;

namespace FlowVPN;

internal static class SecurityChecks
{
    public static void Run(Action<bool, string> check)
    {
        var plain = Encoding.UTF8.GetBytes("Synthetic DPAPI round-trip");
        var encrypted = Native.Protect(plain);
        check(!encrypted.SequenceEqual(plain) && Native.Unprotect(encrypted).SequenceEqual(plain), "Settings encryption round-trips under the current Windows account");
        using var altered = new MemoryStream([1, 2, 4]);
        bool rejected = false;
        try
        {
            CoreIntegrity.Verify(altered, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })));
        }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "Changing even one core byte causes an integrity failure");
        foreach (string category in new[] { "../secret", "ru-blocked\n", "a/b", "..\\secret", "" })
        {
            rejected = false;
            try
            {
                DomainRepository.CategoryId(category);
            }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Unsafe category identifier rejected: " + Convert.ToHexString(Encoding.UTF8.GetBytes(category)));
        }
        byte[] Field(byte tag, byte[] data)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(tag);
            uint length = (uint)data.Length;
            do
            {
                stream.WriteByte((byte)((length & 127u) | (length > 127 ? 128u : 0u)));
                length >>= 7;
            } while (length > 0);
            stream.Write(data);
            return stream.ToArray();
        }
        byte[] Text(byte tag, string text) => Field(tag, Encoding.UTF8.GetBytes(text));
        var categoryBytes = Text(10, "FIXTURE").Concat(Enumerable.Range(0, 4).SelectMany(type => Field(18, new byte[] { 8, (byte)type }.Concat(Text(18, "example.com")).ToArray()))).ToArray();
        var fixture = Field(10, categoryBytes);
        var parsed = GeoSiteReader.Read(fixture, "fixture");
        check(parsed.Categories.SequenceEqual(new[] { "fixture" }) && parsed.Selected.Select(t => (int)t.Kind).SequenceEqual(Enumerable.Range(0, 4)), "GeoSite protobuf reads all four rule kinds and case-normalized category names");
        foreach (var bad in new[] { new byte[] { 10, 127, 1 }, new byte[] { 0 }, fixture.Concat(fixture).ToArray() })
        {
            rejected = false;
            try
            {
                GeoSiteReader.Read(bad);
            }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed or duplicate GeoSite entry is rejected (" + bad.Length + " bytes)");
        }
        var rule = DomainCoverage.ToRuleSet(parsed.Selected)["rules"]![0]!;
        check(rule["domain_suffix"] != null && rule["domain_keyword"] != null && rule["domain_regex"] != null && rule["domain"] == null, "All GeoSite rule kinds survive conversion; redundant exact rules are removed");
        var exact = new DomainCoverage(new DomainTerm[] { new(DomainTermKind.Exact, "example.com"), new(DomainTermKind.Regex, "^example[.]org$") });
        check(DomainCoverage.Effective(["example.com", "example.org"], exact).Count == 2, "Exact and regex matches never erase broader subdomain routing");
        string credential = Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:fixture:password"));
        var source = Importers.Outline("ss://" + credential + "@127.0.0.1:23456/?outline=1#Fixture");
        check(source.Name == "Fixture" && System.Text.Json.Nodes.JsonNode.Parse(source.ConnectionJson)?["password"]?.ToString() == "fixture:password", "Outline import retains colon-containing passwords without contacting a server");
        string file = Path.Combine(Store.Run, "untrusted-import.ovpn");
        try
        {
            File.WriteAllText(file, "client\nremote 127.0.0.1 1194\nscript-security 2\nup malicious.exe\n");
            rejected = false;
            try
            {
                Importers.OpenVpn(file);
            }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "OpenVPN script directives are rejected before reading key material");
        }
        finally { File.Delete(file); }
        check(LevelStore.Snappy([5, 16, 104, 101, 108, 108, 111], new()).SequenceEqual(Encoding.ASCII.GetBytes("hello")), "Snappy literal block is decoded correctly");
        check(LevelStore.Snappy([5, 0, 97, 1, 1], new()).SequenceEqual(Encoding.ASCII.GetBytes("aaaaa")), "Overlapping Snappy back-references preserve their semantics");
        foreach (var bad in new[] { new byte[] { 5, 16, 104 }, new byte[] { 5, 1, 0 }, new byte[] { 0x81, 0x80, 0x80, 0x20 }, new byte[] { 0, 1 } })
        {
            rejected = false;
            try
            {
                LevelStore.Snappy(bad, new());
            }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Corrupt or oversized Snappy block rejected before unbounded allocation (" + bad.Length + " bytes)");
        }
        var budget = new LevelStore.ReadBudget(8);
        _ = LevelStore.Snappy([5, 0, 97, 1, 1], budget);
        rejected = false;
        try
        {
            LevelStore.Snappy([5, 0, 97, 1, 1], budget);
        }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "Outline parsing budget is cumulative across blocks");
        var random = new Random(1742);
        for (int i = 0; i < 5000; i++)
        {
            var sample = new byte[random.Next(80)];
            random.NextBytes(sample);
            try
            {
                LevelStore.Snappy(sample, new(4096));
            }
            catch (InvalidDataException) { }
        }
        check(true, "5000 deterministic malformed Snappy inputs produce bounded results or controlled errors");
        string fixtureFolder = Path.Combine(Store.Run, "outline-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureFolder);
        try
        {
            byte[] key = Encoding.ASCII.GetBytes("_file://\0servers"), value = [1, .. Encoding.ASCII.GetBytes("[]")];
            using var record = new MemoryStream();
            using var writer = new BinaryWriter(record, Encoding.UTF8, true);
            writer.Write(1UL);
            writer.Write(1U);
            writer.Write((byte)1);
            writer.Write((byte)key.Length);
            writer.Write(key);
            writer.Write((byte)value.Length);
            writer.Write(value);
            writer.Flush();
            byte[] payload = record.ToArray();
            File.WriteAllBytes(Path.Combine(fixtureFolder, "000001.log"), [0, 0, 0, 0, (byte)payload.Length, 0, 1, .. payload]);
            check(LevelStore.ServerValues(fixtureFolder).SequenceEqual(new[] { "[]" }), "Bounded Outline WAL reader retains compatible Chromium string decoding");
        }
        finally { Directory.Delete(fixtureFolder, true); }
    }
}
