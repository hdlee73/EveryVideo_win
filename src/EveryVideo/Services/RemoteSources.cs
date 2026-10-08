using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using FluentFTP;

namespace EveryVideo.Services;

public sealed record RemoteItem(string Name, string Location, bool IsFolder, long Size)
{
    public string SizeText => IsFolder ? "" : Size switch
    {
        > 1L << 30 => $"{Size / (double)(1L << 30):0.0} GB",
        > 1L << 20 => $"{Size / (double)(1L << 20):0.0} MB",
        > 0 => $"{Size / 1024.0:0} KB",
        _ => "",
    };
}

/// <summary>구글 드라이브 링크, FTP, SMB(윈도우 공유 폴더) 주소를 다룬다.</summary>
public static class RemoteSources
{
    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".asf", ".flv", ".f4v", ".ts", ".m2ts", ".mts",
        ".mpg", ".mpeg", ".mpe", ".vob", ".3gp", ".3g2", ".ogv", ".ogg", ".ogm", ".divx", ".xvid", ".rm", ".rmvb",
        ".dv", ".mxf", ".m2v", ".y4m", ".hevc", ".h264", ".264", ".265", ".m3u8", ".mpd", ".tp", ".trp", ".dat",
        ".mp3", ".m4a", ".flac", ".wav", ".aac", ".opus", ".wma", ".ac3", ".dts", ".mka",
    };

    public static bool IsMedia(string name) =>
        VideoExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    public static string FileDialogFilter =>
        "동영상/음악|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) + "|모든 파일|*.*";

    // ---------- 구글 드라이브 ----------

    public static bool IsDriveLink(string text) =>
        text.Contains("drive.google.com") || text.Contains("docs.google.com") || text.Contains("drive.usercontent.google.com");

    /// <summary>공유 링크에서 파일 ID 를 뽑아 바로 스트리밍할 수 있는 주소로 바꾼다.</summary>
    public static string? DriveDirectUrl(string link)
    {
        var m = Regex.Match(link, @"/d/([A-Za-z0-9_-]{10,})");
        if (!m.Success) m = Regex.Match(link, @"[?&]id=([A-Za-z0-9_-]{10,})");
        return m.Success
            ? $"https://drive.usercontent.google.com/download?id={m.Groups[1].Value}&export=download&confirm=t"
            : null;
    }

    /// <summary>Google Drive for desktop 이 연결한 드라이브(보통 G:\내 드라이브) 위치.</summary>
    public static string? FindGoogleDriveFolder()
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (d.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) ||
                    d.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(d.RootDirectory.FullName, ".shortcut-targets-by-id")))
                {
                    foreach (var name in new[] { "내 드라이브", "My Drive" })
                    {
                        var p = Path.Combine(d.RootDirectory.FullName, name);
                        if (Directory.Exists(p)) return p;
                    }
                    return d.RootDirectory.FullName;
                }
            }
        }
        catch (Exception e)
        {
            Log.Write("gdrive find", e);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "Google Drive", "My Drive", "내 드라이브", Path.Combine("Google Drive", "My Drive") })
        {
            var p = Path.Combine(home, name);
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    // ---------- 주소 정리 ----------

    /// <summary>사용자가 입력한 주소를 재생 가능한 위치로 바꾼다. smb:// 는 UNC 경로(\\서버\공유)로 바꾼다.</summary>
    public static string Normalize(string input)
    {
        var s = input.Trim().Trim('"');
        if (IsDriveLink(s)) return DriveDirectUrl(s) ?? s;
        if (s.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(s);
            return @"\\" + u.Host + Uri.UnescapeDataString(u.AbsolutePath).Replace('/', '\\').TrimEnd('\\');
        }
        return s;
    }

    public static bool IsUrl(string s) => Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9+.-]*://") && !s.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    /// <summary>화면에 보일 이름. FTP 비밀번호는 감춘다.</summary>
    public static string DisplayName(string location)
    {
        if (IsUrl(location))
        {
            if (Uri.TryCreate(location, UriKind.Absolute, out var u))
            {
                if (location.Contains("drive.usercontent.google.com") || location.Contains("drive.google.com")) return "구글 드라이브 동영상";
                var name = Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath));
                return string.IsNullOrEmpty(name) ? u.Host : name;
            }
            return location;
        }
        return Path.GetFileName(location.TrimEnd('\\', '/'));
    }

    /// <summary>즐겨찾기·이어보기 기준 키. 주소의 계정 정보는 뺀다.</summary>
    public static string Key(string location)
    {
        if (IsUrl(location) && Uri.TryCreate(location, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.UserInfo))
            return new UriBuilder(u) { UserName = "", Password = "" }.Uri.ToString();
        return location;
    }

    // ---------- FTP ----------

    public static string FtpUrl(ServerEntry s, string path)
    {
        var b = new UriBuilder("ftp", s.Host, s.Port > 0 ? s.Port : 21) { Path = path };
        if (!string.IsNullOrEmpty(s.User))
        {
            b.UserName = Uri.EscapeDataString(s.User);
            b.Password = Uri.EscapeDataString(s.Password);
        }
        return b.Uri.AbsoluteUri;
    }

    public static async Task<List<RemoteItem>> ListFtpAsync(ServerEntry s, string path, CancellationToken ct)
    {
        await using var client = new AsyncFtpClient(s.Host,
            string.IsNullOrEmpty(s.User) ? "anonymous" : s.User,
            string.IsNullOrEmpty(s.User) ? "anonymous@" : s.Password,
            s.Port > 0 ? s.Port : 21);
        client.Config.EncryptionMode = FtpEncryptionMode.Auto;
        client.Config.ValidateAnyCertificate = true;
        client.Config.ConnectTimeout = 10000;
        await client.AutoConnect(ct);
        var items = await client.GetListing(string.IsNullOrEmpty(path) ? "/" : path, ct);
        return items
            .Where(i => i.Type == FtpObjectType.Directory || (i.Type == FtpObjectType.File && IsMedia(i.Name)))
            .Select(i => new RemoteItem(i.Name, i.FullName, i.Type == FtpObjectType.Directory, i.Size))
            .OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---------- SMB (윈도우 공유 폴더) ----------

    public static string SmbRoot(ServerEntry s) =>
        @"\\" + s.Host + (string.IsNullOrWhiteSpace(s.Path) ? "" : @"\" + s.Path.Trim('/', '\\').Replace('/', '\\'));

    /// <summary>계정이 있으면 net use 로 먼저 연결한다.</summary>
    public static async Task ConnectSmbAsync(ServerEntry s)
    {
        if (string.IsNullOrEmpty(s.User)) return;
        var share = s.Path.Trim('/', '\\').Split('/', '\\')[0];
        if (string.IsNullOrEmpty(share)) return;
        var psi = new ProcessStartInfo("net")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "use", $@"\\{s.Host}\{share}", s.Password, "/user:" + s.User, "/persistent:no" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    public static Task<List<RemoteItem>> ListSmbAsync(string folder, CancellationToken ct) => Task.Run(() =>
    {
        var dir = new DirectoryInfo(folder);
        var list = new List<RemoteItem>();
        foreach (var d in dir.EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            if ((d.Attributes & FileAttributes.Hidden) == 0) list.Add(new RemoteItem(d.Name, d.FullName, true, 0));
        }
        foreach (var f in dir.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (IsMedia(f.Name)) list.Add(new RemoteItem(f.Name, f.FullName, false, f.Length));
        }
        return list.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }, ct);
}
