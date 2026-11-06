using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace FlowVPN;

// Independent credentials for the loopback traffic probe/update proxy, renewed per VPN session.
public sealed class ProxyAccess
{
    readonly NetworkCredential credential = new("flowvpn", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    public JsonArray Users() => [new JsonObject { ["username"] = credential.UserName, ["password"] = credential.Password }];
    public IWebProxy At(int port) => new WebProxy($"socks5://127.0.0.1:{port}") { Credentials = credential };
}
