namespace FlowVPN;

// This is a declarative network policy, never an executable command or a filesystem path.
internal sealed class BrokerPlan
{
    public string Kind { get; set; } = "";
    public string ConnectionJson { get; set; } = "";
    public BrokerLocalTransport? LocalTransport { get; set; }
    public string Mode { get; set; } = "include";
    public string[] ProcessPatterns { get; set; } = [];
    public string[] Domains { get; set; } = [];
    public bool BaseEnabled { get; set; }
    public string[] IpCidrs { get; set; } = [];
    public BrokerDomainRules? BaseDomains { get; set; }
}

// The broker builds the loopback outbound itself. No executable path or arbitrary proxy address crosses IPC.
internal sealed class BrokerLocalTransport
{
    public string ModuleId { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string InterfaceName { get; set; } = "";
}

internal sealed class BrokerDomainRules
{
    public string[] Domain { get; set; } = [];
    public string[] Suffix { get; set; } = [];
    public string[] Keyword { get; set; } = [];
    public string[] Regex { get; set; } = [];
}

internal sealed class BrokerRequest
{
    public string Command { get; set; } = "";
    public BrokerPlan? Plan { get; set; }
    public string? ConnectionId { get; set; }
}

internal sealed class BrokerReply
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public BrokerState? State { get; set; }
    public CoreTrafficSnapshot? Traffic { get; set; }
}

internal sealed class BrokerState
{
    public bool Running { get; set; }
    public int ProcessId { get; set; }
    public CoreSnapshot? Snapshot { get; set; }
    public string ControlError { get; set; } = "";
    public string[] Logs { get; set; } = [];
    public int ProxyPort { get; set; }
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public string InterfaceName { get; set; } = "";
}
