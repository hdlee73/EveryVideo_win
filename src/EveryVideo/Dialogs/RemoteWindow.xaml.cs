using System.IO;
using System.Windows;
using System.Windows.Input;
using EveryVideo.Services;

namespace EveryVideo.Dialogs;

/// <summary>등록한 FTP/SMB 서버의 폴더를 둘러보고 동영상을 고른다.</summary>
public partial class RemoteWindow : CardWindow
{
    private ServerEntry? _server;
    private string _path = "";
    private CancellationTokenSource? _cts;

    /// <summary>재생할 위치들 (첫 번째부터 재생)</summary>
    public List<string> Selected { get; } = new();

    public RemoteWindow()
    {
        InitializeComponent();
        ReloadServers();
        Loaded += (_, _) => { if (ServerList.Items.Count > 0) ServerList.SelectedIndex = 0; };
    }

    private void ReloadServers()
    {
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = Settings.Current.Servers;
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var d = new ServerEditWindow(null) { Owner = this };
        if (d.ShowDialog() != true || d.Result == null) return;
        Settings.Current.Servers.Add(d.Result);
        Settings.Current.Save();
        ReloadServers();
        ServerList.SelectedItem = d.Result;
    }

    private void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerEntry s) return;
        var d = new ServerEditWindow(s) { Owner = this };
        if (d.ShowDialog() != true || d.Result == null) return;
        var i = Settings.Current.Servers.IndexOf(s);
        Settings.Current.Servers[i] = d.Result;
        Settings.Current.Save();
        ReloadServers();
        ServerList.SelectedIndex = i;
    }

    private void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerEntry s) return;
        if (!CardDialog.Confirm("서버 지우기", $"'{s.Display}' 을(를) 목록에서 지울까요?", "지우기")) return;
        Settings.Current.Servers.Remove(s);
        Settings.Current.Save();
        ReloadServers();
    }

    private async void ServerList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _server = ServerList.SelectedItem as ServerEntry;
        if (_server == null) return;
        if (_server.Kind == "smb")
        {
            StatusText.Text = "연결하는 중…";
            try { await RemoteSources.ConnectSmbAsync(_server); }
            catch (Exception ex) { Log.Write("smb connect", ex); }
            await Open(RemoteSources.SmbRoot(_server));
        }
        else
        {
            await Open(string.IsNullOrWhiteSpace(_server.Path) ? "/" : "/" + _server.Path.Trim('/'));
        }
    }

    private async Task Open(string path)
    {
        if (_server == null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        StatusText.Text = "불러오는 중…";
        PathText.Text = path;
        try
        {
            var items = _server.Kind == "smb"
                ? await RemoteSources.ListSmbAsync(path, cts.Token)
                : await RemoteSources.ListFtpAsync(_server, path, cts.Token);
            if (cts.IsCancellationRequested) return;
            _path = path;
            ItemList.ItemsSource = items;
            StatusText.Text = items.Count == 0 ? "동영상이 없습니다" : $"{items.Count(i => !i.IsFolder)}개 동영상";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("remote list", ex, path);
            StatusText.Text = "";
            ItemList.ItemsSource = null;
            CardDialog.Show("연결하지 못했습니다", $"{path}\n\n{ex.Message}", MessageKind.Error);
        }
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null || string.IsNullOrEmpty(_path)) return;
        if (_server.Kind == "smb")
        {
            var parent = Path.GetDirectoryName(_path.TrimEnd('\\'));
            // \\서버 까지는 올라가지 않는다 (공유 목록은 볼 수 없음).
            if (!string.IsNullOrEmpty(parent) && parent.TrimStart('\\').Contains('\\')) await Open(parent);
        }
        else if (_path != "/")
        {
            var parent = _path.TrimEnd('/');
            parent = parent[..(parent.LastIndexOf('/') + 1)];
            await Open(parent.Length == 0 ? "/" : parent);
        }
    }

    private string Location(RemoteItem item) =>
        _server!.Kind == "smb" ? item.Location : RemoteSources.FtpUrl(_server, item.Location);

    private async void ItemList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is not RemoteItem item) return;
        if (item.IsFolder) await Open(item.Location);
        else Play_Click(sender, e);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var files = ItemList.SelectedItems.Cast<RemoteItem>().Where(i => !i.IsFolder).ToList();
        if (files.Count == 0) return;
        Selected.AddRange(files.Select(Location));
        DialogResult = true;
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.ItemsSource is not IEnumerable<RemoteItem> items) return;
        var files = items.Where(i => !i.IsFolder).ToList();
        if (files.Count == 0) return;
        var first = ItemList.SelectedItem as RemoteItem;
        if (first != null && !first.IsFolder) files = files.SkipWhile(f => f != first).Concat(files.TakeWhile(f => f != first)).ToList();
        Selected.AddRange(files.Select(Location));
        DialogResult = true;
    }
}
