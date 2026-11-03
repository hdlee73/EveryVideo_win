using System.Diagnostics;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EveryVideo.Services;

/// <summary>sRGB ↔ CIE Lab (D65). 컬러화 모델은 밝기(L)만 보고 색(a, b)을 추정한다.</summary>
internal static class Lab
{
    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i =>
    {
        var c = i / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    private const int Lut = 4096;
    private static readonly byte[] ToSrgb = Enumerable.Range(0, Lut + 1).Select(i =>
    {
        var c = i / (float)Lut;
        var s = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)(s * 255f + 0.5f), 0, 255);
    }).ToArray();

    private static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
    private static float Finv(float t) => t > 0.206893f ? t * t * t : (t - 16f / 116f) / 7.787f;

    public static float Luminance(byte r, byte g, byte b) =>
        0.212671f * ToLinear[r] + 0.715160f * ToLinear[g] + 0.072169f * ToLinear[b];

    public static float Lightness(byte r, byte g, byte b) => 116f * F(Luminance(r, g, b)) - 16f;

    private static byte Enc(float v) => ToSrgb[(int)(Math.Clamp(v, 0f, 1f) * Lut)];

    /// <summary>화소의 밝기는 그대로, 색만 (a, b) 로 바꾼다.</summary>
    public static void Apply(ref byte r, ref byte g, ref byte b, float a, float bb)
    {
        var y = Luminance(r, g, b);
        var fy = F(y);
        var x = Finv(fy + a / 500f) * 0.95047f;
        var z = Finv(fy - bb / 200f) * 1.08883f;
        var rr = 3.240479f * x - 1.537150f * y - 0.498535f * z;
        var gg = -0.969256f * x + 1.875992f * y + 0.041556f * z;
        var b2 = 0.055648f * x - 0.204043f * y + 1.057311f * z;
        r = Enc(rr);
        g = Enc(gg);
        b = Enc(b2);
    }
}

/// <summary>화소 배열 (stride = width * bpp). bpp 는 3(RGB24) 또는 4(BGRA32).</summary>
public sealed class PixelFrame
{
    public int Width { get; }
    public int Height { get; }
    public int Bpp { get; }
    public bool Bgr { get; }
    public byte[] Data { get; }

    public PixelFrame(int w, int h, int bpp, bool bgr, byte[]? data = null)
    {
        Width = w;
        Height = h;
        Bpp = bpp;
        Bgr = bgr;
        Data = data ?? new byte[w * h * bpp];
    }

    public (byte r, byte g, byte b) Get(int x, int y)
    {
        var o = (y * Width + x) * Bpp;
        return Bgr ? (Data[o + 2], Data[o + 1], Data[o]) : (Data[o], Data[o + 1], Data[o + 2]);
    }
}

internal static class Ort
{
    public static SessionOptions Options()
    {
        var o = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
        };
        // int8 로 줄여 둔 가중치를 세션을 만들 때 한 번만 풀어 둔다.
        o.AddSessionConfigEntry("session.disable_quant_qdq", "1");
        return o;
    }
}

/// <summary>흑백 → 컬러 (Zhang et al. SIGGRAPH 2017 자동 컬러화).</summary>
public sealed class Colorizer : IDisposable
{
    public const int N = 256;
    private readonly InferenceSession _session;

    public Colorizer() => _session = new InferenceSession(ModelStore.PathOf(AiModel.Colorize), Ort.Options());

    /// <summary>l: N×N L(0~100), 위→아래. 반환: a 평면 다음 b 평면.</summary>
    public float[] Predict(float[] l)
    {
        var t = new DenseTensor<float>(l, new[] { 1, 1, N, N });
        using var r = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("l", t) });
        return r.First().AsEnumerable<float>().ToArray();
    }

    /// <summary>프레임을 N×N 으로 줄여(박스 평균) 밝기 L 배열을 만든다.</summary>
    public static float[] SmallL(PixelFrame f)
    {
        var l = new float[N * N];
        for (var y = 0; y < N; y++)
        {
            var y0 = y * f.Height / N;
            var y1 = Math.Max(y0 + 1, (y + 1) * f.Height / N);
            for (var x = 0; x < N; x++)
            {
                var x0 = x * f.Width / N;
                var x1 = Math.Max(x0 + 1, (x + 1) * f.Width / N);
                // 큰 영상은 몇 화소만 골라 평균 (속도)
                var sy = Math.Max(1, (y1 - y0) / 3);
                var sx = Math.Max(1, (x1 - x0) / 3);
                float sum = 0;
                var cnt = 0;
                for (var yy = y0; yy < y1; yy += sy)
                    for (var xx = x0; xx < x1; xx += sx)
                    {
                        var (r, g, b) = f.Get(xx, yy);
                        sum += Lab.Lightness(r, g, b);
                        cnt++;
                    }
                l[y * N + x] = sum / cnt;
            }
        }
        return l;
    }

    /// <summary>원본 해상도의 밝기에 ab(N×N)를 부드럽게 늘려 입힌다. 병렬 처리.</summary>
    public static void ApplyAb(PixelFrame f, float[] ab)
    {
        const int o = N * N;
        Parallel.For(0, f.Height, y =>
        {
            var fy = Math.Clamp((y + 0.5f) * N / f.Height - 0.5f, 0f, N - 1f);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, N - 1);
            var ty = fy - y0;
            for (var x = 0; x < f.Width; x++)
            {
                var fx = Math.Clamp((x + 0.5f) * N / f.Width - 0.5f, 0f, N - 1f);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, N - 1);
                var tx = fx - x0;
                float w00 = (1 - tx) * (1 - ty), w01 = tx * (1 - ty), w10 = (1 - tx) * ty, w11 = tx * ty;
                int i00 = y0 * N + x0, i01 = y0 * N + x1, i10 = y1 * N + x0, i11 = y1 * N + x1;
                var a = ab[i00] * w00 + ab[i01] * w01 + ab[i10] * w10 + ab[i11] * w11;
                var b = ab[o + i00] * w00 + ab[o + i01] * w01 + ab[o + i10] * w10 + ab[o + i11] * w11;
                var p = (y * f.Width + x) * f.Bpp;
                var d = f.Data;
                if (f.Bgr) Lab.Apply(ref d[p + 2], ref d[p + 1], ref d[p], a, b);
                else Lab.Apply(ref d[p], ref d[p + 1], ref d[p + 2], a, b);
            }
        });
    }

    public void Colorize(PixelFrame f) => ApplyAb(f, Predict(SmallL(f)));

    public void Dispose() => _session.Dispose();
}

/// <summary>Real-ESRGAN general x4v3 (경량) 4배 확대. 조각(타일)으로 나눠 처리한다.</summary>
public sealed class Upscaler : IDisposable
{
    public const int Scale = 4;
    public const int MaxInput = 1280;
    private const int Tile = 192;
    private const int Pad = 10;
    private readonly InferenceSession _session;

    public Upscaler() => _session = new InferenceSession(ModelStore.PathOf(AiModel.Upscale), Ort.Options());

    public PixelFrame Upscale(PixelFrame src, IProgress<double>? progress, CancellationToken ct)
    {
        int w = src.Width, h = src.Height;
        var outF = new PixelFrame(w * Scale, h * Scale, src.Bpp, src.Bgr);
        if (src.Bpp == 4) for (var i = 3; i < outF.Data.Length; i += 4) outF.Data[i] = 255;
        var tilesX = (w + Tile - 1) / Tile;
        var tilesY = (h + Tile - 1) / Tile;
        var done = 0;
        for (var ty = 0; ty < tilesY; ty++)
            for (var tx = 0; tx < tilesX; tx++)
            {
                ct.ThrowIfCancellationRequested();
                int ix = tx * Tile, iy = ty * Tile;
                int iw = Math.Min(Tile, w - ix), ih = Math.Min(Tile, h - iy);
                int x0 = Math.Max(0, ix - Pad), y0 = Math.Max(0, iy - Pad);
                int x1 = Math.Min(w, ix + iw + Pad), y1 = Math.Min(h, iy + ih + Pad);
                int pw = x1 - x0, ph = y1 - y0, plane = pw * ph;
                var chw = new float[3 * plane];
                for (var y = 0; y < ph; y++)
                    for (var x = 0; x < pw; x++)
                    {
                        var (r, g, b) = src.Get(x0 + x, y0 + y);
                        var i = y * pw + x;
                        chw[i] = r / 255f;
                        chw[plane + i] = g / 255f;
                        chw[2 * plane + i] = b / 255f;
                    }
                float[] res;
                using (var r = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("rgb", new DenseTensor<float>(chw, new[] { 1, 3, ph, pw })) }))
                    res = r.First().AsEnumerable<float>().ToArray();
                int ow = pw * Scale, op = ow * ph * Scale;
                int offX = (ix - x0) * Scale, offY = (iy - y0) * Scale;
                for (var yy = 0; yy < ih * Scale; yy++)
                    for (var xx = 0; xx < iw * Scale; xx++)
                    {
                        var si = (offY + yy) * ow + offX + xx;
                        var rr = (byte)Math.Clamp((int)(res[si] * 255f + 0.5f), 0, 255);
                        var gg = (byte)Math.Clamp((int)(res[op + si] * 255f + 0.5f), 0, 255);
                        var bb = (byte)Math.Clamp((int)(res[2 * op + si] * 255f + 0.5f), 0, 255);
                        var p = ((iy * Scale + yy) * outF.Width + ix * Scale + xx) * outF.Bpp;
                        if (src.Bgr) { outF.Data[p] = bb; outF.Data[p + 1] = gg; outF.Data[p + 2] = rr; }
                        else { outF.Data[p] = rr; outF.Data[p + 1] = gg; outF.Data[p + 2] = bb; }
                    }
                done++;
                progress?.Report((double)done / (tilesX * tilesY));
            }
        return outF;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// 동영상 AI 컬러화: ffmpeg 로 프레임을 풀어 받아 색을 입히고, 다시 ffmpeg 로 인코딩한다 (소리는 원본에서 그대로).
/// 프레임마다 색이 깜빡이지 않게 앞 프레임 색과 섞고, 두 프레임마다 한 번 AI 를 돌린다.
/// </summary>
public static class AiVideo
{
    public static async Task ColorizeAsync(string input, string output, IProgress<double>? progress, CancellationToken ct)
    {
        var probe = await Ffmpeg.ProbeAsync(input, ct);
        if (!probe.HasVideo || probe.Width <= 0 || probe.Height <= 0) throw new IOException("영상 정보를 읽지 못했습니다.");
        var (fps, rotated) = await StreamInfoAsync(input, ct);
        int w = (rotated ? probe.Height : probe.Width) & ~1, h = (rotated ? probe.Width : probe.Height) & ~1;

        var dec = Ffmpeg.StartInfo(new[] { "-v", "error", "-i", input, "-map", "0:v:0", "-vf", $"scale={w}:{h}",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-" }, redirectStdout: true);
        var encArgs = new List<string> { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}",
            "-r", fps, "-i", "-", "-i", input, "-map", "0:v:0", "-map", "1:a:0?", "-c:v", "libx264", "-preset", "veryfast",
            "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output };
        var enc = Ffmpeg.StartInfo(encArgs, redirectStdin: true);

        using var colorizer = new Colorizer();
        using var pd = Process.Start(dec)!;
        using var pe = Process.Start(enc)!;
        var errD = pd.StandardError.ReadToEndAsync(ct);
        var errE = pe.StandardError.ReadToEndAsync(ct);
        var total = Math.Max(1, (long)(probe.DurationMs / 1000.0 * double.Parse(fps, System.Globalization.CultureInfo.InvariantCulture)));
        var frameBytes = w * h * 3;
        var buf = new byte[frameBytes];
        float[]? prev = null;
        float prevMean = -1;
        long n = 0;
        try
        {
            var src = pd.StandardOutput.BaseStream;
            var dst = pe.StandardInput.BaseStream;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = 0;
                while (read < frameBytes)
                {
                    var r = await src.ReadAsync(buf.AsMemory(read, frameBytes - read), ct);
                    if (r <= 0) break;
                    read += r;
                }
                if (read < frameBytes) break;
                var f = new PixelFrame(w, h, 3, false, buf);
                if (prev == null || n % 2 == 0)
                {
                    var l = Colorizer.SmallL(f);
                    var mean = l.Average();
                    var ab = await Task.Run(() => colorizer.Predict(l), ct);
                    if (prev != null && Math.Abs(mean - prevMean) < 6f)
                        for (var i = 0; i < ab.Length; i++) ab[i] = prev[i] * 0.45f + ab[i] * 0.55f;
                    prev = ab;
                    prevMean = mean;
                }
                await Task.Run(() => Colorizer.ApplyAb(f, prev!), ct);
                await dst.WriteAsync(buf, ct);
                n++;
                progress?.Report(Math.Clamp((double)n / total, 0, 0.99));
            }
            pe.StandardInput.Close();
            await pe.WaitForExitAsync(ct);
            await pd.WaitForExitAsync(ct);
        }
        catch
        {
            try { pd.Kill(true); } catch { }
            try { pe.Kill(true); } catch { }
            Ffmpeg.TryDelete(output);
            throw;
        }
        if (pe.ExitCode != 0 || !File.Exists(output))
        {
            var log = await errE;
            Log.Write("ai colorize", null, log + await errD);
            Ffmpeg.TryDelete(output);
            throw new IOException("AI 컬러화 저장에 실패했습니다.\n" + string.Join("\n", log.Split('\n').TakeLast(3)));
        }
        progress?.Report(1);
    }

    /// <summary>초당 프레임 수와, 세로로 돌려 보여 주는 영상(90/270도)인지.</summary>
    private static async Task<(string fps, bool rotated)> StreamInfoAsync(string input, CancellationToken ct)
    {
        var (_, log) = await Ffmpeg.RunAsync(new[] { "-i", input }, 0, null, ct);
        var m = System.Text.RegularExpressions.Regex.Match(log, @"Video:.*?,\s*([\d.]+)\s*fps");
        var r = System.Text.RegularExpressions.Regex.Match(log, @"rotat\w*\s*(?:of|:)\s*(-?\d+)");
        var deg = r.Success && int.TryParse(r.Groups[1].Value, out var d) ? Math.Abs(d) % 180 : 0;
        return (m.Success ? m.Groups[1].Value : "30", deg == 90);
    }
}
