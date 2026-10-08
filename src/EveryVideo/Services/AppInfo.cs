using System.Diagnostics;
using System.Reflection;

namespace EveryVideo.Services;

public static class AppInfo
{
    public const string Name = "EveryVideo";
    public const string Author = "이현덕";
    public const string Email = "hdlee73@gmail.com";
    public const string RepoOwner = "hdlee73";
    public const string RepoName = "EveryVideo_win";

    public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";
    public static string ReleasesUrl => RepoUrl + "/releases";
    public static string LatestReleaseUrl => ReleasesUrl + "/latest";
    public static string LatestReleaseApi => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static Assembly Asm => typeof(AppInfo).Assembly;

    /// <summary>"0.1.0" 또는 "0.1.0-dev.12" (빌드 커밋 표시는 뺀다)</summary>
    public static string Version
    {
        get
        {
            var v = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? Asm.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    /// <summary>빌드(업데이트) 날짜, yyyy-MM-dd</summary>
    public static string BuildDate =>
        Asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "";

    public static string VersionLine => string.IsNullOrEmpty(BuildDate) ? $"v{Version}" : $"v{Version} ({BuildDate})";

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write("open url", e, url); }
    }
}
