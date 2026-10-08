using System.IO;
using System.Windows;
using EveryVideo.Services;

namespace EveryVideo.Dialogs;

public partial class ThumbnailWindow : CardWindow
{
    private readonly string _source;
    private readonly long _at, _length;

    public string? SavedPath { get; private set; }

    public ThumbnailWindow(string source, long atMs, long lengthMs)
    {
        InitializeComponent();
        _source = source;
        _at = atMs;
        _length = lengthMs;
        SourceText.Text = $"{RemoteSources.DisplayName(source)} · {TimeFormat.Format(atMs)}";
        TitleBox.Text = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var (cols, rows) = SheetRadio.IsChecked == true
            ? GridCombo.SelectedIndex switch { 0 => (2, 2), 2 => (4, 4), 3 => (4, 3), 4 => (5, 4), _ => (3, 3) }
            : (1, 1);
        var width = WidthCombo.SelectedIndex switch { 0 => 1920, 2 => 640, _ => 1280 };
        var name = Path.GetFileNameWithoutExtension(RemoteSources.DisplayName(_source));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var output = Path.Combine(Settings.Current.CaptureDir,
            $"{name}_썸네일{(cols * rows > 1 ? "_모음" : "_" + TimeFormat.Format(_at).Replace(':', '-'))}_{DateTime.Now:HHmmss}.jpg");
        var title = TitleBox.Text;
        if (ProgressWindow.Run("썸네일 만들기", "썸네일을 만드는 중…",
                (_, ct) => Ffmpeg.ThumbnailAsync(_source, _at, _length, cols, rows, width, title, output, ct)))
        {
            SavedPath = output;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
