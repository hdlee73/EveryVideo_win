using System.Globalization;

namespace EveryVideo.Services;

/// <summary>
/// 화질 개선 (선명하게 · 노이즈 줄이기 · 업스케일). 재생은 LibVLC 필터, 저장은 ffmpeg 필터로 같은 효과를 낸다.
/// </summary>
public static class Quality
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static bool IsOff(Settings s) => s.Sharpen <= 0 && s.Denoise <= 0 && !s.Upscale;

    public static string Label(Settings s)
    {
        var parts = new List<string>();
        if (s.Denoise > 0) parts.Add($"노이즈 줄이기 {s.Denoise}");
        if (s.Upscale) parts.Add("업스케일");
        if (s.Sharpen > 0) parts.Add($"선명하게 {s.Sharpen}");
        return parts.Count == 0 ? "끔" : string.Join(" · ", parts);
    }

    /// <summary>재생 엔진(LibVLC)을 만들 때 넘길 옵션. 필터 값은 엔진을 만들 때만 정할 수 있다.</summary>
    public static List<string> VlcArgs(Settings s)
    {
        var args = new List<string>();
        var filters = new List<string>();
        if (s.Denoise > 0)
        {
            var k = 0.3 + 1.2 * s.Denoise / 100.0;
            filters.Add("hqdn3d");
            args.Add("--hqdn3d-luma-spat=" + F(4 * k));
            args.Add("--hqdn3d-chroma-spat=" + F(3 * k));
            args.Add("--hqdn3d-luma-temp=" + F(6 * k));
            args.Add("--hqdn3d-chroma-temp=" + F(4.5 * k));
        }
        if (s.Sharpen > 0)
        {
            filters.Add("sharpen");
            args.Add("--sharpen-sigma=" + F(0.05 + 1.15 * s.Sharpen / 100.0));
        }
        if (filters.Count > 0) args.Add("--video-filter=" + string.Join(":", filters));
        // NVIDIA RTX · 최신 인텔 그래픽은 그래픽 카드의 '슈퍼 해상도'로, 그 밖에는 그래픽 카드 확대기로 키운다.
        if (s.Upscale) args.Add("--d3d11-upscale-mode=super");
        return args;
    }

    /// <summary>
    /// 저장할 때 쓸 ffmpeg 영상 필터. 업스케일은 1080p(세로 영상은 1080×1920) 안쪽으로만 키운다.
    /// width/height 는 원본 크기 (모르면 0).
    /// </summary>
    public static string? FfmpegFilter(Settings s, int width, int height)
    {
        var f = new List<string>();
        if (s.Denoise > 0)
        {
            var k = 0.3 + 1.2 * s.Denoise / 100.0;
            f.Add($"hqdn3d={F(4 * k)}:{F(3 * k)}:{F(6 * k)}:{F(4.5 * k)}");
        }
        if (s.Upscale && width > 0 && height > 0)
        {
            var scale = Math.Min(4.0, Math.Min(1920.0 / Math.Max(width, height), 1080.0 / Math.Min(width, height)));
            if (scale >= 1.15)
            {
                var w = (int)(width * scale) & ~1;
                var h = (int)(height * scale) & ~1;
                f.Add($"scale={w}:{h}:flags=lanczos");
            }
        }
        if (s.Sharpen > 0) f.Add("cas=" + F(0.1 + 0.8 * s.Sharpen / 100.0));
        return f.Count == 0 ? null : string.Join(",", f);
    }
}
