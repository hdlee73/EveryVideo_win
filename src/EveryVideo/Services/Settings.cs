using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryVideo.Services;

public enum RepeatMode { None, One, All }

public sealed class ServerEntry
{
    public string Name { get; set; } = "";
    /// <summary>"ftp" 또는 "smb"</summary>
    public string Kind { get; set; } = "ftp";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>FTP: 시작 폴더, SMB: 공유 이름과 폴더 (예: Videos/Movies)</summary>
    public string Path { get; set; } = "";

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? $"{Kind.ToUpperInvariant()} · {Host}" : Name;
    [JsonIgnore]
    public string Detail => $"{Kind.ToUpperInvariant()}  {Host}{(Port > 0 ? ":" + Port : "")}/{Path.TrimStart('/')}";
}

/// <summary>앱 설정. %AppData%\EveryVideo\settings.json 에 저장된다.</summary>
public sealed class Settings
{
    public static string DataDir { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryVideo");

    private static string FilePath => System.IO.Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Current { get; private set; } = Load();

    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    /// <summary>-100 ~ 100. 0 이 원본 밝기.</summary>
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int Saturation { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.All;
    public bool ShowSidePanel { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public bool HardwareDecoding { get; set; } = true;
    public string? CaptureFolder { get; set; }
    public string? VideoFolder { get; set; }
    public string? LastFolder { get; set; }
    public List<string> RecentFiles { get; set; } = new();
    public List<ServerEntry> Servers { get; set; } = new();
    /// <summary>파일별 마지막 재생 위치 (ms). 이어서 재생에 쓴다.</summary>
    public Dictionary<string, long> Resume { get; set; } = new();
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 720;

    public string CaptureDir => EnsureDir(CaptureFolder,
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "EveryVideo"));

    public string VideoDir => EnsureDir(VideoFolder,
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "EveryVideo"));

    private static string EnsureDir(string? chosen, string fallback)
    {
        var dir = string.IsNullOrWhiteSpace(chosen) ? fallback : chosen;
        try { Directory.CreateDirectory(dir); }
        catch { dir = fallback; Directory.CreateDirectory(dir); }
        return dir;
    }

    public void AddRecent(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 15) RecentFiles.RemoveRange(15, RecentFiles.Count - 15);
    }

    public void SetResume(string key, long ms, long length)
    {
        // 앞부분 10초, 끝나기 15초 전 이후는 기억하지 않는다.
        if (ms < 10_000 || length <= 0 || ms > length - 15_000) Resume.Remove(key);
        else Resume[key] = ms;
        if (Resume.Count > 300)
            foreach (var k in Resume.Keys.Take(Resume.Count - 300).ToList()) Resume.Remove(k);
    }

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e)
        {
            Log.Write("settings load", e);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Log.Write("settings save", e);
        }
    }
}
