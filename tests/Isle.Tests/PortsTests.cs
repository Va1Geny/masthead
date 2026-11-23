using Isle.Modules.Ports;

namespace Isle.Tests;

public class PortsTests
{
    [Fact]
    public void Port_is_read_from_network_byte_order() => Assert.Equal(5173, PortScanner.Port(0x3514)); // 5173 = 0x1435, bytes swapped

    [Fact]
    public void System_processes_are_hidden()
    {
        Assert.True(PortFilter.IsInteresting(new Listener(3000, 1234, "node", true)));
        Assert.False(PortFilter.IsInteresting(new Listener(135, 1100, "svchost", false)));
        Assert.False(PortFilter.IsInteresting(new Listener(445, 4, "System", false)));
        Assert.False(PortFilter.IsInteresting(new Listener(5000, 999, "", true)));
        Assert.False(PortFilter.IsInteresting(new Listener(62958, 999, "dotnet", true)));          // ephemeral
        Assert.False(PortFilter.IsInteresting(new Listener(4343, 999, "AcerCCAgent", true, true)));  // vendor service
        Assert.True(PortFilter.IsInteresting(new Listener(5432, 999, "postgres", false, true)));     // dev service
    }

    [Fact]
    public void Known_desktop_apps_are_not_dev_servers()
    {
        Assert.Equal("Spotify", PortFilter.DesktopApp("spotify"));
        Assert.Null(PortFilter.DesktopApp("node"));
    }

    [Fact]
    public void Worker_processes_sharing_a_port_are_one_row()
    {
        var merged = PortFilter.Merge([new Listener(80, 30, "nginx", true), new Listener(80, 12, "nginx", true), new Listener(80, 20, "nginx", false)]);
        Assert.Equal(12, Assert.Single(merged).Pid);
        Assert.False(merged[0].Local);
    }

    [Fact]
    public void Ipv4_and_ipv6_sockets_merge_and_network_wins()
    {
        var merged = PortFilter.Merge([
            new Listener(8080, 7, "java", true),
            new Listener(3000, 5, "node", true),
            new Listener(8080, 7, "java", false),
            new Listener(3000, 5, "node", true),
        ]);
        Assert.Equal([3000, 8080], merged.Select(l => l.Port));
        Assert.True(merged[0].Local);
        Assert.False(merged[1].Local);
    }

    [Fact]
    public void Only_new_ports_or_programs_are_announced()
    {
        Listener[] before = [new(3000, 5, "node", true)];
        Listener[] now = [new(3000, 9, "node", true), new(5173, 6, "node", true), new(3000, 7, "deno", true)];
        Assert.Equal([(5173, "node"), (3000, "deno")], PortFilter.Added(before, now).Select(l => (l.Port, l.Process)));
    }

    [Fact]
    public void Scanner_reads_this_machine()
    {
        var all = PortScanner.Scan(interestingOnly: false);
        Assert.NotEmpty(all); // Windows always listens on something (RPC 135, …)
        Assert.All(all, l => Assert.InRange(l.Port, 1, 65535));
    }
}
