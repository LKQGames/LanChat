using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanChat;

/// <summary>
/// 聊天内容传输通道：ECDH(P-256) 协商出共享密钥后，用 AES-256-GCM 逐帧加密。
/// 协议：
///   客户端首行 -> {"Type":"lanchat-handshake","Version":2,"PublicKey":"...","Salt":"..."}
///   服务端回复 -> {"Type":"lanchat-handshake-ack","Version":2,"PublicKey":"...","Salt":"..."}
///   之后每行 -> "E1:" + Base64(nonce12 || tag16 || 密文)
/// 旧版本对端不识别握手包，会直接回错误或不应答，此时退回明文以保持兼容。
/// </summary>
internal sealed class SecureChannel : IDisposable
{
    public const int ProtocolVersion = 2;
    public const string EncryptionDescription = "AES-256-GCM";

    private const string EncryptedPrefix = "E1:";
    private const string HandshakeType = "lanchat-handshake";
    private const string HandshakeAckType = "lanchat-handshake-ack";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    /// <summary>单行上限：一个 2 MB 附件分块加密后约 3.6 MB，留足余量并防御超长行导致的 OOM。</summary>
    private const int MaxLineBytes = 16 * 1024 * 1024;
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("LanChat-AES-256-GCM-v2");

    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private int _readStart;
    private int _readEnd;
    private AesGcm? _cipher;
    private bool _disposed;

    public SecureChannel(NetworkStream stream)
    {
        _stream = stream;
    }

    /// <summary>true 表示当前连接已启用 AES 加密。</summary>
    public bool IsEncrypted => _cipher is not null;

    /// <summary>
    /// 客户端握手。返回 true 表示已启用加密；
    /// 返回 false 表示对端是旧版本（回了非握手内容或超时），调用方可退回明文。
    /// </summary>
    public async Task<bool> NegotiateAsClientAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hello = new HandshakePacket
        {
            Type = HandshakeType,
            Version = ProtocolVersion,
            PublicKey = Convert.ToBase64String(keyPair.PublicKey.ExportSubjectPublicKeyInfo()),
            Salt = Convert.ToBase64String(salt)
        };
        await WriteRawAsync(JsonSerializer.Serialize(hello), cancellationToken).ConfigureAwait(false);

        string? reply;
        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(timeout);
            try
            {
                reply = await ReadRawLineAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;   // 对端没回应，按旧版本处理
            }
        }
        if (reply is null) throw new IOException("连接在加密握手阶段被关闭。");

        HandshakePacket? ack;
        try { ack = JsonSerializer.Deserialize<HandshakePacket>(reply); }
        catch (JsonException) { return false; }
        if (ack?.Type != HandshakeAckType || ack.Version != ProtocolVersion || ack.PublicKey is null) return false;

        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ack.PublicKey), out _);
        Install(await DeriveKeyAsync(keyPair, peerKey.PublicKey, salt).ConfigureAwait(false));
        return true;
    }

    /// <summary>服务端握手：客户端首行是握手包时调用。</summary>
    public async Task<bool> NegotiateAsServerAsync(HandshakePacket hello, CancellationToken cancellationToken)
    {
        if (hello.Version != ProtocolVersion || hello.PublicKey is null || hello.Salt is null) return false;
        byte[] salt;
        try { salt = Convert.FromBase64String(hello.Salt); }
        catch (FormatException) { return false; }

        using var keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ack = new HandshakePacket
        {
            Type = HandshakeAckType,
            Version = ProtocolVersion,
            PublicKey = Convert.ToBase64String(keyPair.PublicKey.ExportSubjectPublicKeyInfo()),
            Salt = hello.Salt
        };
        await WriteRawAsync(JsonSerializer.Serialize(ack), cancellationToken).ConfigureAwait(false);

        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(hello.PublicKey), out _);
        Install(await DeriveKeyAsync(keyPair, peerKey.PublicKey, salt).ConfigureAwait(false));
        return true;
    }

    private static Task<byte[]> DeriveKeyAsync(ECDiffieHellman mine, ECDiffieHellmanPublicKey theirPublicKey, byte[] salt)
    {
        // 双方得到同一个 32 字节密钥：SHA256(salt || ECDH 共享值 || 固定标签)
        var key = mine.DeriveKeyFromHash(theirPublicKey, HashAlgorithmName.SHA256, salt, KeyInfo);
        return Task.FromResult(key);
    }

    private void Install(byte[] key)
    {
        _cipher = new AesGcm(key, TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

     /// <summary>
    /// 读取一行原始文本（未解密）。自定义实现以便限制单行长度，
    /// 防止对端发送超长数据（没有换行）把内存撑爆。
    /// </summary>
    private async Task<string?> ReadRawLineAsync(CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream(4096);
        while (true)
        {
            if (TryTakeLine(buffer, out var line)) return line;
            _readStart = 0;
            _readEnd = await _stream.ReadAsync(_readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (_readEnd == 0) return null;   // 对端关闭
        }
    }

    /// <summary>在已缓冲的字节里找换行；Span 不能出现在 async 方法中，所以单独放在同步方法里。</summary>
    private bool TryTakeLine(MemoryStream buffer, out string? line)
    {
        line = null;
        if (_readStart >= _readEnd) return false;
        var span = _readBuffer.AsSpan(_readStart, _readEnd - _readStart);
        var newline = span.IndexOf((byte)'\n');
        if (newline < 0)
        {
            buffer.Write(span);
            _readStart = _readEnd;
            if (buffer.Length > MaxLineBytes) throw new IOException("数据行超过长度上限，连接已中止。");
            return false;
        }
        buffer.Write(span[..newline]);
        _readStart += newline + 1;
        if (buffer.Length > MaxLineBytes) throw new IOException("数据行超过长度上限，连接已中止。");
        line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return true;
    }
    /// <summary>读取一行并按需解密；返回 null 表示对端关闭连接。</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await ReadRawLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null) return null;
        var cipher = _cipher;
        if (cipher is null) return line;
        if (!line.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            throw new IOException("收到未加密的数据帧，连接已中止。");

        byte[] frame;
        try { frame = Convert.FromBase64String(line[EncryptedPrefix.Length..]); }
        catch (FormatException ex) { throw new IOException("加密帧格式无效。", ex); }
        return Encoding.UTF8.GetString(DecryptFrame(cipher, frame));
    }

    /// <summary>同步解密（Span 不能出现在 async 方法里）。</summary>
    private static byte[] DecryptFrame(AesGcm cipher, byte[] frame)
    {
        if (frame.Length <= NonceSize + TagSize) throw new IOException("加密帧长度异常。");
        var nonce = frame.AsSpan(0, NonceSize);
        var tag = frame.AsSpan(NonceSize, TagSize);
        var body = frame.AsSpan(NonceSize + TagSize);
        var plain = new byte[body.Length];
        try { cipher.Decrypt(nonce, body, tag, plain); }
        catch (CryptographicException ex) { throw new IOException("数据校验失败，连接可能被篡改。", ex); }
        return plain;
    }

    /// <summary>发送一行 JSON（已加密连接上会自动加密）。</summary>
    public Task WriteLineAsync(string json, CancellationToken cancellationToken)
    {
        var cipher = _cipher;
        string line;
        if (cipher is null)
        {
            line = json;
        }
        else
        {
            var plain = Encoding.UTF8.GetBytes(json);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var body = new byte[plain.Length];
            var tag = new byte[TagSize];
            cipher.Encrypt(nonce, plain, body, tag);
            var frame = new byte[NonceSize + TagSize + plain.Length];
            nonce.CopyTo(frame, 0);
            tag.CopyTo(frame, NonceSize);
            body.CopyTo(frame, NonceSize + TagSize);
            line = EncryptedPrefix + Convert.ToBase64String(frame);
        }
        return WriteRawAsync(line, cancellationToken);
    }

    private async Task WriteRawAsync(string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>把一行文本判定为握手包；不是握手包时返回 null。</summary>
    public static HandshakePacket? TryParseHandshake(string line)
    {
        if (!line.Contains("lanchat-handshake", StringComparison.Ordinal)) return null;
        HandshakePacket? packet;
        try { packet = JsonSerializer.Deserialize<HandshakePacket>(line); }
        catch (JsonException) { return null; }
        return packet?.Type == HandshakeType ? packet : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch (ObjectDisposedException) { }
        _cipher?.Dispose();
        _cipher = null;
        _writeLock.Dispose();
    }

    internal sealed class HandshakePacket
    {
        public string? Type { get; set; }
        public int Version { get; set; }
        public string? PublicKey { get; set; }
        public string? Salt { get; set; }
    }
}
