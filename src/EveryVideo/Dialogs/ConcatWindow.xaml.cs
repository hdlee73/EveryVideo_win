using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using EveryVideo.Services;
using Microsoft.Win32;

namespace EveryVideo.Dialogs;

public partial class ConcatWindow : CardWindow
{
    private readonly ObservableCollection<string> _files;
    private CancellationTokenSource? _cts;

    public string? SavedPath { get; private set; }

    public ConcatWindow(IEnumerable<string> initial)
    {
        InitializeComponent();
        _files = new ObservableCollection<string>(initial);
        FileList.ItemsSource = _files;
        ListHelpers.EnableDragReorder(FileList, _files);
        Closing += (_, e) =>
        {
            if (_cts != null && !CardDialog.Confirm("이어붙이기", "작업을 멈출까요?", "멈추기", "계속")) e.Cancel = true;
            else _cts?.Cancel();
        };
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Filter = RemoteSources.FileDialogFilter, Title = "이어붙일 동영상" };
        if (d.ShowDialog(this) == true) foreach (var f in d.FileNames) _files.Add(f);
    }

    private void FileList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files.Where(File.Exists)) _files.Add(f);
    }

    private string? _thumb;

    private void FileList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete) Remove_Click(sender, e);
    }

    private void Sort_Click(object sender, RoutedEventArgs e) => ListHelpers.ShowSortMenu(SortButton, kind =>
    {
        var sorted = ListHelpers.Sort(_files.ToList(), f => f, kind).ToList();
        _files.Clear();
        foreach (var f in sorted) _files.Add(f);
    });

    private void PickThumb_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Title = "썸네일 그림", Filter = "그림|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
            InitialDirectory = Settings.Current.CaptureDir,
        };
        if (d.ShowDialog(this) != true) return;
        _thumb = d.FileName;
        ThumbText.Text = Path.GetFileName(_thumb);
        ThumbOptions.IsEnabled = true;
        ClearThumbButton.Visibility = Visibility.Visible;
    }

    private void ClearThumb_Click(object sender, RoutedEventArgs e)
    {
        _thumb = null;
        ThumbText.Text = "(없음)";
        ThumbOptions.IsEnabled = false;
        ClearThumbButton.Visibility = Visibility.Collapsed;
    }

    private void Move(int delta)
    {
        var i = FileList.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _files.Count) return;
        _files.Move(i, j);
        FileList.SelectedIndex = j;
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is string s) _files.Remove(s);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count < (_thumb != null && IntroCheck.IsChecked == true ? 1 : 2))
        {
            StatusText.Text = "동영상을 두 개 이상 넣어 주세요.";
            return;
        }
        var d = new SaveFileDialog
        {
            Title = "합친 동영상 저장", Filter = "MP4 동영상|*.mp4", InitialDirectory = Settings.Current.VideoDir,
            FileName = $"합친영상_{DateTime.Now:yyyyMMdd_HHmmss}.mp4",
        };
        if (d.ShowDialog(this) != true) return;

        var (w, h) = SizeCombo.SelectedIndex switch
        {
            1 => (1920, 1080), 2 => (1280, 720), 3 => (854, 480), 4 => (1080, 1920), 5 => (720, 1280), _ => (0, 0),
        };
        SaveButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "합치는 중… 길이에 따라 시간이 걸립니다.";
        _cts = new CancellationTokenSource();
        try
        {
            var intro = _thumb != null && IntroCheck.IsChecked == true ? _thumb : null;
            var cover = _thumb != null && CoverCheck.IsChecked == true ? _thumb : null;
            var seconds = IntroSecCombo.SelectedIndex switch { 0 => 1, 2 => 3, 3 => 5, _ => 2 };
            await Ffmpeg.ConcatAsync(_files.ToList(), d.FileName, w, h, FastCheck.IsChecked == true,
                new Progress<double>(p => Progress.Value = p), _cts.Token, intro, seconds, cover);
            SavedPath = d.FileName;
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
            Log.Write("concat", ex);
            StatusText.Text = ex.Message;
            SaveButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
