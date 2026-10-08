using System.IO;
using System.Windows;
using System.Windows.Threading;
using EveryVideo.Services;

namespace EveryVideo.Dialogs;

/// <summary>화면 녹화 창. 녹화하는 동안 떠 있다.</summary>
public partial class RecorderWindow : CardWindow
{
    private readonly ScreenRecorder _recorder = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public RecorderWindow()
    {
        InitializeComponent();
        Owner = null;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _timer.Tick += (_, _) => ElapsedText.Text = TimeFormat.Format((long)(DateTime.Now - _recorder.StartedAt).TotalMilliseconds);
        _recorder.Stopped += () => Dispatcher.BeginInvoke(() => { if (StartButton.Content as string == "녹화 중지") _ = StopAsync(); });
        Loaded += async (_, _) =>
        {
            AudioCombo.Items.Add("녹음 안 함");
            AudioCombo.SelectedIndex = 0;
            try
            {
                foreach (var d in await Ffmpeg.AudioDevicesAsync()) AudioCombo.Items.Add(d);
            }
            catch (Exception ex)
            {
                Log.Write("audio devices", ex);
            }
        };
        Closing += async (_, e) =>
        {
            if (!_recorder.IsRecording) return;
            e.Cancel = true;
            await StopAsync();
            Close();
        };
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            await StopAsync();
            return;
        }
        if (!Ffmpeg.Available)
        {
            StatusText.Text = "ffmpeg.exe 를 찾을 수 없습니다. 앱을 다시 설치해 주세요.";
            return;
        }
        var output = Path.Combine(Settings.Current.VideoDir, $"화면녹화_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        var audio = AudioCombo.SelectedIndex > 0 ? AudioCombo.SelectedItem as string : null;
        try
        {
            _recorder.Start(output, PrimaryRadio.IsChecked == true, audio);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        OptionsPanel.Visibility = Visibility.Collapsed;
        RecPanel.Visibility = Visibility.Visible;
        StartButton.Content = "녹화 중지";
        StartButton.Background = (System.Windows.Media.Brush)FindResource("Danger");
        StatusText.Text = "녹화 중입니다. 이 창은 녹화에 함께 찍힐 수 있습니다.";
        _timer.Start();
        if (HideCheck.IsChecked == true && Application.Current.MainWindow != null)
            Application.Current.MainWindow.WindowState = WindowState.Minimized;
    }

    private async Task StopAsync()
    {
        _timer.Stop();
        StartButton.IsEnabled = false;
        StatusText.Text = "파일을 마무리하는 중…";
        var ok = await _recorder.StopAsync();
        StartButton.IsEnabled = true;
        StartButton.Content = "녹화 시작";
        StartButton.ClearValue(BackgroundProperty);
        OptionsPanel.Visibility = Visibility.Visible;
        RecPanel.Visibility = Visibility.Collapsed;
        if (ok)
        {
            StatusText.Text = $"저장했습니다: {Path.GetFileName(_recorder.OutputPath)}";
            if (CardDialog.Show("화면 녹화", $"녹화한 파일을 저장했습니다.\n{_recorder.OutputPath}", MessageKind.Info, "폴더 열기", "닫기") == 0)
                OpenFolder(_recorder.OutputPath!);
        }
        else
        {
            var last = string.Join("\n", _recorder.LastLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2));
            StatusText.Text = "녹화하지 못했습니다. " + last;
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(Settings.Current.VideoDir);

    public static void OpenFolder(string file)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); }
        catch (Exception e) { Log.Write("explorer", e); }
    }
}
