using System.Security.Cryptography;
using System.Text;

namespace MiHome.Core.Crypto;

/// <summary>
/// 米家云 API 的签名与 RC4 加解密。
/// 移植自 mijiaAPI 的 miutils.py（gen_nonce / get_signed_nonce /
/// gen_enc_signature / generate_enc_params / encrypt_rc4 / decrypt_rc4）。
/// </summary>
public static class MiCrypto
{
    private static readonly Random Random = new();

    /// <summary>
    /// 生成 nonce：8 字节有符号随机数（大端）+ 毫秒/60000 的分钟数（大端最小字节）。
    /// </summary>
    public static string GenNonce()
    {
        long millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> buf = stackalloc byte[16];
        // 64 位随机数，按 signed=True 写入 8 字节大端
        long r = Random.NextInt64();
        WriteInt64BigEndian(buf, r);
        long part2 = millis / 60000;
        int byteCount = part2 == 0 ? 1 : (63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)part2)) / 8 + 1;
        // Python: (part2.bit_length()+7)//8；part2 恒为非负（毫秒时间戳为正）
        WriteInt64BigEndian(buf.Slice(8, byteCount), part2);
        return Convert.ToBase64String(buf.Slice(0, 8 + byteCount).ToArray());
    }

    private static void WriteInt64BigEndian(Span<byte> dst, long value)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            int shift = 8 * (dst.Length - 1 - i);
            dst[i] = (byte)((value >> shift) & 0xFF);
        }
    }

    /// <summary>SHA256(ssecurity_bytes || nonce_bytes) 的 Base64。</summary>
    public static string GetSignedNonce(string ssecurity, string nonce)
    {
        byte[] ssec = Convert.FromBase64String(ssecurity);
        byte[] non = Convert.FromBase64String(nonce);
        byte[] data = new byte[ssec.Length + non.Length];
        ssec.CopyTo(data, 0);
        non.CopyTo(data, ssec.Length);
        return Convert.ToBase64String(SHA256.HashData(data));
    }

    /// <summary>签名串 = method &amp; uri &amp; k=v... &amp; signedNonce，SHA1 后 Base64。</summary>
    public static string GenEncSignature(string uri, string method, string signedNonce,
        IDictionary<string, string> parameters)
    {
        var parts = new List<string> { method.ToUpperInvariant(), uri };
        foreach (var (k, v) in parameters)
        {
            parts.Add($"{k}={v}");
        }
        parts.Add(signedNonce);
        byte[] data = Encoding.UTF8.GetBytes(string.Join("&", parts));
        return Convert.ToBase64String(SHA1.HashData(data));
    }

    /// <summary>
    /// 构造加密请求参数：先对原始参数计算 rc4_hash__，再把每个值 RC4 加密，
    /// 最后追加重定向签名、ssecurity 与 _nonce。注意会原地修改传入字典。
    /// </summary>
    public static Dictionary<string, string> GenerateEncParams(string uri, string method,
        string signedNonce, string nonce, Dictionary<string, string> parameters, string ssecurity)
    {
        parameters["rc4_hash__"] = GenEncSignature(uri, method, signedNonce, parameters);

        foreach (var key in parameters.Keys.ToList())
        {
            parameters[key] = EncryptRc4(signedNonce, parameters[key]);
        }

        parameters["signature"] = GenEncSignature(uri, method, signedNonce, parameters);
        parameters["ssecurity"] = ssecurity;
        parameters["_nonce"] = nonce;
        return parameters;
    }

    /// <summary>RC4 加密（先丢弃 1024 字节 keystream），结果 Base64。</summary>
    public static string EncryptRc4(string keyB64, string payload)
        => Convert.ToBase64String(Rc4Crypt(Convert.FromBase64String(keyB64), Encoding.UTF8.GetBytes(payload)));

    /// <summary>RC4 解密 Base64 载荷。</summary>
    public static byte[] DecryptRc4Raw(string keyB64, string payloadB64)
        => Rc4Crypt(Convert.FromBase64String(keyB64), Convert.FromBase64String(payloadB64));

    /// <summary>
    /// 解密 API 响应：签名密钥 = GetSignedNonce(ssecurity, nonce)，
    /// 明文为 UTF-8，失败时按 gzip 解压后再解 UTF-8。
    /// </summary>
    public static string DecryptResponse(string ssecurity, string nonce, string payloadB64)
    {
        byte[] key = Convert.FromBase64String(GetSignedNonce(ssecurity, nonce));
        byte[] decrypted = Rc4Crypt(key, Convert.FromBase64String(payloadB64));
        try
        {
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (DecoderFallbackException)
        {
            using var input = new MemoryStream(decrypted);
            using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    /// <summary>标准 RC4：先加密并丢弃 1024 字节（与 ARC4.new(...).encrypt(bytes(1024)) 对齐）。</summary>
    private static byte[] Rc4Crypt(byte[] key, byte[] data)
    {
        // KSA
        var s = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        // PRGA：丢弃 1024 字节后处理真实数据
        var result = new byte[data.Length];
        int i2 = 0, j2 = 0, k = 0;
        for (int index = 0; index < 1024 + data.Length; index++)
        {
            i2 = (i2 + 1) & 0xFF;
            j2 = (j2 + s[i2]) & 0xFF;
            (s[i2], s[j2]) = (s[j2], s[i2]);
            byte streamByte = s[(s[i2] + s[j2]) & 0xFF];
            if (index >= 1024)
            {
                result[k++] = (byte)(data[index - 1024] ^ streamByte);
            }
        }
        return result;
    }
}
