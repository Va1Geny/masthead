namespace Isle.Core;

/// <summary>All on-disk locations. Set ISLE_DATA_DIR (or <see cref="Root"/> in tests) to redirect.</summary>
public static class Paths
{
    static string? _root;

    public static string Root
    {
        get
        {
            if (_root is null)
            {
                var env = Environment.GetEnvironmentVariable("ISLE_DATA_DIR");
                _root = string.IsNullOrWhiteSpace(env)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isle")
                    : env;
            }
            Directory.CreateDirectory(_root);
            return _root;
        }
        set => _root = value;
    }

    public static string File(string name) => Path.Combine(Root, name);

    public static string Dir(string name)
    {
        var d = Path.Combine(Root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public static string SettingsFile => File("settings.json");
    public static string LogFile => File("log.txt");
}
