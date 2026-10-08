using System.Windows;
using System.Windows.Threading;
using EveryVideo.Services;

namespace EveryVideo;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        LibVLCSharp.Shared.Core.Initialize();

        if (e.Args.Contains("--selftest"))
        {
            Shutdown(SelfTest() ? 0 : 1);
            return;
        }

        var main = new MainWindow(e.Args);
        MainWindow = main;
        main.Show();
    }

    /// <summary>CI 확인용: 모든 창의 XAML 을 읽어 보고, 재생 엔진을 만들어 본 뒤 끝낸다.</summary>
    private static bool SelfTest()
    {
        try
        {
            using (var vlc = new LibVLCSharp.Shared.LibVLC("--no-video-title-show"))
            using (new LibVLCSharp.Shared.MediaPlayer(vlc)) { }
            _ = new MainWindow(Array.Empty<string>());
            _ = new Dialogs.AboutWindow();
            _ = new Dialogs.ClipWindow("test.mp4", 60_000, 0, 10_000, () => 0);
            _ = new Dialogs.ConcatWindow(Array.Empty<string>());
            _ = new Dialogs.RemoteWindow();
            _ = new Dialogs.ServerEditWindow(null);
            _ = new Dialogs.BookmarksWindow();
            _ = new Dialogs.RecorderWindow();
            if (!Ffmpeg.Available) throw new InvalidOperationException("ffmpeg.exe not found");
            Log.Write("selftest ok", null, $"v{AppInfo.Version} ({AppInfo.BuildDate}) ffmpeg={Ffmpeg.ExePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("selftest failed", ex);
            return false;
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("unhandled", e.Exception);
        e.Handled = true;
        Dialogs.CardDialog.Show("문제가 생겼습니다", e.Exception.Message, Dialogs.MessageKind.Error);
    }
}
