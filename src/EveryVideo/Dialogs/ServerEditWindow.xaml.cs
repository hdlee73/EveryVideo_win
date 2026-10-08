using System.Windows;
using EveryVideo.Services;

namespace EveryVideo.Dialogs;

public partial class ServerEditWindow : CardWindow
{
    public ServerEntry? Result { get; private set; }

    public ServerEditWindow(ServerEntry? existing)
    {
        InitializeComponent();
        if (existing != null)
        {
            Title = "서버 고치기";
            (existing.Kind == "smb" ? SmbRadio : FtpRadio).IsChecked = true;
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port > 0 ? existing.Port.ToString() : "";
            PathBox.Text = existing.Path;
            UserBox.Text = existing.User;
            PassBox.Password = existing.Password;
        }
        FtpRadio.Checked += (_, _) => UpdateKind();
        SmbRadio.Checked += (_, _) => UpdateKind();
        UpdateKind();
        Loaded += (_, _) => HostBox.Focus();
    }

    private void UpdateKind()
    {
        var smb = SmbRadio.IsChecked == true;
        PathLabel.Text = smb ? "공유 이름/폴더 (예: Videos 또는 Videos/영화)" : "시작 폴더 (선택, 예: /movies)";
        PortBox.IsEnabled = !smb;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim().Replace("ftp://", "").Replace("smb://", "").Trim('/', '\\');
        if (host.Length == 0)
        {
            ErrorText.Text = "주소를 입력해 주세요.";
            return;
        }
        var smb = SmbRadio.IsChecked == true;
        if (smb && PathBox.Text.Trim('/', '\\', ' ').Length == 0)
        {
            ErrorText.Text = "SMB 는 공유 이름을 입력해야 합니다. (예: Videos)";
            return;
        }
        int.TryParse(PortBox.Text.Trim(), out var port);
        Result = new ServerEntry
        {
            Kind = smb ? "smb" : "ftp",
            Name = NameBox.Text.Trim(),
            Host = host,
            Port = smb ? 0 : port,
            Path = PathBox.Text.Trim(),
            User = UserBox.Text.Trim(),
            Password = PassBox.Password,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
