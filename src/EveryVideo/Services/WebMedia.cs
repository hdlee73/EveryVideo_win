using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace EveryVideo.Services;

public sealed record WebLink(string Url, string Name, bool Streaming)
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : "";
    public string Detail => (Streaming ? "스트리밍 · " : "") + Host;
}

/// <summary>웹 페이지에서 동영상 링크를 찾고, 파일을 내려받는다.</summary>
public static class WebMedia
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124 Safari/537.36");
        return c;
    }

    private static readonly string Ext = string.Join("|", RemoteSources.VideoExtensions.Select(e => Regex.Escape(e.TrimStart('.'))));

    private static readonly Regex LinkRe = new(
        $@"(?:[""'(=]|^|\s)((?:https?:)?//[^\s""'<>()]+?\.(?:{Ext})(?:\?[^\s""'<>()]*)?|[^\s""'<>():]+?\.(?:{Ext})(?:\?[^\s""'<>()]*)?)(?=[""')\s>]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex OgRe = new(
        @"<meta[^>]+(?:property|name)=[""'](?:og:video(?::url|:secure_url)?|twitter:player:stream)[""'][^>]+content=[""']([^""']+)[""']",
        RegexOptions.IgnoreCase);

    /// <summary>파일을 바로 가리키는 주소인지 (웹 페이지가 아닌지).</summary>
    public static bool IsDirectMedia(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return true;
        if (u.Scheme != "http" && u.Scheme != "https") return true;
        if (RemoteSources.IsDriveLink(url)) return true;
        return RemoteSources.IsMedia(Uri.UnescapeDataString(u.AbsolutePath));
    }

    public static bool IsStreaming(string url)
    {
        var p = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath.ToLowerInvariant() : url.ToLowerInvariant();
        return p.EndsWith(".m3u8") || p.EndsWith(".mpd");
    }

    public static WebLink LinkOf(string url)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var u) ? Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath)) : url;
        return new WebLink(url, string.IsNullOrEmpty(name) ? url : name, IsStreaming(url));
    }

    /// <summary>페이지 안의 동영상 링크 (중복 없이 최대 60개).</summary>
    public static async Task<List<WebLink>> FindAsync(string pageUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        var final = resp.RequestMessage?.RequestUri ?? new Uri(pageUrl);
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("video/") || type.StartsWith("audio/") || type.Contains("mpegurl"))
            return new List<WebLink> { LinkOf(final.AbsoluteUri) };
        var html = (await resp.Content.ReadAsStringAsync(cts.Token)).Replace("\\/", "/");
        var found = new List<string>();
        void Add(string raw)
        {
            var c = raw.Trim().Replace("&amp;", "&");
            string? abs = c.StartsWith("//") ? final.Scheme + ":" + c
                : c.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? c
                : Uri.TryCreate(final, c, out var r) ? r.AbsoluteUri : null;
            if (abs != null && abs.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !found.Contains(abs)) found.Add(abs);
        }
        foreach (Match m in OgRe.Matches(html)) Add(m.Groups[1].Value);
        foreach (Match m in LinkRe.Matches(html)) Add(m.Groups[1].Value);
        return found.Take(60).Select(LinkOf).ToList();
    }

    /// <summary>파일을 folder 에 내려받는다. 같은 이름이 있으면 (1), (2)… 를 붙인다. 진행률 0~1.</summary>
    public static async Task<string> DownloadAsync(string url, string folder, IProgress<double> progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var name = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName;
        name = string.IsNullOrWhiteSpace(name) ? LinkOf(resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url).Name : name.Trim('"');
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        if (string.IsNullOrWhiteSpace(Path.GetExtension(name))) name += ".mp4";
        var path = Path.Combine(folder, name);
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        var total = resp.Content.Headers.ContentLength ?? -1;
        var tmp = path + ".part";
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[256 * 1024];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress.Report((double)done / total);
                }
            }
            File.Move(tmp, path);
            return path;
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* 지우지 못해도 괜찮다 */ }
            throw;
        }
    }
}
