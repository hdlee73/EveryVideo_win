using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EveryVideo.Services;

/// <summary>
/// ffmpeg gdigrab 으로 화면을 녹화한다. Windows 표준 화면 캡쳐이며,
/// 다른 앱의 보호된(DRM) 화면은 Windows 정책에 따라 검게 녹화될 수 있다.
/// </summary>
public sealed class ScreenRecorder
{
    private Process? _proc;
    private readonly StringBuilder _log = new();

    public bool IsRecording => _proc is { HasExited: false };
    public string? OutputPath { get; private set; }
    public DateTime StartedAt { get; private set; }

    public event Action? Stopped;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <param name="primaryOnly">주 모니터만 녹화</param>
    /// <param name="audioDevice">녹음할 dshow 오디오 장치 이름 (null 이면 소리 없음)</param>
    public void Start(string output, bool primaryOnly, string? audioDevice, int fps = 30)
    {
        if (IsRecording) return;
        var args = new List<string> { "-y" };
        args.AddRange(new[] { "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", "1" });
        if (primaryOnly)
        {
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            args.AddRange(new[] { "-offset_x", "0", "-offset_y", "0", "-video_size", $"{w - w % 2}x{h - h % 2}" });
        }
        args.AddRange(new[] { "-i", "desktop" });
        if (!string.IsNullOrEmpty(audioDevice))
            args.AddRange(new[] { "-f", "dshow", "-i", "audio=" + audioDevice });
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-pix_fmt", "yuv420p",
            "-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2" });
        if (!string.IsNullOrEmpty(audioDevice)) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
        args.AddRange(new[] { "-movflags", "+faststart", output });

        _log.Clear();
        _proc = new Process { StartInfo = Ffmpeg.StartInfo(args, redirectStdin: true), EnableRaisingEvents = true };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_log) _log.AppendLine(e.Data); };
        _proc.Exited += (_, _) => Stopped?.Invoke();
        _proc.Start();
        _proc.BeginErrorReadLine();
        OutputPath = output;
        StartedAt = DateTime.Now;
    }

    /// <summary>ffmpeg 에 q 를 보내 파일을 제대로 마무리하고 끝낸다.</summary>
    public async Task<bool> StopAsync()
    {
        var p = _proc;
        if (p == null) return false;
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteAsync('q');
                await p.StandardInput.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { p.Kill(true); }
            }
        }
        catch (Exception e)
        {
            Log.Write("recorder stop", e);
        }
        _proc = null;
        var ok = OutputPath != null && File.Exists(OutputPath) && new FileInfo(OutputPath).Length > 1024;
        if (!ok) lock (_log) Log.Write("recorder", null, _log.ToString());
        return ok;
    }

    public string LastLog { get { lock (_log) return _log.ToString(); } }
}
