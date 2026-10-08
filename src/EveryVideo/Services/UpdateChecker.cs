using System.Net.Http;
using System.Text.Json;

namespace EveryVideo.Services;

public sealed record UpdateInfo(string Version, string PageUrl, string? DownloadUrl, string Notes, DateTime? Published);

/// <summary>GitHub Releases 의 최신 릴리스와 현재 버전을 비교한다.</summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"EveryVideo-Windows/{AppInfo.Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>최신 릴리스 정보. 확인하지 못하면 null.</summary>
    public static async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(AppInfo.LatestReleaseApi, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            DateTime? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetDateTime().ToLocalTime() : null;

            string? download = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                var urls = assets.EnumerateArray()
                    .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString()))
                    .ToList();
                download = urls.FirstOrDefault(a => a.name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)).url
                           ?? urls.FirstOrDefault(a => a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).url;
            }
            return new UpdateInfo(tag.TrimStart('v', 'V'), page ?? AppInfo.LatestReleaseUrl, download, notes, published);
        }
        catch (Exception e)
        {
            Log.Write("update check", e);
            return null;
        }
    }

    /// <summary>latest 가 current 보다 새 버전인가. "0.2.0" > "0.1.0", "0.1.0" > "0.1.0-dev.5".</summary>
    public static bool IsNewer(string latest, string current)
    {
        var (lv, lpre) = Split(latest);
        var (cv, cpre) = Split(current);
        if (lv == null || cv == null) return false;
        var cmp = lv.CompareTo(cv);
        if (cmp != 0) return cmp > 0;
        return lpre == null && cpre != null;
    }

    private static (Version?, string?) Split(string v)
    {
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..] : null;
        if (!System.Version.TryParse(core, out var parsed)) return (null, pre);
        // 0.1 과 0.1.0 을 같게 본다.
        parsed = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return (parsed, pre);
    }
}
