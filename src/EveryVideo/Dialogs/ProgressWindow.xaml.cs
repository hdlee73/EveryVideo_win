using System.Windows;
using EveryVideo.Services;

namespace EveryVideo.Dialogs;

/// <summary>오래 걸리는 ffmpeg 작업을 진행 막대와 함께 돌린다. 성공하면 true.</summary>
public partial class ProgressWindow : CardWindow
{
    private readonly CancellationTokenSource _cts = new();
    private bool _running = true;
    private bool _ok;

    private ProgressWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Closing += (_, _) => { if (_running) _cts.Cancel(); };
    }

    public static bool Run(string title, string message, Func<IProgress<double>, CancellationToken, Task> work)
    {
        var w = new ProgressWindow(title, message);
        w.Loaded += async (_, _) =>
        {
            try
            {
                await work(new Progress<double>(p => w.Progress.Value = p), w._cts.Token);
                w._ok = true;
                w._running = false;
                w.Close();
            }
            catch (OperationCanceledException)
            {
                w._running = false;
                w.Close();
            }
            catch (Exception ex)
            {
                w._running = false;
                Log.Write(title, ex);
                w.ErrorText.Text = ex.Message;
                w.CancelButton.Content = "닫기";
                w.Progress.Value = 0;
            }
        };
        w.ShowDialog();
        return w._ok;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
