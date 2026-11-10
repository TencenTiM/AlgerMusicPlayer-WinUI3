using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AlgerMusicPlayer.WinUI.Services.Api;

/// <summary>
/// 纯 C# 网易云音乐加密协议实现（支持 WeAPI 与 EAPI）
/// </summary>
public static class NeteaseCrypto
{
    private static readonly byte[] Iv = Encoding.UTF8.GetBytes("0102030405060708");
    private static readonly byte[] PresetKey = Encoding.UTF8.GetBytes("0CoJUm6Qyw8W8jud");
    private static readonly byte[] EapiKey = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
    private const string Base62Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private const string RsaModulusHex =
        "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725" +
        "152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312" +
        "ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424" +
        "d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7";

    private static readonly BigInteger RsaModulus = BigInteger.Parse(RsaModulusHex, global::System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger RsaExponent = new(0x010001);

    /// <summary>
    /// WeAPI 加密（AES-128-CBC 双重加密 + RSA 无填充密钥封装）
    /// </summary>
    public static Dictionary<string, string> EncryptWeApi(string jsonPayload)
    {
        var secretKeyBytes = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            secretKeyBytes[i] = (byte)Base62Chars[RandomNumberGenerator.GetInt32(Base62Chars.Length)];
        }

        // 第一层 AES-128-CBC 加密后转 Base64
        var firstEncrypted = AesEncryptCbc(Encoding.UTF8.GetBytes(jsonPayload), PresetKey, Iv);
        var firstBase64 = Convert.ToBase64String(firstEncrypted);

        // 第二层 AES-128-CBC 加密后转 Base64
        var secondEncrypted = AesEncryptCbc(Encoding.UTF8.GetBytes(firstBase64), secretKeyBytes, Iv);
        var paramsVal = Convert.ToBase64String(secondEncrypted);

        // 反转 secretKey 并进行无填充 RSA 幂模运算
        var reversedKey = Enumerable.Reverse(secretKeyBytes).ToArray();
        var encSecKey = RsaEncryptNoPadding(reversedKey);

        return new Dictionary<string, string>
        {
            ["params"] = paramsVal,
            ["encSecKey"] = encSecKey
        };
    }

    /// <summary>
    /// EAPI 加密（用于客户端接口如 /api/song/enhance/player/url/v1）
    /// </summary>
    public static Dictionary<string, string> EncryptEApi(string apiUrl, string jsonPayload)
    {
        var signMessage = $"nobody{apiUrl}use{jsonPayload}md5forencrypt";
        var md5Bytes = MD5.HashData(Encoding.UTF8.GetBytes(signMessage));
        var digest = Convert.ToHexStringLower(md5Bytes);

        var dataText = $"{apiUrl}-36cd479b6b5-{jsonPayload}-36cd479b6b5-{digest}";
        var encrypted = AesEncryptEcb(Encoding.UTF8.GetBytes(dataText), EapiKey);

        return new Dictionary<string, string>
        {
            ["params"] = Convert.ToHexString(encrypted)
        };
    }

    private static byte[] AesEncryptCbc(byte[] plainBytes, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
    }

    private static byte[] AesEncryptEcb(byte[] plainBytes, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
    }

    private static string RsaEncryptNoPadding(byte[] reversedSecretKey)
    {
        // BigInteger(ReadOnlySpan<byte>, isUnsigned: true, isBigEndian: true)
        var inputInt = new BigInteger(reversedSecretKey, isUnsigned: true, isBigEndian: true);
        var encryptedInt = BigInteger.ModPow(inputInt, RsaExponent, RsaModulus);
        var resultBytes = encryptedInt.ToByteArray(isUnsigned: true, isBigEndian: true);

        // 补齐 128 字节（256 位十六进制字符）
        var hex = Convert.ToHexStringLower(resultBytes);
        return hex.PadLeft(256, '0');
    }
}
