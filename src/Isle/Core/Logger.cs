namespace Isle.Core;

/// <summary>Tiny append-only log with 1 MB rotation. Never throws. Never pass secrets or user content.</summary>
public static class Logger
{
    const long MaxBytes = 1024 * 1024;
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Paths.LogFile;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                    System.IO.File.Move(path, path + ".1", overwrite: true);
                System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
