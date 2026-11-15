using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using AlgerMusicPlayer.WinUI.Models;
using Windows.System;

namespace AlgerMusicPlayer.WinUI.Services.Auth;

/// <summary>
/// 纯本地 OAuth 2.0 + PKCE 授权与 RefreshToken 无感续期服务（Spotify / Tidal）
/// </summary>
public sealed class OAuthPkceService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<AccountSession> AuthorizeAsync(PlatformAuthConfig config, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId) || config.ClientId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"请先填写 {config.DisplayName} 的有效 ClientId，或直接使用「内置浏览器登录」免密钥登录。");
        }

        string codeVerifier = GenerateRandomBase64Url(64);
        string codeChallenge = ComputeSha256Base64Url(codeVerifier);
        string expectedState = GenerateRandomBase64Url(24);

        using TcpListener listener = new(IPAddress.Loopback, config.LoopbackPort);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();

        string authUrl = $"{config.AuthorizeEndpoint}" +
                         $"?client_id={Uri.EscapeDataString(config.ClientId.Trim())}" +
                         $"&response_type=code" +
                         $"&redirect_uri={Uri.EscapeDataString(config.RedirectUri)}" +
                         $"&code_challenge_method=S256" +
                         $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                         $"&state={Uri.EscapeDataString(expectedState)}" +
                         $"&scope={Uri.EscapeDataString(config.Scopes)}";

        await Launcher.LaunchUriAsync(new Uri(authUrl));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(180));

        string authCode = await WaitForCallbackCodeAsync(listener, expectedState, timeoutCts.Token);
        return await ExchangeCodeForTokenAsync(config, authCode, codeVerifier, timeoutCts.Token);
    }

    public async Task<AccountSession> RefreshTokenAsync(AccountSession existingSession, PlatformAuthConfig config, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(existingSession.RefreshToken))
        {
            throw new InvalidOperationException("当前会话不包含 RefreshToken，需要重新授权登录。");
        }

        if (string.IsNullOrWhiteSpace(config.ClientId) || config.ClientId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"请先填写 {config.DisplayName} 的 ClientId 以支持 PKCE 续期。");
        }

        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = existingSession.RefreshToken,
            ["client_id"] = config.ClientId.Trim()
        };

        using HttpRequestMessage request = new(HttpMethod.Post, config.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using HttpResponseMessage response = await SharedHttpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"刷新 Token 失败 ({(int)response.StatusCode}): {body}");
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        existingSession.AccessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        if (root.TryGetProperty("refresh_token", out JsonElement newRefresh) &&
            !string.IsNullOrWhiteSpace(newRefresh.GetString()))
        {
            existingSession.RefreshToken = newRefresh.GetString()!;
        }

        int expiresIn = root.TryGetProperty("expires_in", out JsonElement expProp) ? expProp.GetInt32() : 3600;
        existingSession.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        existingSession.UpdatedAt = DateTimeOffset.UtcNow;

        return existingSession;
    }

    private static async Task<string> WaitForCallbackCodeAsync(TcpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            using NetworkStream stream = client.GetStream();

            byte[] buffer = new byte[8192];
            int bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead <= 0)
            {
                continue;
            }

            string requestText = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            string firstLine = requestText.Split("\r\n")[0];
            string[] parts = firstLine.Split(' ');
            if (parts.Length < 2 || !parts[1].StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHtmlResponseAsync(stream, 404, "未找到回调路径");
                continue;
            }

            Uri callbackUri = new("http://127.0.0.1" + parts[1]);
            var query = HttpUtility.ParseQueryString(callbackUri.Query);

            string? error = query["error"];
            if (!string.IsNullOrEmpty(error))
            {
                await WriteHtmlResponseAsync(stream, 400, $"授权已取消或失败: {WebUtility.HtmlEncode(error)}");
                throw new InvalidOperationException($"OAuth 授权失败: {error}");
            }

            string? state = query["state"];
            string? code = query["code"];
            if (state != expectedState || string.IsNullOrEmpty(code))
            {
                await WriteHtmlResponseAsync(stream, 400, "State 校验不匹配或缺少授权码");
                throw new InvalidOperationException("OAuth 回调 State 校验失败。");
            }

            await WriteHtmlResponseAsync(stream, 200, "账号授权已完成，请返回 Alger Music Player 客户端，此页面可关闭。");
            return code;
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task<AccountSession> ExchangeCodeForTokenAsync(PlatformAuthConfig config, string code, string codeVerifier, CancellationToken cancellationToken)
    {
        Dictionary<string, string> form = new()
        {
            ["client_id"] = config.ClientId.Trim(),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = config.RedirectUri,
            ["code_verifier"] = codeVerifier
        };

        using HttpRequestMessage request = new(HttpMethod.Post, config.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using HttpResponseMessage response = await SharedHttpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"换取 Token 失败 ({(int)response.StatusCode}): {body}");
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        string accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        string refreshToken = root.TryGetProperty("refresh_token", out JsonElement refProp)
            ? refProp.GetString() ?? string.Empty
            : string.Empty;
        int expiresIn = root.TryGetProperty("expires_in", out JsonElement expProp) ? expProp.GetInt32() : 3600;

        return new AccountSession
        {
            Platform = config.Platform,
            FlowType = AuthFlowType.OAuth2Pkce,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task WriteHtmlResponseAsync(NetworkStream stream, int statusCode, string message)
    {
        string statusText = statusCode == 200 ? "OK" : "Bad Request";
        string html = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>授权结果</title></head>" +
                      $"<body style=\"font-family:system-ui;background:#18181b;color:#f4f4f5;display:flex;justify-content:center;align-items:center;height:90vh;\">" +
                      $"<h3>{message}</h3></body></html>";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(html);
        string header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                        $"Content-Type: text/html; charset=utf-8\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        $"Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);

        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    private static string GenerateRandomBase64Url(int byteLength)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeSha256Base64Url(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(input));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
