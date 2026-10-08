using System.IO;

namespace EveryVideo.Services;

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string tag, Exception? ex = null, string? message = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.DataDir);
                var path = Path.Combine(Settings.DataDir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag} {message} {ex}\r\n");
            }
        }
        catch
        {
            // 로그를 못 남겨도 앱은 계속 동작해야 한다.
        }
    }
}
