using System.Text.RegularExpressions;
using Isle.Modules.Logs;
using Isle.Modules.Ports;

namespace Isle.Tests;

public class DevExtrasTests
{
    [Fact]
    public void Running_wsl_distributions_are_parsed()
    {
        const string output = "  NAME                   STATE           VERSION\r\n* Ubuntu-24.04           Running         2\r\n  docker-desktop         Stopped         2\r\n  Debian                 Running         1\r\n";
        var rows = ContainerParse.Wsl(output);
        Assert.Equal(["Ubuntu-24.04", "Debian"], rows.Select(r => r.Name));
        Assert.Equal("WSL 2", rows[0].Detail);
    }

    [Fact]
    public void Log_tail_reports_only_new_matching_lines_and_survives_rotation()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "old ERROR before watching\n");
            var tail = new LogTail(file, new Regex(LogWatchModule.DefaultPattern, RegexOptions.IgnoreCase));
            Assert.Null(tail.Poll());                                  // first poll: remember the end only
            File.AppendAllText(file, "INFO started\nWARN slow\n");
            Assert.Null(tail.Poll());
            File.AppendAllText(file, "INFO x\nSystem.NullReferenceException: boom\nERROR second\n");
            Assert.Equal("System.NullReferenceException: boom", tail.Poll());
            Assert.Null(tail.Poll());
            File.WriteAllText(file, "FATAL after rotation\n");     // shorter: read again from the start
            Assert.Equal("FATAL after rotation", tail.Poll());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Missing_log_file_is_quiet() => Assert.Null(new LogTail(@"C:\nope\missing.log", new Regex("ERROR")).Poll());
}
