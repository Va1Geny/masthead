using System.Text;
using Isle.Modules.Docker;

namespace Isle.Tests;

public class DockerTests
{
    const string List = """
    [
     {"Id":"a1","Names":["/shop-db"],"Image":"registry.local:5000/lib/postgres:17","State":"running","Status":"Up 2 hours",
      "Ports":[{"PrivatePort":9187,"PublicPort":9187,"Type":"tcp"},{"PrivatePort":5432,"PublicPort":15432,"Type":"tcp"}],
      "Labels":{"com.docker.compose.project":"shop","com.docker.compose.project.working_dir":"C:\\demo\\shop","com.docker.compose.project.config_files":"C:\\demo\\shop\\a.yaml,C:\\demo\\shop\\b.yaml"}},
     {"Id":"a2","Names":["/shop-web"],"Image":"shop/web","State":"exited","Status":"Exited (1)","Ports":[],"Labels":{"com.docker.compose.project":"shop"}},
     {"Id":"c1","Names":["/zed"],"Image":"redis","State":"running","Status":"Up","Ports":[],"Labels":null},
     {"Id":"b1","Names":["/blog"],"Image":"alpine:3","State":"running","Status":"Up","Ports":[{"PrivatePort":80}],"Labels":{"com.docker.compose.project":"blog"}}
    ]
    """;

    [Fact]
    public void Containers_parse_labels_db_label_and_host_port()
    {
        var boxes = DockerParse.Containers(List);
        Assert.Equal(4, boxes.Count);
        var db = boxes[0];
        Assert.Equal("shop-db", db.Name);
        Assert.Equal("shop", db.Project);
        Assert.Equal("PostgreSQL :15432", db.Detail); // the database port wins over the exporter port
        Assert.True(db.Running);
        Assert.Equal("shop/web", boxes[1].Detail);
        Assert.Null(boxes[2].Project);
        Assert.Equal("Redis", boxes[2].Detail);
        Assert.Equal("alpine:3", boxes[3].Detail);
    }

    [Theory]
    [InlineData("postgres:17", "postgres")]
    [InlineData("docker.io/library/MariaDB:11@sha256:abc", "mariadb")]
    [InlineData("localhost:5000/team/mongo", "mongo")]
    public void Repo_strips_registry_tag_and_digest(string image, string repo) => Assert.Equal(repo, DockerParse.Repo(image));

    [Fact]
    public void Group_orders_projects_and_puts_standalone_last()
    {
        var g = DockerParse.Group(DockerParse.Containers(List));
        Assert.Equal(["blog", "shop", ""], g.Select(x => x.Project));
        Assert.Equal(["shop-db", "shop-web"], g[1].Boxes.Select(b => b.Name));
    }

    [Fact]
    public void Compose_args_use_the_compose_labels()
    {
        var b = DockerParse.Containers(List)[0];
        Assert.Equal(["compose", "-p", "shop", "--project-directory", @"C:\demo\shop", "-f", @"C:\demo\shop\a.yaml", "-f", @"C:\demo\shop\b.yaml", "up", "-d"],
            DockerParse.ComposeArgs("shop", b.WorkDir, b.ConfigFiles, "up", "-d"));
        Assert.Equal(["compose", "-p", "x", "down"], DockerParse.ComposeArgs("x", null, null, "down"));
    }

    [Fact]
    public void Event_parses_container_events_only()
    {
        var e = DockerParse.Event("""{"Type":"container","Action":"die","Actor":{"ID":"a1","Attributes":{"name":"shop-web","exitCode":"1"}},"time":1700000000}""");
        Assert.Equal(new DockerEvent("die", "a1", "shop-web", DateTimeOffset.FromUnixTimeSeconds(1700000000)), e);
        Assert.Null(DockerParse.Event("""{"Type":"network","Action":"connect","Actor":{"ID":"n"}}"""));
        Assert.Null(DockerParse.Event("not json"));
        Assert.Null(DockerParse.Event("{}"));
    }

    [Fact]
    public void Crash_loop_needs_three_deaths_in_the_window()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        DockerEvent Die(string id, int sec, string action = "die") => new(action, id, id, t0.AddSeconds(sec));
        var c = new CrashTracker();
        Assert.False(c.Add(Die("a", 0)));
        Assert.False(c.Add(Die("a", 10, "start")));   // other actions do not count
        Assert.False(c.Add(Die("b", 20)));            // other container
        Assert.False(c.Add(Die("a", 30)));
        Assert.True(c.Add(Die("a", 40)));
        Assert.False(c.Add(Die("a", 50)));            // counter started over
        var slow = new CrashTracker();
        Assert.False(slow.Add(Die("a", 0)));
        Assert.False(slow.Add(Die("a", 100)));
        Assert.False(slow.Add(Die("a", 200)));        // first one is older than 2 min
        Assert.True(slow.Add(Die("a", 210)));
    }

    [Fact]
    public void Df_sums_each_kind_and_ignores_unmeasured_volumes()
    {
        var df = DockerParse.Df("""
        {"LayersSize":1000,"Images":[{"Size":999}],"Containers":[{"SizeRw":10},{"SizeRw":5},{}],
         "Volumes":[{"UsageData":{"Size":300}},{"UsageData":{"Size":-1}}],"BuildCache":[{"Size":70},{"Size":30}]}
        """);
        Assert.Equal(new DiskUsage(1000, 15, 300, 100), df);
        Assert.Equal(1415, df.Total);
        Assert.Equal(new DiskUsage(30, 0, 0, 0), DockerParse.Df("""{"Images":[{"Size":10},{"Size":20}]}"""));
        Assert.Equal("1.5 GB", DockerParse.Size(3L << 29));
        Assert.Equal("12 MB", DockerParse.Size(12L << 20));
    }

    [Fact]
    public void Logs_demux_framed_streams_and_pass_tty_text_through()
    {
        static byte[] Frame(byte stream, string s) => [stream, 0, 0, 0, 0, 0, 0, (byte)s.Length, .. Encoding.UTF8.GetBytes(s)];
        var framed = Frame(1, "out\n").Concat(Frame(2, "err\n")).ToArray();
        Assert.Equal("out\nerr\n", DockerParse.Logs(framed));
        Assert.Equal("plain tty\n", DockerParse.Logs(Encoding.UTF8.GetBytes("plain tty\n")));
        Assert.Equal("", DockerParse.Logs([]));
    }

    [Fact]
    public void Pipe_follows_the_setup_answer()
    {
        Assert.Equal(@"\\.\pipe\docker_engine", DockerApi.PipeFor("docker"));
        Assert.Equal(@"\\.\pipe\podman-machine-default", DockerApi.PipeFor("podman"));
    }
}
