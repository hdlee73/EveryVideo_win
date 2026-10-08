using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using EveryVideo.Dialogs;
using EveryVideo.Services;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using Path = System.IO.Path;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace EveryVideo;

public sealed class PlayItem : INotifyPropertyChanged
{
    private bool _isCurrent;

    public PlayItem(string location)
    {
        Location = location;
        Name = RemoteSources.DisplayName(location);
    }

    public string Location { get; }
    public string Name { get; }
    public string Display => RemoteSources.Key(Location);

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class Segment : INotifyPropertyChanged
{
    private bool _checked = true;

    public Segment(long start, long end)
    {
        Start = Math.Min(start, end);
        End = Math.Max(start, end);
    }

    public long Start { get; }
    public long End { get; }
    public string Range => $"{TimeFormat.Format(Start, true)} ~ {TimeFormat.Format(End, true)}";
    public string LengthText => $"길이 {TimeFormat.Format(End - Start, true)}";

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private static readonly float[] Speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f, 4f, 5f, 6f, 8f, 10f, 12f, 16f, 20f };
    private const float MaxRate = 20f;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    private readonly Settings _settings = Settings.Current;
    private readonly string[] _startupArgs;
    private readonly ObservableCollection<PlayItem> _playlist = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(Math.Min(400, GetDoubleClickTime())) };
    private readonly ObservableCollection<Segment> _segments = new();
    private bool _swRetry;
    private long _lastTime;
    private DateTime _lastLiveSeek;
    private long? _segmentDragStart;

    private LibVLC? _vlc;
    private MediaPlayer? _mp;
    private int _current = -1;
    private long _length;
    private long? _pendingSeek;
    private bool _seekDragging;
    private float _rate = 1f;
    private long _abA = -1, _abB = -1;
    private bool _fullscreen;
    private WindowState _prevState;
    private Rect _prevBounds;
    private double _zoom = 1, _zoomCx = 0.5, _zoomCy = 0.5;
    private Point? _panStart;
    private UpdateInfo? _update;

    // 탐색 미리보기
    private readonly Dictionary<long, BitmapSource> _thumbs = new();
    private long? _thumbWanted;
    private long _thumbHover = -1;
    private bool _thumbBusy;
    private CancellationTokenSource _thumbCts = new();

    public MainWindow(string[] args)
    {
        _startupArgs = args;
        InitializeComponent();
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        PlaylistBox.ItemsSource = _playlist;
        SegmentBox.ItemsSource = _segments;
        ListHelpers.EnableDragReorder(PlaylistBox, _playlist, RecomputeCurrent);
        _playlist.CollectionChanged += (_, _) => UpdatePlaylistCount();
        _segments.CollectionChanged += (_, _) => RebuildMarkers();
        UpdateSidePanelVisibility();

        // 시간 막대: Slider 가 클릭을 먼저 처리해 버리므로 처리된 이벤트도 받는다.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Seek_MouseDown), true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Seek_MouseUp), true);
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); TogglePlay(); };
        SidePanelMenu.IsChecked = _settings.ShowSidePanel;
        HwMenu.IsChecked = _settings.HardwareDecoding;
        VolumeSlider.Value = _settings.Volume;
        BrightnessSlider.Value = _settings.Brightness;
        ContrastSlider.Value = _settings.Contrast;
        SaturationSlider.Value = _settings.Saturation;
        UpdateRepeatButton();
        UpdateMuteButton();

        _tick.Tick += (_, _) => OnTick();
        _osdTimer.Tick += (_, _) => { Osd.Visibility = Visibility.Collapsed; _osdTimer.Stop(); };
        _idleTimer.Tick += (_, _) => HideFullscreenControls();

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnKey;
        Drop += Window_Drop;
        MarkerCanvas.SizeChanged += (_, _) => RebuildMarkers();
        BookmarkStore.Changed += () => Dispatcher.BeginInvoke(RefreshBookmarks);
    }

    // ================= 시작 / 끝 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _vlc = new LibVLC(
                "--no-video-title-show",
                "--no-snapshot-preview",
                "--snapshot-format=png",
                "--network-caching=1500",
                _settings.HardwareDecoding ? "--avcodec-hw=any" : "--avcodec-hw=none");
            _mp = new MediaPlayer(_vlc)
            {
                EnableHardwareDecoding = _settings.HardwareDecoding,
                EnableKeyInput = false,
                EnableMouseInput = false,
            };
            // libvlc 이벤트 안에서 바로 재생을 다시 시작하지 않도록 잠깐 뒤에 처리한다.
            _mp.EndReached += (_, _) => Task.Delay(60).ContinueWith(_ => Dispatcher.BeginInvoke(OnEnded));
            _mp.EncounteredError += (_, _) => Task.Delay(60).ContinueWith(_ => Dispatcher.BeginInvoke(OnError));
            _mp.Vout += (_, _) => Dispatcher.BeginInvoke(() => { ApplyAdjust(); ApplyZoom(); });
            _mp.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _mp?.SetRate(_rate);
                if (_mp == null) return;
                Log.Write("playing", null, CurrentItem?.Name);
                _mp.Volume = (int)VolumeSlider.Value;
                _mp.Mute = _settings.Muted;
            });
            VideoView.MediaPlayer = _mp;
        }
        catch (Exception ex)
        {
            Log.Write("libvlc init", ex);
            CardDialog.Show("재생 엔진을 시작하지 못했습니다", ex.Message, MessageKind.Error);
        }

        HookVideoHost();

        // 동영상 위 레이어(별도 창)에서도 단축키가 먹도록
        Dispatcher.BeginInvoke(() =>
        {
            if (Window.GetWindow(Overlay) is { } overlayWindow && !ReferenceEquals(overlayWindow, this))
                overlayWindow.PreviewKeyDown += OnKey;
        }, DispatcherPriority.ApplicationIdle);

        _tick.Start();
        var files = _startupArgs.Where(a => File.Exists(a) || Directory.Exists(a) || RemoteSources.IsUrl(a)).ToList();
        if (files.Count > 0) AddAndPlay(ExpandPaths(files));

        _ = CheckUpdateAtStartupAsync();
    }

    /// <summary>동영상 창의 빈 곳이 하얗게 보이지 않게 검게 칠한다.</summary>
    private void HookVideoHost(int attempt = 0)
    {
        try
        {
            if (VideoView.Template?.FindName("PART_PlayerHost", VideoView) is System.Windows.Interop.HwndHost host && host.Handle != IntPtr.Zero)
            {
                NativeVideoHost.MakeBlack(host.Handle);
                Log.Write("videohost ok");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write("video host", ex);
            return;
        }
        if (attempt < 40)
            Task.Delay(100).ContinueWith(_ => Dispatcher.BeginInvoke(() => HookVideoHost(attempt + 1)));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveResume();
        if (!_fullscreen && WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.Save();
        _tick.Stop();
        _thumbCts.Cancel();
        try
        {
            var mp = _mp;
            _mp = null;
            VideoView.MediaPlayer = null;
            mp?.Stop();
            mp?.Dispose();
            _vlc?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("close", ex);
        }
    }

    private async Task CheckUpdateAtStartupAsync()
    {
        if (!_settings.CheckUpdates) return;
        await Task.Delay(2500);
        var latest = await UpdateChecker.GetLatestAsync();
        if (latest == null || !UpdateChecker.IsNewer(latest.Version, AppInfo.Version)) return;
        _update = latest;
        UpdateBadge.Header = $"● 새 버전 v{latest.Version}";
        UpdateBadge.Visibility = Visibility.Visible;
        UpdateMenu.Header = $"새 버전 v{latest.Version} 내려받기";
        UpdateMenu.Visibility = Visibility.Visible;
        if (_settings.SkippedVersion == latest.Version) return;

        var notes = latest.Notes.Trim();
        if (notes.Length > 500) notes = notes[..500] + "…";
        var msg = $"EveryVideo 새 버전 v{latest.Version} 이(가) 나왔습니다. (지금 v{AppInfo.Version})" +
                  (notes.Length > 0 ? "\n\n" + notes : "");
        switch (CardDialog.Show("업데이트 알림", msg, MessageKind.Update, "내려받기", "나중에", "이 버전 건너뛰기"))
        {
            case 0:
                AppInfo.OpenUrl(latest.DownloadUrl ?? latest.PageUrl);
                break;
            case 2:
                _settings.SkippedVersion = latest.Version;
                _settings.Save();
                break;
        }
    }

    private void UpdateMenu_Click(object sender, RoutedEventArgs e) =>
        AppInfo.OpenUrl(_update?.DownloadUrl ?? _update?.PageUrl ?? AppInfo.LatestReleaseUrl);

    // ================= 재생목록 / 열기 =================

    private PlayItem? CurrentItem => _current >= 0 && _current < _playlist.Count ? _playlist[_current] : null;

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(p).Where(RemoteSources.IsMedia)
                        .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception e)
                {
                    Log.Write("folder", e, p);
                    continue;
                }
                foreach (var f in files) yield return f;
            }
            else yield return p;
        }
    }

    /// <summary>재생목록 끝에 넣고 첫 번째 것을 재생한다.</summary>
    private void AddAndPlay(IEnumerable<string> locations)
    {
        var first = -1;
        foreach (var l in locations)
        {
            _playlist.Add(new PlayItem(l));
            if (first < 0) first = _playlist.Count - 1;
        }
        if (first >= 0) PlayAt(first);
    }

    private void AddToPlaylist(IEnumerable<string> locations)
    {
        var wasEmpty = _playlist.Count == 0;
        foreach (var l in locations) _playlist.Add(new PlayItem(l));
        if (wasEmpty && _playlist.Count > 0) PlayAt(0);
    }

    private void PlayAt(int index, long? startMs = null, bool software = false)
    {
        if (_mp == null || _vlc == null || index < 0 || index >= _playlist.Count) return;
        SaveResume();
        if (CurrentItem != null) CurrentItem.IsCurrent = false;
        _current = index;
        var item = _playlist[index];
        item.IsCurrent = true;
        PlaylistBox.ScrollIntoView(item);

        // 새 동영상 상태로 초기화
        _length = 0;
        _abA = _abB = -1;
        UpdateAB();
        _zoom = 1;
        _zoomCx = _zoomCy = 0.5;
        ZoomSlider.Value = 1;
        _thumbCts.Cancel();
        _thumbCts = new CancellationTokenSource();
        _thumbs.Clear();
        _thumbWanted = null;
        PreviewImage.Source = null;
        SeekSlider.Maximum = 1;
        SeekSlider.Value = 0;

        var key = RemoteSources.Key(item.Location);
        if (!software)
        {
            _swRetry = false;
            _segments.Clear();
        }
        EndPanel.Visibility = Visibility.Collapsed;
        _pendingSeek = startMs ?? (_settings.Resume.TryGetValue(key, out var r) ? r : null);
        if (startMs == null && _pendingSeek != null)
            ShowOsd($"이어서 재생 {TimeFormat.Format(_pendingSeek.Value)}  (Home: 처음부터)", 3);

        try
        {
            using var media = RemoteSources.IsUrl(item.Location)
                ? new Media(_vlc, new Uri(item.Location))
                : new Media(_vlc, item.Location, FromType.FromPath);
            if (software) media.AddOption(":avcodec-hw=none");
            _mp.Play(media);
        }
        catch (Exception ex)
        {
            Log.Write("play", ex, item.Location);
            ShowOsd("열 수 없습니다: " + ex.Message);
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
        Title = $"{item.Name} - EveryVideo";
        if (!RemoteSources.IsUrl(item.Location) || !item.Location.Contains("@"))
        {
            _settings.AddRecent(item.Location);
            _settings.Save();
        }
        RefreshBookmarks();
    }

    private void OnError()
    {
        // 하드웨어 디코딩에서 실패하면 지금 위치부터 소프트웨어 디코딩으로 한 번 더 시도한다.
        if (_settings.HardwareDecoding && !_swRetry && _current >= 0)
        {
            _swRetry = true;
            Log.Write("play error", null, "retry without hw decoding at " + _lastTime);
            PlayAt(_current, _lastTime, software: true);
            ShowOsd("다른 방식으로 다시 재생합니다");
            return;
        }
        ShowOsd("재생할 수 없는 파일이거나 연결할 수 없습니다", 3);
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (_current >= 0) PlayAt(_current, 0);
    }

    private void OnEnded()
    {
        if (CurrentItem is { } item) _settings.Resume.Remove(RemoteSources.Key(item.Location));
        if (_length > 0)
        {
            SeekSlider.Value = SeekSlider.Maximum;
            TimeText.Text = TimeFormat.Format(_length);
        }
        switch (_settings.Repeat)
        {
            case RepeatMode.One:
                PlayAt(_current, 0);
                break;
            case RepeatMode.All:
                if (_playlist.Count > 0) PlayAt((_current + 1) % _playlist.Count, 0);
                break;
            default:
                if (_current + 1 < _playlist.Count) PlayAt(_current + 1, 0);
                else
                {
                    _mp?.Stop();
                    PlayButton.Tag = FindResource("IcPlay");
                    EndPanel.Visibility = Visibility.Visible;
                }
                break;
        }
    }

    private void SaveResume()
    {
        if (_mp == null || CurrentItem == null || _length <= 0) return;
        _settings.SetResume(RemoteSources.Key(CurrentItem.Location), _mp.Time, _length);
    }

    private void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Multiselect = true, Filter = RemoteSources.FileDialogFilter, Title = "동영상 열기",
            InitialDirectory = _settings.LastFolder ?? "",
        };
        if (d.ShowDialog(this) != true) return;
        _settings.LastFolder = Path.GetDirectoryName(d.FileNames[0]);
        AddAndPlay(d.FileNames);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "동영상 폴더 열기", InitialDirectory = _settings.LastFolder ?? "" };
        if (d.ShowDialog(this) != true) return;
        _settings.LastFolder = d.FolderName;
        var files = ExpandPaths(new[] { d.FolderName }).ToList();
        if (files.Count == 0) CardDialog.Show("폴더 열기", "이 폴더에는 동영상이 없습니다.");
        else AddAndPlay(files);
    }

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        var clip = "";
        try
        {
            if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText().Trim();
                if (RemoteSources.IsUrl(t) || t.StartsWith(@"\\")) clip = t;
            }
        }
        catch
        {
            // 클립보드를 못 읽어도 괜찮다.
        }
        var url = CardDialog.Prompt("주소 · 공유 링크 열기",
            "http(s), m3u8, rtsp, ftp://, smb://, \\\\서버\\공유 주소나 구글 드라이브 공유 링크를 붙여 넣으세요.",
            clip, null, "재생");
        if (string.IsNullOrWhiteSpace(url)) return;
        var loc = RemoteSources.Normalize(url);
        if (RemoteSources.IsDriveLink(url) && !loc.Contains("usercontent"))
        {
            CardDialog.Show("구글 드라이브", "링크에서 파일 ID 를 찾지 못했습니다. 파일의 공유 링크(…/file/d/…)를 넣어 주세요.", MessageKind.Error);
            return;
        }
        AddAndPlay(new[] { loc });
    }

    private void GoogleDrive_Click(object sender, RoutedEventArgs e)
    {
        var folder = RemoteSources.FindGoogleDriveFolder();
        if (folder == null)
        {
            var choice = CardDialog.Show("구글 드라이브",
                "이 PC 에서 'Google Drive for desktop' 드라이브를 찾지 못했습니다.\n\n" +
                "설치하면 내 드라이브가 탐색기 드라이브(G:)로 보이고, 파일을 내려받지 않고 바로 스트리밍 재생할 수 있습니다. " +
                "또는 '링크가 있는 모든 사용자'로 공유한 파일의 링크를 붙여 넣어 바로 재생할 수 있습니다.",
                MessageKind.Info, "공유 링크로 열기", "Google Drive 설치 페이지", "취소");
            if (choice == 0) OpenUrl_Click(sender, e);
            else if (choice == 1) AppInfo.OpenUrl("https://www.google.com/drive/download/");
            return;
        }
        // 파일을 고르지 않고 취소해도 된다.
        var d = new OpenFileDialog
        {
            Multiselect = true, Filter = RemoteSources.FileDialogFilter, Title = "구글 드라이브에서 열기",
            InitialDirectory = folder,
        };
        if (d.ShowDialog(this) == true) AddAndPlay(d.FileNames);
    }

    private void Remote_Click(object sender, RoutedEventArgs e)
    {
        var d = new RemoteWindow();
        if (d.ShowDialog() == true && d.Selected.Count > 0) AddAndPlay(d.Selected);
    }

    private void RecentMenu_Opened(object sender, RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        if (_settings.RecentFiles.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(없음)", IsEnabled = false });
            return;
        }
        foreach (var f in _settings.RecentFiles)
        {
            var mi = new MenuItem { Header = RemoteSources.DisplayName(f).Replace("_", "__"), ToolTip = f };
            mi.Click += (_, _) => AddAndPlay(new[] { f });
            RecentMenu.Items.Add(mi);
        }
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "목록 지우기" };
        clear.Click += (_, _) => { _settings.RecentFiles.Clear(); _settings.Save(); };
        RecentMenu.Items.Add(clear);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            var list = ExpandPaths(files).Where(f => !File.Exists(f) || RemoteSources.IsMedia(f) || Path.GetExtension(f).Length > 0).ToList();
            var subs = list.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".srt" or ".smi" or ".ass" or ".ssa" or ".vtt" or ".sub").ToList();
            if (subs.Count > 0 && _mp != null)
            {
                foreach (var s in subs) _mp.AddSlave(MediaSlaveType.Subtitle, new Uri(s).AbsoluteUri, true);
                ShowOsd("자막을 불러왔습니다");
                list = list.Except(subs).ToList();
            }
            if (list.Count > 0) AddAndPlay(list);
        }
        else if (e.Data.GetData(DataFormats.Text) is string text && RemoteSources.IsUrl(text.Trim()))
        {
            e.Handled = true;
            AddAndPlay(new[] { RemoteSources.Normalize(text) });
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Filter = RemoteSources.FileDialogFilter, Title = "재생목록에 추가" };
        if (d.ShowDialog(this) == true) AddToPlaylist(d.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "폴더의 동영상 추가" };
        if (d.ShowDialog(this) == true) AddToPlaylist(ExpandPaths(new[] { d.FolderName }));
    }

    private void PlaylistBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedItem is PlayItem item) PlayAt(_playlist.IndexOf(item), null);
    }

    private void PlaylistBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { PlaylistRemove_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter) { PlaylistBox_DoubleClick(sender, null!); e.Handled = true; }
    }

    private void MovePlaylist(int delta)
    {
        if (PlaylistBox.SelectedItem is not PlayItem item) return;
        var i = _playlist.IndexOf(item);
        var j = i + delta;
        if (j < 0 || j >= _playlist.Count) return;
        _playlist.Move(i, j);
        _current = CurrentItem == null ? -1 : _playlist.IndexOf(_playlist.First(p => p.IsCurrent));
        PlaylistBox.SelectedItem = item;
    }

    private void PlaylistUp_Click(object sender, RoutedEventArgs e) => MovePlaylist(-1);
    private void PlaylistDown_Click(object sender, RoutedEventArgs e) => MovePlaylist(1);

    private void RecomputeCurrent()
    {
        var cur = _playlist.FirstOrDefault(p => p.IsCurrent);
        _current = cur == null ? -1 : _playlist.IndexOf(cur);
    }

    private void UpdatePlaylistCount()
    {
        PlaylistCount.Text = _playlist.Count == 0 ? "끌어서 순서 바꾸기 · × 로 빼기" : $"{_playlist.Count}개 · 끌어서 순서 바꾸기";
    }

    private void PlaylistSort_Click(object sender, RoutedEventArgs e) => ListHelpers.ShowSortMenu(PlaylistSortButton, kind =>
    {
        var sorted = ListHelpers.Sort(_playlist.ToList(), p => p.Location, kind).ToList();
        _playlist.Clear();
        foreach (var p in sorted) _playlist.Add(p);
        RecomputeCurrent();
    });

    private void PlaylistItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlayItem item) return;
        PlaylistBox.SelectedItems.Clear();
        PlaylistBox.SelectedItem = item;
        PlaylistRemove_Click(sender, e);
    }

    private void PlaylistRemove_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentItem;
        foreach (var item in PlaylistBox.SelectedItems.Cast<PlayItem>().ToList()) _playlist.Remove(item);
        _current = current != null ? _playlist.IndexOf(current) : -1;
        if (current != null && _current < 0)
        {
            SaveResume();
            _mp?.Stop();
            ShowEmptyIfIdle();
        }
    }

    private void PlaylistClear_Click(object sender, RoutedEventArgs e)
    {
        SaveResume();
        _mp?.Stop();
        _playlist.Clear();
        _current = -1;
        ShowEmptyIfIdle();
    }

    private void ShowEmptyIfIdle()
    {
        _length = 0;
        Title = "EveryVideo";
        EmptyPanel.Visibility = Visibility.Visible;
        RefreshBookmarks();
    }

    // ================= 재생 조작 =================

    private void OnTick()
    {
        if (_mp == null) return;
        // 시간 막대 밖에서 버튼을 놓아 MouseUp 을 못 받은 경우
        if (_seekDragging && Mouse.LeftButton == MouseButtonState.Released) Seek_MouseUp(this, null!);
        var playing = _mp.IsPlaying;
        PlayButton.Tag = FindResource(playing ? "IcPause" : "IcPlay");
        var len = _mp.Length;
        if (len > 0 && len != _length)
        {
            _length = len;
            SeekSlider.Maximum = len;
            DurationText.Text = TimeFormat.Format(len);
            RebuildMarkers();
        }
        var t = _mp.Time;
        if (t > 0) _lastTime = t;
        if (_pendingSeek is long target && playing && _mp.IsSeekable)
        {
            _pendingSeek = null;
            if (target > 0 && (len <= 0 || target < len - 1000)) _mp.Time = target;
            return;
        }
        if (_abA >= 0 && _abB > _abA && t >= _abB) _mp.Time = _abA;
        if (!_seekDragging && t >= 0)
        {
            SeekSlider.Value = Math.Min(t, SeekSlider.Maximum);
            TimeText.Text = TimeFormat.Format(t);
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_mp == null) return;
        if (_current < 0)
        {
            if (_playlist.Count > 0) PlayAt(0);
            else OpenFiles_Click(this, new RoutedEventArgs());
            return;
        }
        if (_mp.State is VLCState.Ended or VLCState.Stopped or VLCState.NothingSpecial) PlayAt(_current, 0);
        else _mp.Pause();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        SaveResume();
        _mp?.Stop();
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_mp != null && _mp.Time > 3000 && _current >= 0) { _mp.Time = 0; return; }
        if (_current > 0) PlayAt(_current - 1);
        else if (_settings.Repeat == RepeatMode.All && _playlist.Count > 0) PlayAt(_playlist.Count - 1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_current + 1 < _playlist.Count) PlayAt(_current + 1);
        else if (_settings.Repeat == RepeatMode.All && _playlist.Count > 0) PlayAt(0);
    }

    private void SeekBy(long deltaMs)
    {
        if (_mp == null || !_mp.IsSeekable) return;
        var t = ClampToAB(Math.Clamp(_mp.Time + deltaMs, 0, Math.Max(0, _length - 500)));
        _mp.Time = t;
        SeekSlider.Value = t;
        ShowOsd($"{(deltaMs > 0 ? "▶▶" : "◀◀")} {TimeFormat.Format(t)} / {TimeFormat.Format(_length)}");
    }

    private void Back10_Click(object sender, RoutedEventArgs e) => SeekBy(-10_000);
    private void Fwd10_Click(object sender, RoutedEventArgs e) => SeekBy(10_000);

    /// <summary>구간반복(A-B) 중에는 그 구간 밖으로 나가지 않는다.</summary>
    private long ClampToAB(long ms) => _abA >= 0 && _abB > _abA ? Math.Clamp(ms, _abA, _abB - 200) : ms;

    private void SeekTo(long ms)
    {
        if (_mp == null) return;
        ms = ClampToAB(ms);
        if (!_mp.IsPlaying && _mp.State is VLCState.Ended or VLCState.Stopped) { PlayAt(_current, ms); return; }
        _mp.Time = Math.Clamp(ms, 0, Math.Max(0, _length - 200));
        SeekSlider.Value = ms;
        TimeText.Text = TimeFormat.Format(ms);
    }

    // ---- 시간 막대 + 미리보기 ----

    private long TrackValue(MouseEventArgs e)
    {
        if (SeekSlider.Template.FindName("PART_Track", SeekSlider) is not Track track) return (long)SeekSlider.Value;
        return (long)Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), 0, Math.Max(0, _length));
    }

    private void Seek_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_length <= 0) return;
        var ms = TrackValue(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Shift+끌기: 구간 목록에 구간을 더한다.
            _segmentDragStart = ms;
            SeekSlider.CaptureMouse();
            e.Handled = true;
            DrawSegmentPreview(ms, ms);
            return;
        }
        _seekDragging = true;
        // 누른 곳으로 바로 옮긴다 (손잡이가 아닌 곳을 눌러도)
        SeekSlider.Value = ms;
        _lastLiveSeek = DateTime.MinValue;
    }

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_segmentDragStart is long start)
        {
            _segmentDragStart = null;
            SeekSlider.ReleaseMouseCapture();
            var end = TrackValue(e);
            if (Math.Abs(end - start) >= 300) AddSegment(start, end);
            RebuildMarkers();
            return;
        }
        if (!_seekDragging) return;
        _seekDragging = false;
        SeekTo((long)SeekSlider.Value);
        if (!SeekSlider.IsMouseOver) PreviewPopup.IsOpen = false;
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_seekDragging) return;
        TimeText.Text = TimeFormat.Format((long)e.NewValue);
        ShowPreview((long)e.NewValue);
        // 끄는 동안에도 화면이 따라오도록 잠깐씩 이동
        if (_mp != null && _mp.IsSeekable && (DateTime.Now - _lastLiveSeek).TotalMilliseconds > 250)
        {
            _lastLiveSeek = DateTime.Now;
            _mp.Time = ClampToAB((long)e.NewValue);
        }
    }

    private void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        if (_segmentDragStart is long segStart)
        {
            var now = TrackValue(e);
            DrawSegmentPreview(segStart, now);
            ShowPreview(now, e.GetPosition(SeekSlider).X);
            return;
        }
        if (_length <= 0 || _seekDragging) return;
        if (SeekSlider.Template.FindName("PART_Track", SeekSlider) is not Track track) return;
        var ms = (long)Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), 0, _length);
        ShowPreview(ms, e.GetPosition(SeekSlider).X);
    }

    private void Seek_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_seekDragging) PreviewPopup.IsOpen = false;
    }

    private void ShowPreview(long ms, double? x = null)
    {
        if (_length <= 0 || CurrentItem == null) return;
        if (x == null)
        {
            // 끄는 중에는 손잡이 위치를 쓴다.
            var w = SeekSlider.ActualWidth - 14;
            x = 7 + w * ms / Math.Max(1, _length);
        }
        PreviewTime.Text = TimeFormat.Format(ms);
        var hasFfmpeg = Ffmpeg.Available;
        PreviewFrame.Visibility = hasFfmpeg ? Visibility.Visible : Visibility.Collapsed;
        PreviewPopup.HorizontalOffset = x.Value - (hasFfmpeg ? 100 : 30);
        PreviewPopup.VerticalOffset = hasFfmpeg ? -140 : -36;
        PreviewPopup.IsOpen = true;
        if (hasFfmpeg) RequestThumb(ms);
    }

    private long ThumbBucket => Math.Max(1000, _length / 400);

    private async void RequestThumb(long ms)
    {
        var key = ms / ThumbBucket;
        _thumbHover = key;
        if (_thumbs.TryGetValue(key, out var cached))
        {
            PreviewImage.Source = cached;
            return;
        }
        _thumbWanted = key;
        if (_thumbBusy) return;
        _thumbBusy = true;
        var location = CurrentItem!.Location;
        var ct = _thumbCts.Token;
        try
        {
            while (_thumbWanted is long k && !ct.IsCancellationRequested)
            {
                _thumbWanted = null;
                if (_thumbs.ContainsKey(k)) continue;
                var bytes = await Ffmpeg.FrameAsync(location, k * ThumbBucket + ThumbBucket / 2, 192, ct);
                if (bytes == null || ct.IsCancellationRequested) continue;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                if (_thumbs.Count > 500) _thumbs.Clear();
                _thumbs[k] = bmp;
                if (_thumbHover == k || _thumbWanted == null) PreviewImage.Source = bmp;
            }
        }
        catch (Exception ex)
        {
            Log.Write("thumb", ex);
        }
        finally
        {
            _thumbBusy = false;
        }
    }

    // ---- 즐겨찾기 / A-B 표시 ----

    private void RebuildMarkers()
    {
        MarkerCanvas.Children.Clear();
        if (_length <= 0) return;
        var w = MarkerCanvas.ActualWidth - 14;
        double X(long ms) => 7 + w * ms / _length;

        if (_abA >= 0)
        {
            var a = X(_abA);
            var b = _abB > _abA ? X(_abB) : a + 2;
            var band = new Rectangle
            {
                Width = Math.Max(2, b - a), Height = 8, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                Fill = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xB3, 0x00)),
            };
            Canvas.SetLeft(band, a);
            Canvas.SetTop(band, 13);
            MarkerCanvas.Children.Add(band);
        }

        SegmentCanvas.Children.Clear();
        foreach (var seg in _segments)
        {
            var a = X(seg.Start);
            var rect = new Rectangle
            {
                Width = Math.Max(2, X(seg.End) - a), Height = 14, RadiusX = 3, RadiusY = 3,
                Fill = new SolidColorBrush(seg.Checked ? Color.FromArgb(0x99, 0x4F, 0xC3, 0xF7) : Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            };
            Canvas.SetLeft(rect, a);
            Canvas.SetTop(rect, 10);
            SegmentCanvas.Children.Add(rect);
        }

        if (CurrentItem == null) return;
        foreach (var bm in BookmarkStore.For(RemoteSources.Key(CurrentItem.Location)))
        {
            var mark = new Polygon
            {
                Points = new PointCollection { new(0, 0), new(10, 0), new(5, 7) },
                Fill = (Brush)FindResource("Accent"),
                Cursor = Cursors.Hand,
                ToolTip = $"{bm.Time}  {bm.Note}".Trim(),
            };
            var pos = bm.PositionMs;
            mark.MouseLeftButtonDown += (_, e) => { SeekTo(pos); e.Handled = true; };
            Canvas.SetLeft(mark, X(pos) - 5);
            Canvas.SetTop(mark, 0);
            MarkerCanvas.Children.Add(mark);
        }
    }

    private void RefreshBookmarks()
    {
        var list = CurrentItem == null ? new List<Bookmark>() : BookmarkStore.For(RemoteSources.Key(CurrentItem.Location));
        BookmarkBox.ItemsSource = list;
        BookmarkEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildMarkers();
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_mp == null || CurrentItem == null || _length <= 0) return;
        var t = _mp.Time;
        var bm = new Bookmark
        {
            Media = RemoteSources.Key(CurrentItem.Location), Title = CurrentItem.Name, PositionMs = t,
        };
        BookmarkStore.Add(bm);
        ShowOsd($"★ {TimeFormat.Format(t)} 즐겨찾기 했습니다");
        if (_settings.ShowSidePanel) BookmarkTab.IsChecked = true;
    }

    private void EditBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkBox.SelectedItem is not Bookmark b) return;
        var note = CardDialog.Prompt("메모", $"{b.Title} · {b.Time}", b.Note, null, "저장");
        if (note == null) return;
        b.Note = note.Trim();
        BookmarkStore.Update();
    }

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkBox.SelectedItem is Bookmark b) BookmarkStore.Remove(b);
    }

    private void BookmarkBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BookmarkBox.SelectedItem is Bookmark b) SeekTo(b.PositionMs);
    }

    private void AllBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var d = new BookmarksWindow();
        if (d.ShowDialog() != true || d.Chosen is not { } b) return;
        // 지금 목록에 있으면 그것을, 없으면 새로 넣어 해당 시점부터 재생
        var idx = _playlist.ToList().FindIndex(p => RemoteSources.Key(p.Location).Equals(b.Media, StringComparison.OrdinalIgnoreCase));
        if (idx == _current && idx >= 0) { SeekTo(b.PositionMs); return; }
        if (idx < 0)
        {
            if (!RemoteSources.IsUrl(b.Media) && !File.Exists(b.Media))
            {
                CardDialog.Show("즐겨찾기", $"파일을 찾을 수 없습니다.\n{b.Media}", MessageKind.Error);
                return;
            }
            var loc = b.Media;
            // FTP 는 등록된 서버의 계정으로 다시 연결한다.
            if (loc.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(loc, UriKind.Absolute, out var u))
            {
                var server = _settings.Servers.FirstOrDefault(s => s.Kind == "ftp" && s.Host.Equals(u.Host, StringComparison.OrdinalIgnoreCase));
                if (server != null) loc = RemoteSources.FtpUrl(server, Uri.UnescapeDataString(u.AbsolutePath));
            }
            _playlist.Add(new PlayItem(loc));
            idx = _playlist.Count - 1;
        }
        PlayAt(idx, b.PositionMs);
    }

    // ---- 구간반복 / 반복 / 속도 ----

    private void AB_Click(object sender, RoutedEventArgs e)
    {
        if (_mp == null || _length <= 0) return;
        var t = _mp.Time;
        if (_abA < 0)
        {
            _abA = t;
            ShowOsd($"구간반복 시작 A = {TimeFormat.Format(t)}  (한 번 더 누르면 B)");
        }
        else if (_abB < 0)
        {
            if (t <= _abA + 300)
            {
                ShowOsd("B 는 A 보다 뒤여야 합니다");
                return;
            }
            _abB = t;
            _mp.Time = _abA;
            ShowOsd($"구간반복 {TimeFormat.Format(_abA)} ~ {TimeFormat.Format(_abB)}");
        }
        else
        {
            _abA = _abB = -1;
            ShowOsd("구간반복 해제");
        }
        UpdateAB();
    }

    private void UpdateAB()
    {
        ABButton.Content = _abA < 0 ? "A-B" : _abB < 0 ? "A-…" : "A-B ✓";
        ABButton.Foreground = (Brush)FindResource(_abA < 0 ? "Text" : "Accent");
        RebuildMarkers();
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _settings.Repeat = _settings.Repeat switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.None,
        };
        UpdateRepeatButton();
        ShowOsd(_settings.Repeat switch
        {
            RepeatMode.All => "전체 반복",
            RepeatMode.One => "한 개 반복",
            _ => "반복 안 함",
        });
    }

    private void UpdateRepeatButton()
    {
        RepeatButton.Tag = FindResource(_settings.Repeat == RepeatMode.One ? "IcRepeatOne" : "IcRepeat");
        RepeatButton.Foreground = (Brush)FindResource(_settings.Repeat == RepeatMode.None ? "TextDim" : "Accent");
        RepeatButton.ToolTip = _settings.Repeat switch
        {
            RepeatMode.All => "전체 반복 (L)",
            RepeatMode.One => "한 개 반복 (L)",
            _ => "반복 안 함 (L)",
        };
    }

    private void SetRate(float rate)
    {
        _rate = Math.Clamp(rate, 0.25f, MaxRate);
        _mp?.SetRate(_rate);
        SpeedButton.Content = $"{_rate:0.0#}x";
        var normal = Math.Abs(_rate - 1f) < 0.01;
        SpeedButton.Foreground = (Brush)FindResource(normal ? "Text" : "Accent");
        SpeedResetButton.Visibility = normal ? Visibility.Collapsed : Visibility.Visible;
        ShowOsd($"재생 속도 {_rate:0.0#}배");
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SpeedButton, Placement = PlacementMode.Top };
        foreach (var s in Speeds)
        {
            var mi = new MenuItem { Header = $"{s:0.0#}x", IsCheckable = true, IsChecked = Math.Abs(s - _rate) < 0.01 };
            mi.Click += (_, _) => SetRate(s);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void SpeedButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        SetRate(1f);
        e.Handled = true;
    }

    private void SpeedUp_Click(object sender, RoutedEventArgs e) => SetRate(Speeds.FirstOrDefault(s => s > _rate + 0.01f, MaxRate));
    private void SpeedDown_Click(object sender, RoutedEventArgs e) => SetRate(Speeds.LastOrDefault(s => s < _rate - 0.01f, 0.25f));
    private void SpeedReset_Click(object sender, RoutedEventArgs e) => SetRate(1f);

    // ---- 볼륨 ----

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = (int)Math.Round(e.NewValue);
        if (VolumeText != null) VolumeText.Text = $"{v}%";
        if (_mp != null) _mp.Volume = v;
        _settings.Volume = v;
    }

    private void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 200);
        if (_settings.Muted) Mute_Click(this, new RoutedEventArgs());
        ShowOsd($"볼륨 {(int)VolumeSlider.Value}%");
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _settings.Muted = !_settings.Muted;
        UpdateMuteButton();
        ShowOsd(_settings.Muted ? "음소거" : $"볼륨 {(int)VolumeSlider.Value}%");
    }

    private void UpdateMuteButton()
    {
        if (_mp != null) _mp.Mute = _settings.Muted;
        MuteButton.Tag = FindResource(_settings.Muted ? "IcVolumeOff" : "IcVolume");
    }

    // ---- 밝기 / 색 ----

    private void Brightness_Click(object sender, RoutedEventArgs e) => BrightnessPopup.IsOpen = true;

    private void Adjust_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValue == null || ContrastValue == null || SaturationValue == null) return;
        _settings.Brightness = (int)BrightnessSlider.Value;
        _settings.Contrast = (int)ContrastSlider.Value;
        _settings.Saturation = (int)SaturationSlider.Value;
        BrightnessValue.Text = Signed(_settings.Brightness);
        ContrastValue.Text = Signed(_settings.Contrast);
        SaturationValue.Text = Signed(_settings.Saturation);
        ApplyAdjust();
    }

    private static string Signed(int v) => v > 0 ? "+" + v : v.ToString();

    private void AdjustReset_Click(object sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value = ContrastSlider.Value = SaturationSlider.Value = 0;
    }

    /// <summary>
    /// 밝기는 VLC 밝기와 감마를 함께 올려 어두운 장면도 충분히 밝아지게 한다.
    /// 최대(+100) 이면 밝기 2.0 · 감마 2.6 으로, 원본보다 훨씬 밝게 보인다.
    /// </summary>
    private void ApplyAdjust()
    {
        if (_mp == null) return;
        var b = _settings.Brightness / 100f;
        var c = _settings.Contrast / 100f;
        var s = _settings.Saturation / 100f;
        var on = b != 0 || c != 0 || s != 0;
        _mp.SetAdjustInt(VideoAdjustOption.Enable, on ? 1 : 0);
        if (!on) return;
        _mp.SetAdjustFloat(VideoAdjustOption.Brightness, 1f + b * (b > 0 ? 1.0f : 0.8f));
        _mp.SetAdjustFloat(VideoAdjustOption.Gamma, 1f + b * (b > 0 ? 1.6f : 0.5f));
        _mp.SetAdjustFloat(VideoAdjustOption.Contrast, 1f + c * (c > 0 ? 1.0f : 0.8f));
        _mp.SetAdjustFloat(VideoAdjustOption.Saturation, 1f + s);
    }

    // ---- 음성 · 자막 / 화면 비율 ----

    private void Tracks_Click(object sender, RoutedEventArgs e)
    {
        if (_mp == null) return;
        var menu = new ContextMenu { PlacementTarget = TracksButton, Placement = PlacementMode.Top };
        menu.Items.Add(new MenuItem { Header = "음성", IsEnabled = false, FontWeight = FontWeights.Bold });
        var audio = _mp.AudioTrackDescription;
        if (audio.Length == 0) menu.Items.Add(new MenuItem { Header = "  (없음)", IsEnabled = false });
        foreach (var t in audio)
        {
            var id = t.Id;
            var mi = new MenuItem { Header = "  " + t.Name, IsCheckable = true, IsChecked = _mp.AudioTrack == id };
            mi.Click += (_, _) => _mp?.SetAudioTrack(id);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "자막", IsEnabled = false, FontWeight = FontWeights.Bold });
        foreach (var t in _mp.SpuDescription)
        {
            var id = t.Id;
            var mi = new MenuItem { Header = "  " + (id < 0 ? "자막 끄기" : t.Name), IsCheckable = true, IsChecked = _mp.Spu == id };
            mi.Click += (_, _) => _mp?.SetSpu(id);
            menu.Items.Add(mi);
        }
        var load = new MenuItem { Header = "  자막 파일 불러오기…" };
        load.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "자막|*.srt;*.smi;*.ass;*.ssa;*.vtt;*.sub;*.idx|모든 파일|*.*", Title = "자막 파일" };
            if (d.ShowDialog(this) == true && _mp != null)
            {
                _mp.AddSlave(MediaSlaveType.Subtitle, new Uri(d.FileName).AbsoluteUri, true);
                ShowOsd("자막을 불러왔습니다");
            }
        };
        menu.Items.Add(load);
        var later = new MenuItem { Header = $"  자막 0.5초 늦게  (지금 {_mp.SpuDelay / 1_000_000.0:+0.0;-0.0;0}초)" };
        later.Click += (_, _) => { if (_mp != null) { _mp.SetSpuDelay(_mp.SpuDelay + 500_000); ShowOsd($"자막 싱크 {_mp.SpuDelay / 1_000_000.0:+0.0;-0.0;0}초"); } };
        var earlier = new MenuItem { Header = "  자막 0.5초 빠르게" };
        earlier.Click += (_, _) => { if (_mp != null) { _mp.SetSpuDelay(_mp.SpuDelay - 500_000); ShowOsd($"자막 싱크 {_mp.SpuDelay / 1_000_000.0:+0.0;-0.0;0}초"); } };
        menu.Items.Add(later);
        menu.Items.Add(earlier);
        menu.IsOpen = true;
    }

    private void Aspect_Click(object sender, RoutedEventArgs e)
    {
        if (_mp == null) return;
        var menu = new ContextMenu { PlacementTarget = AspectButton, Placement = PlacementMode.Top };
        var options = new (string label, string? value)[]
        {
            ("원본 비율", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("2.35:1", "2.35:1"),
            ("1:1", "1:1"), ("9:16 (세로)", "9:16"), ("창에 꽉 채우기", "fill"),
        };
        foreach (var (label, value) in options)
        {
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = _mp.AspectRatio == value || (value == "fill" && _fillAspect) };
            mi.Click += (_, _) =>
            {
                if (_mp == null) return;
                _fillAspect = value == "fill";
                _mp.AspectRatio = _fillAspect ? FillAspect() : value;
                ShowOsd("화면 비율: " + label);
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private bool _fillAspect;

    private string FillAspect() => $"{Math.Max(1, (int)VideoView.ActualWidth)}:{Math.Max(1, (int)VideoView.ActualHeight)}";

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fillAspect && _mp != null) Dispatcher.BeginInvoke(() => { if (_mp != null) _mp.AspectRatio = FillAspect(); }, DispatcherPriority.Background);
    }

    // ---- 확대 ----

    private void Zoom_Click(object sender, RoutedEventArgs e) => ZoomPopup.IsOpen = true;

    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomValue == null) return;
        _zoom = e.NewValue;
        ZoomValue.Text = $"{_zoom:0.0}배";
        ApplyZoom();
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        _zoomCx = _zoomCy = 0.5;
        ZoomSlider.Value = 1;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_mp == null) return;
        if (_zoom <= 1.01)
        {
            _mp.CropGeometry = null;
            return;
        }
        // 자르기 좌표는 회전 전 원본 프레임 기준이다. 휴대폰 세로 영상처럼 회전 정보가 있으면
        // 화면에서 보는 중심점을 원본 좌표로 바꿔야 위아래/좌우가 엉뚱하게 잘리지 않는다.
        if (!TryGetSourceVideo(out var w, out var h, out var orientation)) return;
        var half = 0.5 / _zoom;
        _zoomCx = Math.Clamp(_zoomCx, half, 1 - half);
        _zoomCy = Math.Clamp(_zoomCy, half, 1 - half);
        var (sx, sy) = orientation switch
        {
            VideoOrientation.RightTop => (_zoomCy, 1 - _zoomCx),      // 90도 회전
            VideoOrientation.BottomRight => (1 - _zoomCx, 1 - _zoomCy), // 180도
            VideoOrientation.LeftBottom => (1 - _zoomCy, _zoomCx),     // 270도
            _ => (_zoomCx, _zoomCy),
        };
        var cw = (int)(w / _zoom);
        var ch = (int)(h / _zoom);
        var x = (int)Math.Clamp(sx * w - cw / 2.0, 0, w - cw);
        var y = (int)Math.Clamp(sy * h - ch / 2.0, 0, h - ch);
        _mp.CropGeometry = $"{cw}x{ch}+{x}+{y}";
    }

    private bool TryGetSourceVideo(out uint width, out uint height, out VideoOrientation orientation)
    {
        width = height = 0;
        orientation = VideoOrientation.TopLeft;
        try
        {
            var media = _mp?.Media;
            if (media != null)
                foreach (var t in media.Tracks)
                {
                    if (t.TrackType != TrackType.Video || t.Data.Video.Width == 0) continue;
                    width = t.Data.Video.Width;
                    height = t.Data.Video.Height;
                    orientation = t.Data.Video.Orientation;
                    return true;
                }
        }
        catch (Exception e)
        {
            Log.Write("tracks", e);
        }
        if (_mp == null || !_mp.Size(0, ref width, ref height) || width == 0 || height == 0) return false;
        return true;
    }

    private void ZoomAt(Point p, double factor)
    {
        var nx = p.X / Math.Max(1, Overlay.ActualWidth);
        var ny = p.Y / Math.Max(1, Overlay.ActualHeight);
        // 지금 보이는 영역에서 마우스가 가리키는 곳을 새 중심으로
        var half = 0.5 / _zoom;
        var targetX = _zoomCx - half + nx * 2 * half;
        var targetY = _zoomCy - half + ny * 2 * half;
        var newZoom = Math.Clamp(_zoom * factor, 1, 4);
        var newHalf = 0.5 / newZoom;
        _zoomCx = targetX - (nx - 0.5) * 2 * newHalf;
        _zoomCy = targetY - (ny - 0.5) * 2 * newHalf;
        ZoomSlider.Value = newZoom;
        ShowOsd($"확대 {newZoom:0.0}배");
    }

    // ---- 화면 위 마우스 ----

    private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ZoomAt(e.GetPosition(Overlay), e.Delta > 0 ? 1.15 : 1 / 1.15);
        else ChangeVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 두 번 클릭은 전체화면. 한 번 클릭으로 예약한 재생/정지는 취소.
            _clickTimer.Stop();
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (_zoom > 1.01)
        {
            _panStart = e.GetPosition(Overlay);
            Overlay.CaptureMouse();
        }
        if (_current >= 0 && EmptyPanel.Visibility != Visibility.Visible && EndPanel.Visibility != Visibility.Visible)
        {
            _clickTimer.Stop();
            _clickTimer.Start();
        }
    }

    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart != null)
        {
            _panStart = null;
            Overlay.ReleaseMouseCapture();
        }
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_fullscreen) ShowFullscreenControls();
        if (_panStart is Point start && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(Overlay);
            var dx = (p.X - start.X) / Math.Max(1, Overlay.ActualWidth) / _zoom;
            var dy = (p.Y - start.Y) / Math.Max(1, Overlay.ActualHeight) / _zoom;
            if (Math.Abs(dx) + Math.Abs(dy) < 0.002) return;
            _clickTimer.Stop();
            _zoomCx -= dx;
            _zoomCy -= dy;
            _panStart = p;
            ApplyZoom();
        }
    }

    private void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string header, RoutedEventHandler h, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Click += h;
            menu.Items.Add(mi);
        }
        Add("파일 열기…", OpenFiles_Click, "Ctrl+O");
        Add("주소 · 링크 열기…", OpenUrl_Click, "Ctrl+U");
        menu.Items.Add(new Separator());
        Add(_fullscreen ? "전체화면 끝내기" : "전체화면", Fullscreen_Click, "F");
        Add("화면 캡쳐", Capture_Click, "C");
        Add("구간 저장…", Clip_Click, "S");
        Add("현재 시점 즐겨찾기", AddBookmark_Click, "B");
        Add("구간반복 A-B", AB_Click, "R");
        menu.Items.Add(new Separator());
        Add("음성 · 자막…", Tracks_Click);
        Add("밝기 · 색 조절…", Brightness_Click);
        Add("확대 원래대로", ZoomReset_Click, "0");
        menu.IsOpen = true;
    }

    // ================= 전체화면 =================

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _fullscreen = true;
            _prevState = WindowState;
            _prevBounds = new Rect(Left, Top, Width, Height);
            TopMenu.Visibility = Visibility.Collapsed;
            SidePanel.Visibility = Visibility.Collapsed;
            SideSplitter.Visibility = Visibility.Collapsed;
            SideColumn.Width = new GridLength(0);
            ControlsHost.Child = null;
            FsControlsHost.Child = ControlBar;
            ControlsHost.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            FullscreenButton.Tag = FindResource("IcFullscreenExit");
            ShowFullscreenControls();
        }
        else
        {
            _fullscreen = false;
            _idleTimer.Stop();
            Overlay.Cursor = null;
            FsControlsHost.Child = null;
            FsControlsHost.Visibility = Visibility.Collapsed;
            ControlsHost.Child = ControlBar;
            ControlsHost.Visibility = Visibility.Visible;
            TopMenu.Visibility = Visibility.Visible;
            UpdateSidePanelVisibility();
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _prevState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            if (_prevState != WindowState.Maximized)
            {
                Left = _prevBounds.Left;
                Top = _prevBounds.Top;
                Width = _prevBounds.Width;
                Height = _prevBounds.Height;
            }
            FullscreenButton.Tag = FindResource("IcFullscreen");
        }
    }

    private void ShowFullscreenControls()
    {
        if (!_fullscreen) return;
        FsControlsHost.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void HideFullscreenControls()
    {
        _idleTimer.Stop();
        if (!_fullscreen) return;
        if (FsControlsHost.IsMouseOver || BrightnessPopup.IsOpen || ZoomPopup.IsOpen || _seekDragging)
        {
            _idleTimer.Start();
            return;
        }
        FsControlsHost.Visibility = Visibility.Collapsed;
        PreviewPopup.IsOpen = false;
        Overlay.Cursor = Cursors.None;
    }

    // ================= 캡쳐 / 구간 저장 / 이어붙이기 / 녹화 =================

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_mp == null || CurrentItem == null || !_mp.WillPlay) return;
        var name = Path.GetFileNameWithoutExtension(CurrentItem.Name);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 60) name = name[..60];
        var file = Path.Combine(_settings.CaptureDir, $"{name}_{TimeFormat.Format(_mp.Time).Replace(':', '-')}_{DateTime.Now:HHmmssfff}.png");
        if (!_mp.TakeSnapshot(0, file, 0, 0))
        {
            ShowOsd("캡쳐하지 못했습니다");
            return;
        }
        // 스냅샷은 잠시 뒤에 파일로 써진다. 다 써지면 클립보드에도 넣는다.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 30 && !File.Exists(file); i++) await Task.Delay(100);
            await Task.Delay(150);
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    Clipboard.SetImage(bmp);
                    ShowOsd($"캡쳐 저장: {Path.GetFileName(file)} (클립보드에도 복사)", 2.5);
                }
                catch (Exception ex)
                {
                    Log.Write("capture clipboard", ex);
                    ShowOsd(File.Exists(file) ? $"캡쳐 저장: {Path.GetFileName(file)}" : "캡쳐하지 못했습니다", 2.5);
                }
            });
        });
    }

    private void Clip_Click(object sender, RoutedEventArgs e)
    {
        if (_mp == null || CurrentItem == null || _length <= 0) return;
        if (!Ffmpeg.Available)
        {
            CardDialog.Show("구간 저장", "ffmpeg.exe 를 찾을 수 없습니다. 앱을 다시 설치해 주세요.", MessageKind.Error);
            return;
        }
        long start, end;
        if (_abA >= 0 && _abB > _abA) (start, end) = (_abA, _abB);
        else if (_abA >= 0) (start, end) = (_abA, Math.Max(_abA + 1000, _mp.Time));
        else (start, end) = (_mp.Time, Math.Min(_length, _mp.Time + 30_000));
        var wasPlaying = _mp.IsPlaying;
        if (wasPlaying) _mp.SetPause(true);
        var d = new ClipWindow(CurrentItem.Location, _length, start, end, () => _mp?.Time ?? 0);
        d.ShowDialog();
        if (d.SavedPath != null) ShowOsd($"저장했습니다: {Path.GetFileName(d.SavedPath)}", 3);
    }

    private bool CanEdit(string title)
    {
        if (_mp == null || CurrentItem == null || _length <= 0) return false;
        if (Ffmpeg.Available) return true;
        CardDialog.Show(title, "ffmpeg.exe 를 찾을 수 없습니다. 앱을 다시 설치해 주세요.", MessageKind.Error);
        return false;
    }

    private (long start, long end) DefaultRange(long fallbackLength)
    {
        var t = _mp?.Time ?? 0;
        if (_abA >= 0 && _abB > _abA) return (_abA, _abB);
        if (_abA >= 0) return (_abA, Math.Max(_abA + 1000, t));
        return (t, Math.Min(_length, t + fallbackLength));
    }

    private void Gif_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit("GIF 만들기")) return;
        var (start, end) = DefaultRange(5_000);
        _mp!.SetPause(true);
        var d = new ClipWindow(CurrentItem!.Location, _length, start, end, () => _mp?.Time ?? 0, ClipMode.Gif);
        d.ShowDialog();
        if (d.SavedPath != null) ShowOsd($"GIF 저장: {Path.GetFileName(d.SavedPath)}", 3);
    }

    private void DeleteRange_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit("구간 삭제")) return;
        var (start, end) = DefaultRange(10_000);
        _mp!.SetPause(true);
        var d = new ClipWindow(CurrentItem!.Location, _length, start, end, () => _mp?.Time ?? 0, ClipMode.Delete);
        d.ShowDialog();
        if (d.SavedPath != null) ShowOsd($"구간을 지운 영상 저장: {Path.GetFileName(d.SavedPath)}", 3);
    }

    private void Thumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit("썸네일 만들기")) return;
        _mp!.SetPause(true);
        var d = new ThumbnailWindow(CurrentItem!.Location, _mp.Time, _length);
        d.ShowDialog();
        if (d.SavedPath != null) ShowOsd($"썸네일 저장: {Path.GetFileName(d.SavedPath)}", 3);
    }

    private void EditMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = EditButton, Placement = PlacementMode.Top };
        void Add(string header, RoutedEventHandler h, string gesture)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += h;
            menu.Items.Add(mi);
        }
        Add("구간 저장…", Clip_Click, "S");
        Add("GIF 만들기…", Gif_Click, "G");
        Add("썸네일 만들기…", Thumbnail_Click, "T");
        Add("구간 삭제…", DeleteRange_Click, "");
        menu.Items.Add(new Separator());
        Add("A-B 구간을 구간 목록에 추가", AddSegment_Click, "Ins");
        Add("여러 구간 저장 · 삭제…", ShowSegments_Click, "");
        menu.Items.Add(new Separator());
        Add("동영상 이어붙이기…", Concat_Click, "");
        menu.IsOpen = true;
    }

    // ================= 여러 구간 =================

    private void ShowSegments_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.ShowSidePanel) SetSidePanel(true);
        SegmentTab.IsChecked = true;
    }

    private void AddSegment(long start, long end)
    {
        if (_length > 0) end = Math.Min(end, _length);
        var seg = new Segment(Math.Max(0, start), end);
        if (seg.End - seg.Start < 300) return;
        var i = 0;
        while (i < _segments.Count && _segments[i].Start < seg.Start) i++;
        _segments.Insert(i, seg);
        seg.PropertyChanged += (_, _) => RebuildMarkers();
        ShowOsd($"구간 추가 {seg.Range}");
        if (_settings.ShowSidePanel) SegmentTab.IsChecked = true;
    }

    private void AddSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_abA >= 0 && _abB > _abA)
        {
            AddSegment(_abA, _abB);
            _abA = _abB = -1;
            UpdateAB();
        }
        else
        {
            ShowOsd("먼저 A-B(R 키)로 구간을 고르거나, Shift 를 누른 채 시간 막대를 끌어 주세요", 3);
        }
    }

    private void DrawSegmentPreview(long a, long b)
    {
        RebuildMarkers();
        if (_length <= 0) return;
        var w = SegmentCanvas.ActualWidth - 14;
        var x1 = 7 + w * Math.Min(a, b) / _length;
        var x2 = 7 + w * Math.Max(a, b) / _length;
        var rect = new Rectangle
        {
            Width = Math.Max(2, x2 - x1), Height = 14, RadiusX = 3, RadiusY = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0xAA, 0x4F, 0xC3, 0xF7)),
        };
        Canvas.SetLeft(rect, x1);
        Canvas.SetTop(rect, 10);
        SegmentCanvas.Children.Add(rect);
    }

    private void ClearSegments_Click(object sender, RoutedEventArgs e) => _segments.Clear();

    private void SegmentRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Segment seg) _segments.Remove(seg);
    }

    private void SegmentCheck_Click(object sender, RoutedEventArgs e) => RebuildMarkers();

    private void SegmentBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SegmentBox.SelectedItem is Segment seg) SeekTo(seg.Start);
    }

    private List<Segment>? CheckedSegments(string title)
    {
        if (!CanEdit(title)) return null;
        var list = _segments.Where(s => s.Checked).OrderBy(s => s.Start).ToList();
        if (list.Count > 0) return list;
        CardDialog.Show(title, "체크한 구간이 없습니다. Shift 를 누른 채 시간 막대를 끌거나 A-B 를 고른 뒤 '구간 추가'를 눌러 구간을 만드세요.");
        return null;
    }

    private string SafeBaseName()
    {
        var name = Path.GetFileNameWithoutExtension(CurrentItem!.Name);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 60 ? name[..60] : name;
    }

    private void SaveSegmentsEach_Click(object sender, RoutedEventArgs e)
    {
        if (CheckedSegments("구간 저장") is not { } list) return;
        _mp!.SetPause(true);
        var source = CurrentItem!.Location;
        var dir = _settings.VideoDir;
        var name = SafeBaseName();
        var saved = 0;
        ProgressWindow.Run("구간 저장", $"체크한 구간 {list.Count}개를 각각 저장하는 중…", async (progress, ct) =>
        {
            for (var i = 0; i < list.Count; i++)
            {
                var seg = list[i];
                var output = Path.Combine(dir, $"{name}_{TimeFormat.Format(seg.Start).Replace(':', '-')}_{i + 1}.mp4");
                var index = i;
                await Ffmpeg.CutAsync(source, seg.Start, seg.End, output, true, true,
                    new Progress<double>(p => progress.Report((index + p) / list.Count)), ct);
                saved++;
            }
        });
        if (saved > 0) ShowOsd($"구간 {saved}개를 저장했습니다 ({dir})", 3);
    }

    private void SaveSegmentsJoined_Click(object sender, RoutedEventArgs e)
    {
        if (CheckedSegments("구간 합쳐 저장") is not { } list) return;
        _mp!.SetPause(true);
        var source = CurrentItem!.Location;
        var output = Path.Combine(_settings.VideoDir, $"{SafeBaseName()}_구간{list.Count}개_{DateTime.Now:HHmmss}.mp4");
        var keep = list.Select(s => (s.Start, s.End)).ToList();
        if (ProgressWindow.Run("구간 합쳐 저장", $"체크한 구간 {list.Count}개를 하나로 합쳐 저장하는 중…",
                (progress, ct) => Ffmpeg.KeepSegmentsAsync(source, keep, output, progress, ct)))
            ShowOsd($"저장했습니다: {Path.GetFileName(output)}", 3);
    }

    private void DeleteSegments_Click(object sender, RoutedEventArgs e)
    {
        if (CheckedSegments("구간 삭제") is not { } list) return;
        _mp!.SetPause(true);
        var source = CurrentItem!.Location;
        var keep = Ffmpeg.Complement(list.Select(s => (s.Start, s.End)), _length);
        if (keep.Count == 0)
        {
            CardDialog.Show("구간 삭제", "모든 부분을 지우게 되어 남는 영상이 없습니다.");
            return;
        }
        var output = Path.Combine(_settings.VideoDir, $"{SafeBaseName()}_구간삭제_{DateTime.Now:HHmmss}.mp4");
        if (ProgressWindow.Run("구간 삭제", $"체크한 구간 {list.Count}개를 지운 영상을 저장하는 중… (원본은 그대로)",
                (progress, ct) => Ffmpeg.KeepSegmentsAsync(source, keep, output, progress, ct)))
            ShowOsd($"저장했습니다: {Path.GetFileName(output)}", 3);
    }

    private void Concat_Click(object sender, RoutedEventArgs e)
    {
        if (!Ffmpeg.Available)
        {
            CardDialog.Show("이어붙이기", "ffmpeg.exe 를 찾을 수 없습니다. 앱을 다시 설치해 주세요.", MessageKind.Error);
            return;
        }
        var d = new ConcatWindow(_playlist.Where(p => !RemoteSources.IsUrl(p.Location)).Select(p => p.Location));
        d.ShowDialog();
        if (d.SavedPath != null &&
            CardDialog.Show("이어붙이기", $"저장했습니다.\n{d.SavedPath}", MessageKind.Info, "바로 재생", "폴더 열기", "닫기") is var c)
        {
            if (c == 0) AddAndPlay(new[] { d.SavedPath });
            else if (c == 1) RecorderWindow.OpenFolder(d.SavedPath);
        }
    }

    private RecorderWindow? _recorder;

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is { IsLoaded: true })
        {
            _recorder.Activate();
            return;
        }
        _recorder = new RecorderWindow();
        _recorder.Show();
    }

    // ================= 기타 메뉴 =================

    private void OpenCaptureFolder_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(_settings.CaptureDir);
    private void OpenVideoFolder_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(_settings.VideoDir);

    private void ChangeFolders_Click(object sender, RoutedEventArgs e)
    {
        var c = CardDialog.Show("저장 위치",
            $"캡쳐: {_settings.CaptureDir}\n동영상(구간 저장·녹화): {_settings.VideoDir}",
            MessageKind.Info, "캡쳐 위치 바꾸기", "동영상 위치 바꾸기", "닫기");
        if (c is not (0 or 1)) return;
        var d = new OpenFolderDialog { InitialDirectory = c == 0 ? _settings.CaptureDir : _settings.VideoDir };
        if (d.ShowDialog(this) != true) return;
        if (c == 0) _settings.CaptureFolder = d.FolderName;
        else _settings.VideoFolder = d.FolderName;
        _settings.Save();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void SidePanelMenu_Click(object sender, RoutedEventArgs e) => SetSidePanel(SidePanelMenu.IsChecked);

    private void SidePanelToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) ToggleFullscreen();
        SetSidePanel(!_settings.ShowSidePanel);
    }

    private void SetSidePanel(bool show)
    {
        _settings.ShowSidePanel = show;
        SidePanelMenu.IsChecked = show;
        if (!_fullscreen) UpdateSidePanelVisibility();
    }

    private void SideTab_Changed(object sender, RoutedEventArgs e)
    {
        if (PlaylistPanel == null || BookmarkPanel == null || SegmentPanel == null) return;
        PlaylistPanel.Visibility = PlaylistTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BookmarkPanel.Visibility = BookmarkTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SegmentPanel.Visibility = SegmentTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSidePanelVisibility()
    {
        var show = _settings.ShowSidePanel && !_fullscreen;
        SidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SideSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SideColumn.Width = show ? new GridLength(Math.Clamp(_settings.SidePanelWidth, 220, 600)) : new GridLength(0);
        SideColumn.MinWidth = show ? 220 : 0;
    }

    private void SideSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _settings.SidePanelWidth = SideColumn.ActualWidth;
    }

    private void Topmost_Click(object sender, RoutedEventArgs e) => Topmost = TopmostMenu.IsChecked;

    private void Hw_Click(object sender, RoutedEventArgs e)
    {
        _settings.HardwareDecoding = HwMenu.IsChecked;
        _settings.Save();
        CardDialog.Show("하드웨어 가속", "앱을 다시 시작하면 적용됩니다.");
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => CardDialog.Show("단축키",
        "Space  재생 / 일시정지\n" +
        "← →  5초 이동 (Ctrl: 30초, Shift: 1초)\n" +
        "↑ ↓ · 마우스 휠  볼륨\n" +
        "PgUp / PgDn  이전 / 다음\n" +
        "[ ]  느리게 / 빠르게 (최대 20배),  Backspace · \\  원래 속도\n" +
        "R  구간반복 (A → B → 해제, 반복 중에는 구간 안에서만 이동)\n" +
        "Insert  A-B 구간을 구간 목록에 추가,  Shift+시간 막대 끌기  구간 추가\n" +
        "G  GIF 만들기,  T  썸네일 만들기\n" +
        "L  반복 방식 (전체 → 한 개 → 안 함)\n" +
        "화면 클릭  재생 / 일시정지\n" +
        "F · Enter · 두 번 클릭  전체화면,  Esc  끝내기\n" +
        "C  화면 캡쳐,  S  구간 저장\n" +
        "B  현재 시점 즐겨찾기\n" +
        "M  음소거,  Home  처음부터\n" +
        "Ctrl+휠  확대,  끌기  확대한 화면 이동,  0  확대 원래대로\n" +
        "Ctrl+O  파일 열기,  Ctrl+U  주소 열기");

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow().ShowDialog();
    }

    // ================= 키보드 =================

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;
        // 목록에 초점이 있으면 위/아래 키는 목록 이동에 쓴다.
        if (e.Key is Key.Up or Key.Down && (PlaylistBox.IsKeyboardFocusWithin || BookmarkBox.IsKeyboardFocusWithin)) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var handled = true;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekBy(ctrl ? -30_000 : shift ? -1_000 : -5_000); break;
            case Key.Right: SeekBy(ctrl ? 30_000 : shift ? 1_000 : 5_000); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.PageUp: Prev_Click(this, e); break;
            case Key.PageDown: Next_Click(this, e); break;
            case Key.Home: SeekTo(0); break;
            case Key.F:
            case Key.Enter when !PlaylistBox.IsKeyboardFocusWithin:
                ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.M: Mute_Click(this, e); break;
            case Key.OemOpenBrackets: SpeedDown_Click(this, e); break;
            case Key.OemCloseBrackets: SpeedUp_Click(this, e); break;
            case Key.Back:
            case Key.OemBackslash:
            case Key.Oem5:
                SpeedReset_Click(this, e); break;
            case Key.G: Gif_Click(this, e); break;
            case Key.T when !ctrl: Thumbnail_Click(this, e); break;
            case Key.Insert: AddSegment_Click(this, e); break;
            case Key.R: AB_Click(this, e); break;
            case Key.L: Repeat_Click(this, e); break;
            case Key.C when !ctrl: Capture_Click(this, e); break;
            case Key.S when !ctrl: Clip_Click(this, e); break;
            case Key.B: AddBookmark_Click(this, e); break;
            case Key.D0:
            case Key.NumPad0: ZoomReset_Click(this, e); break;
            case Key.O when ctrl && shift: OpenFolder_Click(this, e); break;
            case Key.O when ctrl: OpenFiles_Click(this, e); break;
            case Key.U when ctrl: OpenUrl_Click(this, e); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    // ================= 안내 글 =================

    private void ShowOsd(string text, double seconds = 1.6)
    {
        OsdText.Text = text;
        Osd.Visibility = Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Interval = TimeSpan.FromSeconds(seconds);
        _osdTimer.Start();
    }
}
