using System.Collections.Concurrent;
using System.IO;
using System.Text;
using FluentFTP;

namespace EveryVideo.Services;

/// <summary>
/// FTP 파일 이름 인코딩.
/// 한국 NAS·공유기 FTP 는 이름을 CP949(EUC-KR) 로 주는 곳이 많아, 제어 연결은 Latin1 로 열어
/// 바이트를 그대로 받고 이름·경로는 여기서 직접 바꾼다. 자동이면 이름 바이트가 올바른 UTF-8 인지 보고,
/// 아니면 CP949 로 읽는다.
/// </summary>
public static class FtpText
{
    public const string Auto = "auto";
    public const string Utf8 = "utf-8";
    public const string Cp949 = "cp949";

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    public static readonly Encoding Korean;

    /// <summary>서버(host:port)별로 알아낸 인코딩. 재생할 때도 같은 인코딩을 쓴다.</summary>
    private static readonly ConcurrentDictionary<string, Encoding> Detected = new();

    static FtpText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Korean = Encoding.GetEncoding(949);
    }

    public static string Normalize(string? mode) => mode?.ToLowerInvariant() switch
    {
        Utf8 or "utf8" => Utf8,
        Cp949 or "euc-kr" or "ms949" => Cp949,
        _ => Auto,
    };

    private static bool IsAscii(string s)
    {
        foreach (var c in s) if (c >= 0x80) return false;
        return true;
    }

    private static string? TryUtf8(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>한 FTP 연결의 인코딩 상태.</summary>
    public sealed class Session
    {
        private readonly string _key;
        public string Mode { get; }
        public Encoding Charset { get; private set; }

        public Session(string host, int port, string? mode)
        {
            _key = $"{host.ToLowerInvariant()}:{port}";
            Mode = Normalize(mode);
            Charset = Mode switch
            {
                Cp949 => Korean,
                Utf8 => Encoding.UTF8,
                _ => Detected.TryGetValue(_key, out var e) ? e : Encoding.UTF8,
            };
        }

        /// <summary>제어 연결 설정: Latin1 로 고정해 바이트를 그대로 주고받는다.</summary>
        public void Configure(BaseFtpClient client) => client.Encoding = Latin1;

        /// <summary>유니코드 경로 → 서버로 보낼 문자열(바이트를 Latin1 로 담은 것).</summary>
        public string Encode(string path, Encoding? cs = null) => Latin1.GetString((cs ?? Charset).GetBytes(path));

        /// <summary>서버가 준 문자열(Latin1) → 유니코드.</summary>
        public string Decode(string raw)
        {
            if (IsAscii(raw)) return raw;
            var bytes = Latin1.GetBytes(raw);
            if (Mode == Auto) return TryUtf8(bytes) ?? Korean.GetString(bytes);
            return Charset.GetString(bytes);
        }

        /// <summary>목록에서 본 이름들로 서버 인코딩을 정한다(자동).</summary>
        public void Learn(IEnumerable<string> rawNames)
        {
            if (Mode != Auto) return;
            var nonAscii = rawNames.Where(n => !IsAscii(n)).ToList();
            if (nonAscii.Count == 0) return;
            Charset = nonAscii.All(n => TryUtf8(Latin1.GetBytes(n)) != null) ? Encoding.UTF8 : Korean;
            Detected[_key] = Charset;
        }

        private IEnumerable<Encoding> Candidates()
        {
            if (Mode != Auto) return new[] { Charset };
            return new[] { Charset, Encoding.UTF8, Korean }.Distinct();
        }

        private void Remember(Encoding cs)
        {
            Charset = cs;
            if (Mode == Auto) Detected[_key] = cs;
        }

        public async Task<string> ResolveDirAsync(AsyncFtpClient c, string path, CancellationToken ct)
        {
            if (Mode != Auto || IsAscii(path)) return Encode(path);
            foreach (var cs in Candidates())
            {
                var p = Encode(path, cs);
                if (await c.DirectoryExists(p, ct)) { Remember(cs); return p; }
            }
            return Encode(path);
        }

        public string ResolveFile(FtpClient c, string path, out long size)
        {
            size = -1;
            foreach (var cs in Candidates())
            {
                var p = Encode(path, cs);
                var s = c.GetFileSize(p, -1);
                if (s >= 0) { size = s; Remember(cs); return p; }
            }
            return Encode(path);
        }
    }

    /// <summary>
    /// 이름에 한글 등이 있는 ftp:// 주소. LibVLC 의 ftp 는 서버가 UTF8 을 알리지 않으면
    /// ASCII 경로만 받으므로, 이런 주소는 앱이 직접 읽어 넘긴다.
    /// </summary>
    public static bool NeedsOwnStream(string location) =>
        location.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(location, UriKind.Absolute, out var u)
        && !IsAscii(Uri.UnescapeDataString(u.AbsolutePath));
}

/// <summary>FTP 파일을 이어받기(REST)로 앞뒤로 옮겨 가며 읽는 스트림. LibVLC StreamMediaInput 에 쓴다.</summary>
public sealed class FtpReadStream : Stream
{
    private readonly object _lock = new();
    private readonly Uri _uri;
    private readonly string? _encoding;
    private FtpClient? _client;
    private string _path = "";
    private long _length = -1;
    private bool _failed;
    private Stream? _inner;
    private long _pos;
    private volatile bool _disposed;

    /// <summary>
    /// ftp://user:pass@host:port/경로. encoding 은 서버 설정(auto/utf-8/cp949).
    /// 연결은 LibVLC 가 처음 읽을 때(재생 스레드) 해서 화면이 멈추지 않는다.
    /// </summary>
    public FtpReadStream(string location, string? encoding)
    {
        _uri = new Uri(location);
        _encoding = encoding;
    }

    /// <summary>잠금 안에서 부른다. 실패하면 false (LibVLC 콜백으로 예외를 넘기지 않는다).</summary>
    private bool EnsureOpen()
    {
        if (_client != null) return true;
        if (_failed || _disposed) return false;
        var u = _uri;
        string user = "anonymous", pass = "anonymous@";
        if (!string.IsNullOrEmpty(u.UserInfo))
        {
            var i = u.UserInfo.IndexOf(':');
            user = Uri.UnescapeDataString(i >= 0 ? u.UserInfo[..i] : u.UserInfo);
            pass = i >= 0 ? Uri.UnescapeDataString(u.UserInfo[(i + 1)..]) : "";
        }
        var port = u.Port > 0 ? u.Port : 21;
        var session = new FtpText.Session(u.Host, port, _encoding);
        var client = new FtpClient(u.Host, user, pass, port);
        session.Configure(client);
        client.Config.EncryptionMode = FtpEncryptionMode.Auto;
        client.Config.ValidateAnyCertificate = true;
        client.Config.ConnectTimeout = 10000;
        client.Config.ReadTimeout = 30000;
        client.Config.DataConnectionReadTimeout = 30000;
        try
        {
            client.Connect();
            if (session.Mode != FtpText.Cp949) client.Execute("OPTS UTF8 ON");
            _path = session.ResolveFile(client, Uri.UnescapeDataString(u.AbsolutePath), out _length);
            // SIZE 를 모르는 서버: 길이를 모르는 채로 끝까지 읽는다.
            if (_length < 0) _length = long.MaxValue;
            _client = client;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("ftp open", ex, u.Host + u.AbsolutePath);
            _failed = true;
            try { client.Dispose(); } catch { }
            return false;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Length
    {
        get { lock (_lock) return EnsureOpen() ? _length : 0; }
    }

    public override long Position
    {
        get => _pos;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (!EnsureOpen() || _pos >= _length || count == 0) return 0;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _inner ??= _client!.OpenRead(_path, FtpDataType.Binary, _pos);
                    var n = _inner.Read(buffer, offset, (int)Math.Min(count, _length - _pos));
                    _pos += n;
                    return n;
                }
                catch (Exception ex)
                {
                    if (_disposed) return 0;
                    Log.Write("ftp read", ex, _path);
                    CloseInner();
                    if (attempt > 0) return 0;
                    // 데이터 연결이 끊기면 지금 위치부터 한 번 다시 연다.
                    try { if (!_client!.IsConnected) _client.Connect(); } catch { return 0; }
                }
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_lock)
        {
            var len = EnsureOpen() ? _length : 0;
            var target = origin switch
            {
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => len + offset,
                _ => offset,
            };
            target = Math.Clamp(target, 0, Math.Max(0, len));
            if (target != _pos) CloseInner();
            _pos = target;
            return _pos;
        }
    }

    private void CloseInner()
    {
        var s = _inner;
        _inner = null;
        try { s?.Dispose(); } catch { }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            // 읽는 중일 수 있으므로 잠그지 않고 연결부터 끊어 막힌 읽기를 풀어 준다.
            _disposed = true;
            try { _client?.Dispose(); } catch { }
            lock (_lock) CloseInner();
        }
        base.Dispose(disposing);
    }
}
