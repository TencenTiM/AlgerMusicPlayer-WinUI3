using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlgerMusicPlayer.WinUI.Services.Auth;

/// <summary>
/// 纯 C# 跨平台加密与签名辅助工具（Apple Music ES256 Developer JWT & YouTube Music SAPISIDHASH）
/// </summary>
public static class PlatformCryptoHelpers
{
    /// <summary>
    /// 使用 Apple Developer 后台下载的 .p8 私钥在纯 C# 本地签发 ES256 Developer Token
    /// </summary>
    public static string CreateAppleMusicDeveloperToken(string teamId, string keyId, string privateKeyPem, TimeSpan validDuration)
    {
        if (string.IsNullOrWhiteSpace(teamId) || teamId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(keyId) || keyId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException("请先配置有效的 Apple Music TeamId、KeyId 与 .p8 私钥内容。");
        }

        string cleanBase64 = privateKeyPem
            .Replace("-----BEGIN PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-----END PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\\n", string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Trim();

        byte[] keyBytes = Convert.FromBase64String(cleanBase64);

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long expUnix = DateTimeOffset.UtcNow.Add(validDuration).ToUnixTimeSeconds();

        string headerJson = JsonSerializer.Serialize(new
        {
            alg = "ES256",
            kid = keyId.Trim()
        });

        string payloadJson = JsonSerializer.Serialize(new
        {
            iss = teamId.Trim(),
            iat = nowUnix,
            exp = expUnix
        });

        string encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        string encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        string signingInput = $"{encodedHeader}.{encodedPayload}";

        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);

        byte[] signature = ecdsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    /// <summary>
    /// 根据提取的 SAPISID Cookie 动态计算 YouTube Music 请求头 Authorization: SAPISIDHASH
    /// </summary>
    public static string CreateYouTubeMusicSapisidHash(string sapisid, string origin = "https://music.youtube.com")
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string raw = $"{timestamp} {sapisid} {origin}";
        byte[] hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        string sha1Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return $"SAPISIDHASH {timestamp}_{sha1Hex}";
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
