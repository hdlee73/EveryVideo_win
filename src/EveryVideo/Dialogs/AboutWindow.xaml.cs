using System.Windows;
using EveryVideo.Services;

namespace EveryVideo.Dialogs;

public partial class AboutWindow : CardWindow
{
    private UpdateInfo? _latest;

    public AboutWindow()
    {
        InitializeComponent();
        NameText.Text = AppInfo.Name;
        AppNameText.Text = $"{AppInfo.Name} for Windows";
        VersionText.Text = $"v{AppInfo.Version}" + (string.IsNullOrEmpty(AppInfo.BuildDate) ? "" : $"  (업데이트 {AppInfo.BuildDate})");
        AuthorRun.Text = AppInfo.Author + " ";
        MailRun.Text = $"({AppInfo.Email})";
        AutoCheck.IsChecked = Settings.Current.CheckUpdates;
        AutoCheck.Checked += (_, _) => SaveAuto();
        AutoCheck.Unchecked += (_, _) => SaveAuto();
        Loaded += (_, _) => Check_Click(this, new RoutedEventArgs());
    }

    private void SaveAuto()
    {
        Settings.Current.CheckUpdates = AutoCheck.IsChecked == true;
        Settings.Current.Save();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateText.Text = "새 버전을 확인하는 중…";
        _latest = await UpdateChecker.GetLatestAsync();
        CheckButton.IsEnabled = true;
        if (_latest == null)
        {
            UpdateText.Text = "새 버전을 확인하지 못했습니다. 인터넷 연결을 확인해 주세요.";
            DownloadButton.Visibility = Visibility.Collapsed;
        }
        else if (UpdateChecker.IsNewer(_latest.Version, AppInfo.Version))
        {
            var date = _latest.Published is { } d ? $" ({d:yyyy-MM-dd})" : "";
            UpdateText.Text = $"새 버전 v{_latest.Version}{date} 이(가) 있습니다.";
            DownloadButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateText.Text = $"최신 버전을 쓰고 있습니다. (최신 v{_latest.Version})";
            DownloadButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Download_Click(object sender, RoutedEventArgs e) =>
        AppInfo.OpenUrl(_latest?.DownloadUrl ?? _latest?.PageUrl ?? AppInfo.LatestReleaseUrl);

    private void Releases_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl);

    private void Mail_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl("mailto:" + AppInfo.Email);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
