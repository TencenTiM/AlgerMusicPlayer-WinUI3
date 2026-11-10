using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AlgerMusicPlayer.WinUI.Services.Audio;

namespace AlgerMusicPlayer.WinUI.Services.System;

/// <summary>
/// 局域网 Web 远程控制服务（对应原 remoteControl.ts，允许手机浏览器扫码遥控播放器）
/// </summary>
public class RemoteControlService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly AudioPlayerService _playerService;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public string AccessUrl { get; private set; } = string.Empty;

    public RemoteControlService(ConfigService configService, AudioPlayerService playerService)
    {
        _configService = configService;
        _playerService = playerService;

        if (_configService.Settings.EnableRemoteControl)
        {
            Start();
        }
    }

    public bool Start()
    {
        Stop();
        try
        {
            int port = _configService.Settings.RemoteControlPort;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();

            _cts = new CancellationTokenSource();
            IsRunning = true;
            var localIp = GetLocalIpAddress();
            AccessUrl = $"http://{localIp}:{port}/";

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }
        catch
        {
            IsRunning = false;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // 忽略关闭异常
        }
        finally
        {
            _listener = null;
            IsRunning = false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
            catch
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var resp = ctx.Response;
            resp.Headers.Add("Access-Control-Allow-Origin", "*");

            if (path == "/api/state")
            {
                var stateObj = new
                {
                    songName = _playerService.CurrentSong?.Name ?? "未在播放",
                    artist = _playerService.CurrentSong?.ArtistText ?? "",
                    cover = _playerService.CurrentSong?.PicUrl ?? "",
                    isPlaying = _playerService.IsPlaying,
                    lyric = _playerService.CurrentLyricLine?.Text ?? "",
                    volume = (int)Math.Round(_configService.Settings.Volume * 100)
                };
                await WriteStringAsync(resp, JsonSerializer.Serialize(stateObj), "application/json; charset=utf-8");
                return;
            }

            if (path == "/api/toggle")
            {
                _playerService.TogglePlayPause();
                await WriteStringAsync(resp, "{\"ok\":true}", "application/json");
                return;
            }

            if (path == "/api/next")
            {
                await _playerService.NextAsync();
                await WriteStringAsync(resp, "{\"ok\":true}", "application/json");
                return;
            }

            if (path == "/api/prev")
            {
                await _playerService.PreviousAsync();
                await WriteStringAsync(resp, "{\"ok\":true}", "application/json");
                return;
            }

            if (path == "/api/volume")
            {
                var vStr = ctx.Request.QueryString["v"];
                if (int.TryParse(vStr, out var v))
                {
                    _playerService.SetVolume(v / 100f);
                }
                await WriteStringAsync(resp, "{\"ok\":true}", "application/json");
                return;
            }

            // 默认返回响应式手机遥控页面
            await WriteStringAsync(resp, GetMobileRemoteHtml(), "text/html; charset=utf-8");
        }
        catch
        {
            // 忽略单次请求异常
        }
    }

    private static async Task WriteStringAsync(HttpListenerResponse resp, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        resp.ContentType = contentType;
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch
        {
            // 回退到 localhost
        }
        return "127.0.0.1";
    }

    private static string GetMobileRemoteHtml() => """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width,initial-scale=1" />
          <title>Alger Music 远程控制</title>
          <style>
            body { background:#121212; color:#fff; font-family:system-ui,sans-serif; display:flex; flex-direction:column; align-items:center; justify-content:center; min-height:92vh; margin:0; padding:16px; }
            .card { background:#1e1e1e; border-radius:20px; padding:28px; width:100%; max-width:340px; text-align:center; box-shadow:0 12px 32px rgba(0,0,0,.5); }
            .title { font-size:20px; font-weight:700; margin:12px 0 4px; }
            .artist { font-size:14px; color:#9ca3af; margin-bottom:14px; }
            .lyric { font-size:15px; color:#22c55e; min-height:24px; margin-bottom:20px; }
            .btns { display:flex; justify-content:center; gap:18px; margin-bottom:20px; }
            button { background:#22c55e; color:#fff; border:none; border-radius:50px; padding:12px 22px; font-size:15px; font-weight:600; cursor:pointer; }
            input[type=range] { width:100%; accent-color:#22c55e; }
          </style>
        </head>
        <body>
          <div class="card">
            <div class="title" id="song">加载中...</div>
            <div class="artist" id="artist"></div>
            <div class="lyric" id="lyric"></div>
            <div class="btns">
              <button onclick="act('/api/prev')">上一首</button>
              <button onclick="act('/api/toggle')" id="playBtn">播放/暂停</button>
              <button onclick="act('/api/next')">下一首</button>
            </div>
            <div>音量: <input type="range" min="0" max="100" id="vol" oninput="setVol(this.value)" /></div>
          </div>
          <script>
            async function act(u){ await fetch(u); sync(); }
            async function setVol(v){ await fetch('/api/volume?v='+v); }
            async function sync(){
              try {
                const r = await (await fetch('/api/state')).json();
                document.getElementById('song').innerText = r.songName;
                document.getElementById('artist').innerText = r.artist;
                document.getElementById('lyric').innerText = r.lyric;
                document.getElementById('vol').value = r.volume;
              } catch(e){}
            }
            setInterval(sync, 1000); sync();
          </script>
        </body>
        </html>
        """;

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
