using System.Globalization;

namespace EveryVideo.Services;

public static class TimeFormat
{
    public static string Format(long ms, bool withMillis = false)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        var s = t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
        return withMillis ? $"{s}.{t.Milliseconds / 100}" : s;
    }

    /// <summary>"1:02:03.5", "02:03", "123.4" 같은 글자를 ms 로 바꾼다.</summary>
    public static bool TryParse(string text, out long ms)
    {
        ms = 0;
        text = text.Trim();
        if (text.Length == 0) return false;
        var parts = text.Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        ms = (long)Math.Round(total * 1000);
        return true;
    }

    /// <summary>ffmpeg 인자용 "123.456"</summary>
    public static string Seconds(long ms) => (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}
