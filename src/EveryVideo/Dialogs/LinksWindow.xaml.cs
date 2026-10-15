using System.Windows;
using System.Windows.Input;
using EveryVideo.Services;

namespace EveryVideo.Dialogs;

public enum LinkAction { None, Play, Download }

/// <summary>웹 페이지에서 찾은 동영상 링크를 골라 재생하거나 내려받는다.</summary>
public partial class LinksWindow : CardWindow
{
    public LinkAction Action { get; private set; }
    public List<WebLink> Selected { get; } = new();

    public LinksWindow(IReadOnlyList<WebLink> links, string pageUrl, bool forDownload = false)
    {
        InitializeComponent();
        List.ItemsSource = links;
        InfoText.Text = $"{pageUrl}\n이 페이지에서 동영상 {links.Count}개를 찾았습니다. 여러 개를 고르면 차례로 재생하거나 모두 내려받습니다. " +
                        "m3u8 같은 스트리밍 주소는 내려받을 수 없어요.";
        if (forDownload)
        {
            DownloadButton.Style = (Style)FindResource("PrimaryButton");
            PlayButton.Style = (Style)FindResource("TextButton");
        }
        Loaded += (_, _) => { if (links.Count > 0) List.SelectedIndex = 0; List.Focus(); };
    }

    private bool Collect()
    {
        Selected.Clear();
        Selected.AddRange(List.SelectedItems.Cast<WebLink>());
        return Selected.Count > 0;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!Collect()) return;
        Action = LinkAction.Play;
        DialogResult = true;
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (!Collect()) return;
        Action = LinkAction.Download;
        DialogResult = true;
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => Play_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
