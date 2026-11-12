using System.IO;

namespace LanChat;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly List<string> History = new();
    private static readonly DateTime StartedAt = DateTime.Now;
    public static event Action<string>? EntryAdded;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (Sync) History.Add(line);
        EntryAdded?.Invoke(line);
    }

    public static string[] GetSnapshot()
    {
        lock (Sync) return History.ToArray();
    }

    public static void SaveRuntimeLog()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"LanChat-runtime-{StartedAt:yyyyMMdd-HHmmss}.txt");
            string[] lines;
            lock (Sync)
            {
                History.Add($"{DateTime.Now:HH:mm:ss}  程序退出。");
                lines = History.ToArray();
            }
            File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Runtime logging is intentionally silent; never block application shutdown.
        }
    }
}
