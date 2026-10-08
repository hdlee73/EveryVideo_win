using System.IO;
using System.Windows;
using EveryVideo.Services;
using Microsoft.Win32;

namespace EveryVideo.Dialogs;

public partial class ClipWindow : CardWindow
{
    private readonly string _source;
    private readonly long _length;
    private readonly Func<long> _now;
    private CancellationTokenSource? _cts;
    private string _folder;

    /// <summary>저장에 성공한 파일</summary>
    public string? SavedPath { get; private set; }

    public ClipWindow(string source, long length, long startMs, long endMs, Func<long> now)
    {
        InitializeComponent();
        _source = source;
        _length = length;
        _now = now;
        _folder = Settings.Current.VideoDir;
        SourceText.Text = RemoteSources.DisplayName(source);
        StartBox.Text = TimeFormat.Format(startMs, true);
        EndBox.Text = TimeFormat.Format(endMs, true);
        var baseName = Path.GetFileNameWithoutExtension(RemoteSources.DisplayName(source));
        foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        NameBox.Text = $"{baseName}_{TimeFormat.Format(startMs).Replace(':', '-')}.mp4";
        FolderText.Text = _folder;
        StartBox.TextChanged += (_, _) => UpdateLength();
        EndBox.TextChanged += (_, _) => UpdateLength();
        UpdateLength();
        Closing += (_, e) =>
        {
            if (_cts != null && !CardDialog.Confirm("구간 저장", "저장을 멈출까요?", "멈추기", "계속"))
                e.Cancel = true;
            else _cts?.Cancel();
        };
    }

    private void UpdateLength()
    {
        if (TimeFormat.TryParse(StartBox.Text, out var s) && TimeFormat.TryParse(EndBox.Text, out var e) && e > s)
            LengthText.Text = $"길이 {TimeFormat.Format(e - s, true)}";
        else
            LengthText.Text = "시간 형식: 분:초 (예: 01:23.5) 또는 시:분:초";
    }

    private void StartNow_Click(object sender, RoutedEventArgs e) => StartBox.Text = TimeFormat.Format(_now(), true);
    private void EndNow_Click(object sender, RoutedEventArgs e) => EndBox.Text = TimeFormat.Format(_now(), true);

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "구간 저장 위치", InitialDirectory = _folder };
        if (d.ShowDialog(this) != true) return;
        _folder = d.FolderName;
        FolderText.Text = _folder;
        Settings.Current.VideoFolder = _folder;
        Settings.Current.Save();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TimeFormat.TryParse(StartBox.Text, out var start) || !TimeFormat.TryParse(EndBox.Text, out var end) || end <= start)
        {
            StatusText.Text = "시작과 끝 시간을 확인해 주세요. 끝이 시작보다 뒤여야 합니다.";
            return;
        }
        if (_length > 0) end = Math.Min(end, _length);
        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = "clip.mp4";
        if (!name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) name += ".mp4";
        var output = Path.Combine(_folder, name);
        if (File.Exists(output) && !CardDialog.Confirm("구간 저장", "같은 이름의 파일이 있습니다. 덮어쓸까요?", "덮어쓰기"))
            return;

        SaveButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "저장하는 중…";
        _cts = new CancellationTokenSource();
        try
        {
            await Ffmpeg.CutAsync(_source, start, end, output, AccurateRadio.IsChecked == true, AudioCheck.IsChecked == true,
                new Progress<double>(p => Progress.Value = p), _cts.Token);
            SavedPath = output;
            _cts = null;
            Close();
        }
        catch (OperationCanceledException)
        {
            _cts = null;
        }
        catch (Exception ex)
        {
            _cts = null;
            Log.Write("clip", ex);
            StatusText.Text = ex.Message;
            SaveButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
