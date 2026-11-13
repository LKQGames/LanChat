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
        var fileName = $"LanChat-runtime-{StartedAt:yyyyMMdd-HHmmss}.txt";
        string[] lines;
        lock (Sync)
        {
            History.Add($"{DateTime.Now:HH:mm:ss}  程序退出。");
            lines = History.ToArray();
        }
        foreach (var directory in GetLogDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllLines(Path.Combine(directory, fileName), lines, new System.Text.UTF8Encoding(true));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 程序目录不可写时退回到用户缓存目录；两种情况都失败也不影响退出。
            }
        }
    }

    /// <summary>优先写入程序目录下的 logs 文件夹，不可写时退回 LocalAppData。</summary>
    private static IEnumerable<string> GetLogDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "logs");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanChat", "logs");
    }
}
