using System.Diagnostics;
using System.IO;
using LibVLCSharp.Shared;

namespace EveryVideo.Services;

/// <summary>
/// 재생 엔진(LibVLC)을 화면을 막지 않고 준비한다.
/// LibVLC 패키지에는 plugins.dat(부품 목록 캐시)가 없어서, 그대로 두면 켤 때마다
/// 수백 개의 부품 DLL 을 모두 열어 본다. 처음 한 번 이 PC 에서 캐시를 만들어 두면
/// 다음부터는 필요한 부품만 읽어 훨씬 빨리 켜진다.
/// </summary>
public static class VlcEngine
{
    private static string PluginDir => Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins");
    // 설치 폴더(Program Files)는 쓰기 권한이 없을 수 있어 스탬프는 AppData에 둔다.
    // 기존에 설치 폴더에 있던 스탬프도 읽어서 불필요한 --reset-plugins-cache를 피한다.
    private static string StampPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryVideo", "plugins.ver");
    private static string LegacyStampPath => Path.Combine(PluginDir, "plugins.ver");

    public static Task<LibVLC> CreateAsync(bool hardwareDecoding) => Task.Run(() =>
    {
        var sw = Stopwatch.StartNew();
        Core.Initialize();
        var args = new List<string>
        {
            "--no-video-title-show",
            "--no-snapshot-preview",
            "--snapshot-format=png",
            "--network-caching=1500",
            hardwareDecoding ? "--avcodec-hw=any" : "--avcodec-hw=none",
        };
        var stamp = Stamp();
        var rebuild = stamp != null && !CacheIsCurrent(stamp);
        if (rebuild) args.Add("--reset-plugins-cache");
        var vlc = new LibVLC(args.ToArray());
        if (rebuild)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StampPath)!);
                File.WriteAllText(StampPath, stamp);
            }
            catch { /* 캐시 스탬프를 못 써도 재생은 된다. */ }
        }
        Log.Write("engine ready", null, $"{sw.ElapsedMilliseconds} ms{(rebuild ? " (plugins cache rebuilt)" : "")}");
        return vlc;
    });

    /// <summary>앱 버전과 이 PC 에 풀린 파일 시각을 묶은 값. 둘 중 하나라도 바뀌면 캐시를 다시 만든다.</summary>
    private static string? Stamp()
    {
        try
        {
            var core = Path.Combine(PluginDir, "..", "libvlccore.dll");
            if (!Directory.Exists(PluginDir) || !File.Exists(core)) return null;
            return $"{AppInfo.Version}|{File.GetLastWriteTimeUtc(core).Ticks}";
        }
        catch
        {
            return null;
        }
    }

    private static bool CacheIsCurrent(string stamp)
    {
        try
        {
            // AppData 또는 예전 위치(설치 폴더) 중 하나라도 맞으면 캐시가 유효하다.
            if (File.Exists(StampPath) && File.ReadAllText(StampPath) == stamp)
                return File.Exists(Path.Combine(PluginDir, "plugins.dat"));
            if (File.Exists(LegacyStampPath) && File.ReadAllText(LegacyStampPath) == stamp)
                return File.Exists(Path.Combine(PluginDir, "plugins.dat"));
            return false;
        }
        catch
        {
            return false;
        }
    }
}
