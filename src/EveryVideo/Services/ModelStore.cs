using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace EveryVideo.Services;

public enum AiModel { Colorize, Upscale }

/// <summary>
/// AI 모델 파일. 설치 파일을 키우지 않으려고 처음 쓸 때 한 번만 내려받아 %AppData%\EveryVideo\models 에 둔다.
/// 안드로이드 앱과 같은 파일(EveryVideo 저장소 models/)을 쓰고 SHA-256 으로 확인한다.
/// </summary>
public static class ModelStore
{
    private static readonly string[] Bases =
    {
        "https://raw.githubusercontent.com/hdlee73/everyvideo/main/models/",
        "https://raw.githubusercontent.com/hdlee73/everyvideo/claude/project-thread-qe24r9/models/",
    };

    public static string Dir => Path.Combine(Settings.DataDir, "models");

    public static (string file, string sha256, int sizeMb, string title) Info(AiModel m) => m switch
    {
        AiModel.Colorize => ("colorize.onnx", "7322dac83f7c8e1a4a38d684b22bdf89cf23c76cbf79b71eaa773533ec96c545", 33, "AI 컬러화"),
        _ => ("upscale_x4.onnx", "8272c563563a7dd005b47a94389ff2ab0dd89ffc96a50b0063f45f11ef040b7c", 5, "AI 업스케일"),
    };

    public static string PathOf(AiModel m) => Path.Combine(Dir, Info(m).file);

    public static bool IsReady(AiModel m) => File.Exists(PathOf(m)) && new FileInfo(PathOf(m)).Length > 0;

    public static async Task DownloadAsync(AiModel m, IProgress<double>? progress, CancellationToken ct)
    {
        if (IsReady(m)) return;
        var (file, sha, sizeMb, title) = Info(m);
        Directory.CreateDirectory(Dir);
        var target = PathOf(m);
        var tmp = target + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveryVideo/" + AppInfo.Version);
        Exception? last = null;
        foreach (var b in Bases)
        {
            try
            {
                using var resp = await http.GetAsync(b + file, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? sizeMb * 1_000_000L;
                using (var input = await resp.Content.ReadAsStreamAsync(ct))
                using (var output = File.Create(tmp))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    while ((n = await input.ReadAsync(buf, ct)) > 0)
                    {
                        await output.WriteAsync(buf.AsMemory(0, n), ct);
                        hash.AppendData(buf, 0, n);
                        done += n;
                        progress?.Report(Math.Clamp((double)done / total, 0, 1));
                    }
                    var hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    if (hex != sha) throw new IOException("파일 확인(체크섬)에 실패했습니다");
                }
                File.Move(tmp, target, true);
                return;
            }
            catch (OperationCanceledException)
            {
                Ffmpeg.TryDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                Ffmpeg.TryDelete(tmp);
                last = ex;
                Log.Write("model download", ex, b + file);
            }
        }
        throw new IOException($"{title} 모델을 내려받지 못했습니다: {last?.Message}", last);
    }
}
