namespace FlowVPN;

internal static class TrafficHistoryChecks
{
    public static void Run(Action<bool, string> check)
    {
        var history = new TrafficHistory();
        var time = DateTimeOffset.UnixEpoch;
        check(!history.Add(1, time, 1, 2), "Traffic history rejects samples without a session");
        check(history.Begin("first", "profile") && history.Add(1, time, 0, 0), "Real zero traffic is retained as an available sample");
        check(!history.Add(1, time.AddSeconds(1), 1, 2) && !history.Add(2, time, 1, 2), "Repeated sequence and nonincreasing timestamps are ignored");
        check(!history.Add(2, time.AddSeconds(1), double.NaN, 1) && !history.Add(2, time.AddSeconds(1), 1, double.PositiveInfinity) && !history.Add(2, time.AddSeconds(1), -1, 1), "Invalid traffic rates do not enter the chart");
        check(history.Gap(time.AddSeconds(1)) && !history.Gap(time.AddSeconds(2)) && !history.Copy()[1].Available, "An outage adds one explicit gap instead of zero samples");
        check(history.Add(2, time.AddSeconds(3), 10, 20) && !TrafficHistory.Continuous(history.Copy()[1], history.Copy()[2]), "Fresh samples resume after an outage without bridging it");
        var paused = history.Copy();
        for (int i = 3; i < 1200; i++) history.Add(i, time.AddSeconds(i + 1), i, i * 2);
        check(history.Count == TrafficHistory.Capacity && history.Copy()[0].At == time.AddSeconds(301) && history.Copy()[^1].At == time.AddSeconds(1200), "Traffic ring keeps the newest 900 ordered samples");
        check(paused.Length == 3 && paused[0].At == time, "Paused chart copies remain unchanged while live sampling continues");
        history.Prune(time.AddSeconds(1199));
        check(history.Count == 2 && history.Copy()[0].At == time.AddSeconds(1199), "Time pruning removes expired samples even without incoming telemetry");
        check(!history.Begin("first", "profile") && history.Count == 2 && history.Begin("second", "profile") && history.Count == 0 && history.Add(1, time, 1, 2), "Reconnecting the same profile starts a new independent history");
        check(history.Begin("second", "other") && history.Count == 0, "Changing the session profile cannot reuse another profile history");
        history.Add(1, time, 1, 2);
        history.Clear();
        check(history.Count == 0 && history.SessionId.Length == 0 && history.ProfileId.Length == 0, "Disposal clearing releases session identifiers and retained samples");
        check(TrafficHistory.Continuous(new(time, 0, 0), new(time.AddSeconds(1), 1, 1)) && !TrafficHistory.Continuous(new(time, 0, 0), new(time.AddSeconds(4), 1, 1)), "Chart segments bridge consecutive samples but never missing intervals");
        CoreConnection[] connections = [new("1", "example.org", @"C:\Apps\Browser.exe", "vpn", 10, 30, "192.0.2.1"), new("2", "EXAMPLE.ORG", @"C:\Apps\Browser.exe", "direct", 20, 40, "192.0.2.2"), new("3", "", "", "vpn", 2, 3, "192.0.2.3")];
        var apps = TrafficConnectionGroups.Build(connections, TrafficGrouping.Application, TrafficSort.Total, "");
        check(apps.Count == 2 && apps[0].Connections.Count == 2 && apps[0].Uploaded == 30 && apps[0].Downloaded == 70, "Application breakdown sums only its currently active sockets");
        var addresses = TrafficConnectionGroups.Build(connections, TrafficGrouping.Address, TrafficSort.Total, "");
        check(addresses.Count == 2 && addresses[0].Connections.Count == 2 && addresses[1].Name == "192.0.2.3", "Address grouping merges domain case and retains IP-only connections");
        check(TrafficConnectionGroups.Build(connections, TrafficGrouping.Application, TrafficSort.Total, "192.0.2.2").Single().Connections.Single().Id == "2", "Connection search includes destination IPs");
        check(TrafficConnectionGroups.Sum(long.MaxValue - 1, 8) == long.MaxValue, "Large active traffic totals saturate instead of overflowing");
        check(TrafficConnectionGroups.Build([], TrafficGrouping.Address, TrafficSort.Uploaded, "").Count == 0, "Empty connection snapshots remain empty rather than preserving closed sockets");
    }
}
