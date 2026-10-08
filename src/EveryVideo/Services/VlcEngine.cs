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
    private static string StampPath => Path.Combine(PluginDir, "plugins.ver");

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
            try { File.WriteAllText(StampPath, stamp); }
            catch { /* 설치 폴더에 쓸 수 없으면 캐시 없이 쓴다. */ }
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
            return File.Exists(Path.Combine(PluginDir, "plugins.dat"))
                   && File.Exists(StampPath)
                   && File.ReadAllText(StampPath) == stamp;
        }
        catch
        {
            return false;
        }
    }
}
