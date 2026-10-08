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
                var c = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
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

    private static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";

    /// <summary>
    /// 여러 동영상을 순서대로 하나의 MP4 로 합친다.
    /// fast=true 이면 다시 인코딩 없이 붙이고(같은 형식일 때만 가능), 실패하면 다시 인코딩한다.
    /// width/height 가 0 이면 첫 영상 크기를 쓴다.
    /// introImage 가 있으면 맨 앞에 introSeconds 초 동안 보여 주고, coverImage 가 있으면 MP4 표지(썸네일)로 넣는다.
    /// </summary>
    public static async Task ConcatAsync(IReadOnlyList<string> inputs, string output, int width, int height, bool fast,
        IProgress<double>? progress, CancellationToken ct, string? introImage = null, double introSeconds = 3,
        string? coverImage = null)
    {
        if (inputs.Count + (introImage != null ? 1 : 0) < 2) throw new ArgumentException("두 개 이상 골라 주세요.");
        var target = coverImage != null ? Path.Combine(Path.GetDirectoryName(output)!, $".everyvideo_{Guid.NewGuid():N}.mp4") : output;
        try
        {
            await ConcatCoreAsync(inputs, target, width, height, fast && introImage == null, progress, ct, introImage, introSeconds);
            if (coverImage != null)
            {
                await SetCoverAsync(target, coverImage, output, ct);
                TryDelete(target);
            }
        }
        catch
        {
            if (coverImage != null) TryDelete(target);
            throw;
        }
    }

    private static async Task ConcatCoreAsync(IReadOnlyList<string> inputs, string output, int width, int height, bool fast,
        IProgress<double>? progress, CancellationToken ct, string? introImage, double introSeconds)
    {
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

        // (경로, 이미지인가, 길이)
        var items = new List<(string path, bool image, MediaProbe probe)>();
        var introMs = (long)(introSeconds * 1000);
        if (introImage != null) items.Add((introImage, true, new MediaProbe(introMs, true, false, 0, 0)));
        for (var i = 0; i < inputs.Count; i++)
        {
            var image = IsImage(inputs[i]);
            items.Add((inputs[i], image, image ? probes[i] with { DurationMs = introMs, HasVideo = true, HasAudio = false } : probes[i]));
        }
        total = items.Sum(i => i.probe.DurationMs);

        var args = new List<string> { "-y" };
        foreach (var it in items)
        {
            if (it.image) args.AddRange(new[] { "-loop", "1", "-framerate", "30", "-t", TimeFormat.Seconds(it.probe.DurationMs) });
            args.AddRange(new[] { "-i", it.path });
        }
        var filter = new StringBuilder();
        var extra = items.Count;
        var pairs = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var p = items[i].probe;
            var sec = TimeFormat.Seconds(Math.Max(p.DurationMs, 100));
            if (p.HasVideo)
                filter.Append($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{i}];");
            else
                filter.Append($"color=c=black:s={width}x{height}:r=30:d={sec},format=yuv420p[v{i}];");
            if (p.HasAudio)
                filter.Append($"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
            else
            {
                // 소리가 없는 영상·그림은 같은 길이의 무음을 채운다.
                args.AddRange(new[] { "-f", "lavfi", "-t", sec, "-i", "anullsrc=r=48000:cl=stereo" });
                filter.Append($"[{extra}:a]aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
                extra++;
            }
            pairs.Append($"[v{i}][a{i}]");
        }
        filter.Append($"{pairs}concat=n={items.Count}:v=1:a=1[v][a]");
        args.AddRange(new[] { "-filter_complex", filter.ToString(), "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart", output });

        var (c2, log2) = await RunAsync(args, total, progress, ct);
        if (c2 == 0 && File.Exists(output)) return;
        Log.Write("concat encode", null, LastLines(log2, 20));
        TryDelete(output);
        throw new IOException("이어붙이기에 실패했습니다.\n" + LastLines(log2, 3));
    }

    /// <summary>MP4 에 표지 그림(썸네일)을 넣는다. 탐색기 미리보기 등에 보인다.</summary>
    public static async Task SetCoverAsync(string input, string image, string output, CancellationToken ct)
    {
        var (code, log) = await RunAsync(new[]
        {
            "-y", "-i", input, "-i", image, "-map", "0", "-map", "1", "-c", "copy", "-c:v:1", "mjpeg",
            "-disposition:v:1", "attached_pic", "-movflags", "+faststart", output,
        }, 0, null, ct);
        if (code == 0 && File.Exists(output)) return;
        Log.Write("cover", null, LastLines(log, 10));
        throw new IOException("표지 그림을 넣지 못했습니다.\n" + LastLines(log, 3));
    }

    /// <summary>
    /// 여러 구간을 이어서 하나의 MP4 로 저장한다 (다시 인코딩). 구간 삭제는 남길 구간들을 넘겨 쓴다.
    /// </summary>
    public static async Task KeepSegmentsAsync(string input, IReadOnlyList<(long start, long end)> keep, string output,
        IProgress<double>? progress, CancellationToken ct)
    {
        keep = keep.Where(k => k.end - k.start >= 100).OrderBy(k => k.start).ToList();
        if (keep.Count == 0) throw new ArgumentException("남길 구간이 없습니다.");
        if (keep.Count == 1)
        {
            await CutAsync(input, keep[0].start, keep[0].end, output, true, true, progress, ct);
            return;
        }
        var probe = await ProbeAsync(input, ct);
        var filter = new StringBuilder();
        var pairs = new StringBuilder();
        for (var i = 0; i < keep.Count; i++)
        {
            var (s, e) = keep[i];
            var range = $"start={TimeFormat.Seconds(s)}:end={TimeFormat.Seconds(e)}";
            filter.Append($"[0:v:0]trim={range},setpts=PTS-STARTPTS[v{i}];");
            if (probe.HasAudio) filter.Append($"[0:a:0]atrim={range},asetpts=PTS-STARTPTS[a{i}];");
            pairs.Append(probe.HasAudio ? $"[v{i}][a{i}]" : $"[v{i}]");
        }
        filter.Append($"{pairs}concat=n={keep.Count}:v=1:a={(probe.HasAudio ? 1 : 0)}[v]{(probe.HasAudio ? "[a]" : "")}");
        var args = new List<string> { "-y", "-i", input, "-filter_complex", filter.ToString(), "-map", "[v]" };
        if (probe.HasAudio) args.AddRange(new[] { "-map", "[a]", "-c:a", "aac", "-b:a", "192k" });
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", output });
        var total = keep.Sum(k => k.end - k.start);
        var (code, log) = await RunAsync(args, total, progress, ct);
        if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 1024) return;
        Log.Write("segments", null, LastLines(log, 20));
        TryDelete(output);
        throw new IOException("저장에 실패했습니다.\n" + LastLines(log, 3));
    }

    /// <summary>[0,length] 에서 지울 구간들을 빼고 남는 구간들</summary>
    public static List<(long start, long end)> Complement(IEnumerable<(long start, long end)> remove, long length)
    {
        var result = new List<(long, long)>();
        long pos = 0;
        foreach (var (s, e) in remove.OrderBy(r => r.start))
        {
            if (s > pos) result.Add((pos, s));
            pos = Math.Max(pos, e);
        }
        if (pos < length) result.Add((pos, length));
        return result;
    }

    /// <summary>구간을 GIF 로 저장한다 (팔레트를 만들어 색이 깨끗하게).</summary>
    public static async Task GifAsync(string input, long startMs, long endMs, string output, int width, int fps,
        IProgress<double>? progress, CancellationToken ct)
    {
        var dur = endMs - startMs;
        if (dur <= 0) throw new ArgumentException("끝 시간이 시작 시간보다 뒤여야 합니다.");
        var (code, log) = await RunAsync(new[]
        {
            "-y", "-ss", TimeFormat.Seconds(startMs), "-t", TimeFormat.Seconds(dur), "-i", input,
            "-vf", $"fps={fps},scale={width}:-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
            "-loop", "0", output,
        }, dur, progress, ct);
        if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 0) return;
        Log.Write("gif", null, LastLines(log, 10));
        TryDelete(output);
        throw new IOException("GIF 를 만들지 못했습니다.\n" + LastLines(log, 3));
    }

    /// <summary>
    /// 썸네일 그림(JPG)을 만든다. cols*rows 가 1 이면 지정 시점 한 장, 그 이상이면 영상 전체에서 고른 장면 모음.
    /// title 이 있으면 그림 아래쪽에 큰 글씨로 넣는다.
    /// </summary>
    public static async Task ThumbnailAsync(string input, long atMs, long lengthMs, int cols, int rows, int width,
        string? title, string output, CancellationToken ct)
    {
        string? textFile = null;
        try
        {
            var vf = new StringBuilder();
            var args = new List<string> { "-y" };
            var count = cols * rows;
            if (count <= 1)
            {
                args.AddRange(new[] { "-ss", TimeFormat.Seconds(atMs), "-i", input, "-frames:v", "1" });
                vf.Append($"scale={width}:-2");
            }
            else
            {
                // 영상 전체를 고르게 나눈 시점마다 한 장씩 빠르게 뽑아 바둑판으로 붙인다.
                var step = Math.Max(500, lengthMs / (count + 1));
                for (var i = 0; i < count; i++)
                    args.AddRange(new[] { "-ss", TimeFormat.Seconds(step * (i + 1)), "-i", input });
                var w = width / cols;
                w -= w % 2;
                for (var i = 0; i < count; i++)
                    vf.Append($"[{i}:v:0]trim=end_frame=1,setpts=PTS-STARTPTS,scale={w}:{w * 9 / 16 / 2 * 2}:force_original_aspect_ratio=decrease,pad={w}:{w * 9 / 16 / 2 * 2}:(ow-iw)/2:(oh-ih)/2,setsar=1[f{i}];");
                for (var i = 0; i < count; i++) vf.Append($"[f{i}]");
                vf.Append($"concat=n={count}:v=1:a=0,tile={cols}x{rows}:padding=4:margin=4:color=black");
                args.AddRange(new[] { "-frames:v", "1" });
            }
            if (!string.IsNullOrWhiteSpace(title))
            {
                textFile = Path.Combine(Path.GetTempPath(), $"everyvideo_title_{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(textFile, title.Trim(), new UTF8Encoding(false), ct);
                var font = new[] { "malgunbd.ttf", "malgun.ttf", "arialbd.ttf" }
                    .Select(f => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), f))
                    .FirstOrDefault(File.Exists);
                vf.Append(",drawtext=");
                if (font != null) vf.Append($"fontfile='{FilterPath(font)}':");
                vf.Append($"textfile='{FilterPath(textFile)}':fontcolor=white:fontsize=h/9:borderw=6:bordercolor=black@0.8:" +
                          "x=(w-text_w)/2:y=h-text_h-h/12");
            }
            args.AddRange(new[] { count <= 1 ? "-vf" : "-filter_complex", vf.ToString(), "-q:v", "2", output });
            var (code, log) = await RunAsync(args, 0, null, ct);
            if (code == 0 && File.Exists(output)) return;
            Log.Write("thumbnail", null, LastLines(log, 10));
            throw new IOException("썸네일을 만들지 못했습니다.\n" + LastLines(log, 3));
        }
        finally
        {
            if (textFile != null) TryDelete(textFile);
        }
    }

    /// <summary>필터 안에 쓰는 경로: 역슬래시는 / 로, : 는 \: 로</summary>
    private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

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
