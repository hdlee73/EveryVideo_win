using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EveryVideo.Services;

public sealed record MediaProbe(long DurationMs, bool HasVideo, bool HasAudio, int Width, int Height);

/// <summary>
/// 함께 배포되는 ffmpeg.exe 로 구간 저장, 이어붙이기, 미리보기 그림, 화면 녹화를 한다.
/// </summary>
public static partial class Ffmpeg
{
    private static string? _path;

    public static string? ExePath => _path ??= Find();

    public static bool Available => ExePath != null;

    private static string? Find()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var c in new[] { Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"), Path.Combine(baseDir, "ffmpeg.exe") })
            if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var c = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(c)) return c;
            }
            catch
            {
                // PATH 에 이상한 항목이 있어도 넘어간다.
            }
        }
        return null;
    }

    public static ProcessStartInfo StartInfo(IEnumerable<string> args, bool redirectStdout = false, bool redirectStdin = false)
    {
        var psi = new ProcessStartInfo(ExePath ?? throw new InvalidOperationException("ffmpeg.exe 를 찾을 수 없습니다. 앱을 다시 설치해 주세요."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardInput = redirectStdin,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-hide_banner");
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>ffmpeg 를 실행하고 끝날 때까지 기다린다. progress 는 0~1.</summary>
    public static async Task<(int code, string log)> RunAsync(IEnumerable<string> args, long totalMs,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var p = new Process { StartInfo = StartInfo(args) };
        var log = new StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (log)
            {
                log.AppendLine(e.Data);
                if (log.Length > 200_000) log.Remove(0, 100_000);
            }
            if (progress != null && totalMs > 0)
            {
                var m = TimeRegex().Match(e.Data);
                if (m.Success && TimeSpan.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var t))
                    progress.Report(Math.Clamp(t.TotalMilliseconds / totalMs, 0, 1));
            }
        };
        p.Start();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* 이미 끝남 */ }
            throw;
        }
        lock (log) return (p.ExitCode, log.ToString());
    }

    [GeneratedRegex(@"time=\s*(\d+:\d+:\d+(?:\.\d+)?)")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"Duration:\s*(\d+:\d+:\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"Stream #\d+:\d+.*?: Video: .*?(\d{2,5})x(\d{2,5})")]
    private static partial Regex VideoRegex();

    /// <summary>길이, 영상/소리 유무, 크기를 알아낸다.</summary>
    public static async Task<MediaProbe> ProbeAsync(string input, CancellationToken ct = default)
    {
        var (_, log) = await RunAsync(new[] { "-i", input }, 0, null, ct);
        long dur = 0;
        var d = DurationRegex().Match(log);
        if (d.Success && TimeSpan.TryParse(d.Groups[1].Value, CultureInfo.InvariantCulture, out var ts))
            dur = (long)ts.TotalMilliseconds;
        var v = VideoRegex().Match(log);
        int w = 0, h = 0;
        if (v.Success)
        {
            w = int.Parse(v.Groups[1].Value);
            h = int.Parse(v.Groups[2].Value);
        }
        return new MediaProbe(dur, log.Contains(": Video:"), log.Contains(": Audio:"), w, h);
    }

    private static string LastLines(string log, int n = 6) =>
        string.Join("\n", log.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(n));

    /// <summary>
    /// 구간을 잘라 저장한다. 빠르게(원본 그대로 옮겨 담기) → 다시 인코딩 → 소리 빼고 순으로 시도한다.
    /// </summary>
    public static async Task CutAsync(string input, long startMs, long endMs, string output, bool accurate,
        bool withAudio, IProgress<double>? progress, CancellationToken ct)
    {
        var dur = endMs - startMs;
        if (dur <= 0) throw new ArgumentException("끝 시간이 시작 시간보다 뒤여야 합니다.");

        IEnumerable<string> Args(string mode)
        {
            var a = new List<string> { "-y", "-ss", TimeFormat.Seconds(startMs), "-i", input, "-t", TimeFormat.Seconds(dur),
                "-map", "0:v:0?" };
            if (withAudio && mode != "noaudio") a.AddRange(new[] { "-map", "0:a:0?" });
            if (mode == "copy")
                a.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });
            else
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "192k" });
            a.AddRange(new[] { "-sn", "-dn", "-movflags", "+faststart", output });
            return a;
        }

        var modes = accurate ? new[] { "encode", "noaudio" } : new[] { "copy", "encode", "noaudio" };
        string log = "";
        foreach (var mode in modes)
        {
            if (mode == "noaudio" && !withAudio) continue;
            int code;
            (code, log) = await RunAsync(Args(mode), dur, progress, ct);
            if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 1024) return;
            Log.Write("cut " + mode, null, LastLines(log));
            progress?.Report(0);
        }
        TryDelete(output);
        throw new IOException("구간 저장에 실패했습니다.\n" + LastLines(log, 3));
    }

    /// <summary>
    /// 여러 동영상을 순서대로 하나의 MP4 로 합친다.
    /// fast=true 이면 다시 인코딩 없이 붙이고(같은 형식일 때만 가능), 실패하면 다시 인코딩한다.
    /// width/height 가 0 이면 첫 영상 크기를 쓴다.
    /// </summary>
    public static async Task ConcatAsync(IReadOnlyList<string> inputs, string output, int width, int height, bool fast,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (inputs.Count < 2) throw new ArgumentException("두 개 이상 골라 주세요.");
        var probes = new List<MediaProbe>();
        foreach (var i in inputs) probes.Add(await ProbeAsync(i, ct));
        var total = probes.Sum(p => p.DurationMs);

        if (fast)
        {
            var list = Path.Combine(Path.GetTempPath(), $"everyvideo_concat_{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(list, inputs.Select(i => $"file '{i.Replace("'", "'\\''")}'"), ct);
            try
            {
                var (code, log) = await RunAsync(new[] { "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy",
                    "-movflags", "+faststart", output }, total, progress, ct);
                if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 1024) return;
                Log.Write("concat copy", null, LastLines(log));
            }
            finally
            {
                TryDelete(list);
            }
            progress?.Report(0);
        }

        if (width <= 0 || height <= 0)
        {
            var first = probes.FirstOrDefault(p => p.Width > 0);
            width = first?.Width ?? 1280;
            height = first?.Height ?? 720;
        }
        width -= width % 2;
        height -= height % 2;

        var args = new List<string> { "-y" };
        foreach (var i in inputs) args.AddRange(new[] { "-i", i });
        var filter = new StringBuilder();
        var extra = inputs.Count;
        var pairs = new StringBuilder();
        for (var i = 0; i < inputs.Count; i++)
        {
            var p = probes[i];
            var sec = TimeFormat.Seconds(Math.Max(p.DurationMs, 100));
            if (p.HasVideo)
                filter.Append($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{i}];");
            else
                filter.Append($"color=c=black:s={width}x{height}:r=30:d={sec},format=yuv420p[v{i}];");
            if (p.HasAudio)
                filter.Append($"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
            else
            {
                // 소리가 없는 영상은 같은 길이의 무음을 채운다.
                args.AddRange(new[] { "-f", "lavfi", "-t", sec, "-i", "anullsrc=r=48000:cl=stereo" });
                filter.Append($"[{extra}:a]aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
                extra++;
            }
            pairs.Append($"[v{i}][a{i}]");
        }
        filter.Append($"{pairs}concat=n={inputs.Count}:v=1:a=1[v][a]");
        args.AddRange(new[] { "-filter_complex", filter.ToString(), "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart", output });

        var (c2, log2) = await RunAsync(args, total, progress, ct);
        if (c2 == 0 && File.Exists(output)) return;
        Log.Write("concat encode", null, LastLines(log2, 20));
        TryDelete(output);
        throw new IOException("이어붙이기에 실패했습니다.\n" + LastLines(log2, 3));
    }

    /// <summary>지정 시점의 작은 그림(JPEG)을 만든다. 탐색 미리보기용.</summary>
    public static async Task<byte[]?> FrameAsync(string input, long ms, int width, CancellationToken ct)
    {
        if (!Available) return null;
        using var p = new Process
        {
            StartInfo = StartInfo(new[]
            {
                "-loglevel", "error", "-ss", TimeFormat.Seconds(ms), "-i", input, "-frames:v", "1", "-an", "-sn",
                "-vf", $"scale={width}:-2", "-q:v", "6", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1",
            }, redirectStdout: true),
        };
        p.Start();
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        using var ms2 = new MemoryStream();
        try
        {
            await p.StandardOutput.BaseStream.CopyToAsync(ms2, ct);
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* 이미 끝남 */ }
            return null;
        }
        return ms2.Length > 0 ? ms2.ToArray() : null;
    }

    /// <summary>dshow 오디오 장치 이름 목록 (화면 녹화에서 소리 녹음용)</summary>
    public static async Task<List<string>> AudioDevicesAsync()
    {
        if (!Available) return new();
        var (_, log) = await RunAsync(new[] { "-list_devices", "true", "-f", "dshow", "-i", "dummy" }, 0, null, CancellationToken.None);
        var result = new List<string>();
        var inAudio = false;
        foreach (var line in log.Split('\n'))
        {
            // 새 ffmpeg: "[dshow @ ...] "이름" (audio)", 옛 ffmpeg: "DirectShow audio devices" 다음 줄들
            if (line.Contains("DirectShow audio devices")) { inAudio = true; continue; }
            if (line.Contains("DirectShow video devices")) { inAudio = false; continue; }
            if (line.Contains("Alternative name")) continue;
            var m = Regex.Match(line, "\"([^\"]+)\"");
            if (!m.Success) continue;
            if (line.Contains("(audio)") || (inAudio && !line.Contains("(video)")))
                result.Add(m.Groups[1].Value);
        }
        return result.Distinct().ToList();
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 무시 */ }
    }
}
