using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AlgerMusicPlayer.WinUI.Models;

namespace AlgerMusicPlayer.WinUI.Services.Audio;

/// <summary>
/// mpv 音频参数快照（1:1 对应 src/shared/mpvPlayback.ts MpvAudioParams）
/// </summary>
public sealed class MpvAudioParams
{
    public string? Channels { get; set; }
    public int? ChannelCount { get; set; }
    public int? SampleRate { get; set; }
    public string? Format { get; set; }
}

/// <summary>
/// mpv 播放请求模型（1:1 对应 src/shared/mpvPlayback.ts MpvLoadRequest）
/// </summary>
public sealed class MpvLoadRequest
{
    public string FilePath { get; set; } = string.Empty;
    public bool ShouldPlay { get; set; } = true;
    public double SeekTimeSeconds { get; set; }
    public float Volume { get; set; } = 1.0f;
    public float PlaybackRate { get; set; } = 1.0f;
    public bool EqEnabled { get; set; }
    public float[] EqBandGains { get; set; } = new float[10];
}

/// <summary>
/// 内置 mpv 原生播放管理器（1:1 还原 .worktrees/local-music-folder-tree/src/main/modules/mpvPlayback.ts）
/// 专用于本地文件与 SMB 网络共享音频的无下混（--ad-lavc-downmix=no --audio-channels=5.1,stereo）WASAPI 原生输出
/// </summary>
public sealed class MpvPlaybackService : IDisposable
{
    private const int CommandTimeoutMs = 5000;
    private const int LoadTimeoutMs = 15000;
    private const int PipeConnectTimeoutMs = 5000;

    private static readonly int[] MpvEqFrequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private Process? _child;
    private NamedPipeClientStream? _pipeStream;
    private Task? _startTask;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextRequestId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pendingCommands = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<(int Token, TaskCompletionSource<bool> Tcs)>> _eventWaiters = new();

    private volatile bool _shuttingDown;
    private volatile bool _suppressNextExit;
    private int _recoveryAttempts;
    private MpvLoadRequest? _lastLoadRequest;
    private int _loadSequence;
    private int _channelWarningGeneration = -1;

    private int _generation;
    private double _currentTimeSeconds;
    private double _durationSeconds;
    private bool _paused = true;
    private bool _loading;
    private bool _idle = true;
    private MpvAudioParams? _inputAudio;
    private MpvAudioParams? _outputAudio;

    public double CurrentTimeSeconds => _currentTimeSeconds;
    public double DurationSeconds => _durationSeconds;
    public bool IsPaused => _paused;
    public bool IsLoading => _loading;
    public bool IsIdle => _idle;
    public MpvAudioParams? InputAudio => _inputAudio;
    public MpvAudioParams? OutputAudio => _outputAudio;

    public event Action? StateChanged;
    public event Action? Loaded;
    public event Action? Ended;
    public event Action<string>? WarningReceived;
    public event Action<string>? ErrorReceived;
    public event Action<MpvAudioParams?>? InputAudioParamsChanged;

    public MpvPlaybackService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    /// <summary>
    /// 判断当前平台与内置 mpv.exe 是否可用（对应 getCapabilities）
    /// </summary>
    public bool IsAvailable(out string? reason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            reason = "mpv 本地播放仅在 Windows x64 启用";
            return false;
        }

        var exePath = GetExecutablePath();
        if (!File.Exists(exePath))
        {
            reason = $"未找到 mpv: {exePath}";
            return false;
        }

        reason = null;
        return true;
    }

    public static string GetExecutablePath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate1 = Path.Combine(baseDir, "mpv", "win-x64", "mpv.exe");
        if (File.Exists(candidate1)) return candidate1;

        var candidate2 = Path.Combine(baseDir, "Assets", "mpv", "win-x64", "mpv.exe");
        if (File.Exists(candidate2)) return candidate2;

        return candidate1;
    }

    public async Task LoadAsync(MpvLoadRequest request, CancellationToken ct = default)
    {
        if (!IsAvailable(out var reason))
        {
            throw new InvalidOperationException(reason ?? "当前环境不支持 mpv 本地播放");
        }

        var normalized = new MpvLoadRequest
        {
            FilePath = Path.GetFullPath(request.FilePath),
            ShouldPlay = request.ShouldPlay,
            SeekTimeSeconds = Math.Max(0, request.SeekTimeSeconds),
            Volume = Math.Clamp(request.Volume, 0f, 1f),
            PlaybackRate = Math.Clamp(request.PlaybackRate, 0.25f, 4.0f),
            EqEnabled = request.EqEnabled,
            EqBandGains = (float[])request.EqBandGains.Clone()
        };

        _lastLoadRequest = normalized;
        _recoveryAttempts = 0;

        try
        {
            await LoadInternalAsync(normalized, ct);
        }
        catch
        {
            if (ReferenceEquals(_lastLoadRequest, normalized))
            {
                _lastLoadRequest = null;
            }
            throw;
        }
    }

    public Task PauseAsync() => SetPropertyAsync("pause", true);

    public Task ResumeAsync() => SetPropertyAsync("pause", false);

    public async Task StopAsync()
    {
        _lastLoadRequest = null;
        Interlocked.Increment(ref _loadSequence);
        CancelEventWaiters("file-loaded", new OperationCanceledException("播放已停止"));
        if (_pipeStream == null || !_pipeStream.IsConnected)
        {
            ResetState();
            return;
        }

        try
        {
            await SendCommandAsync(["stop"]);
        }
        catch
        {
            // 忽略停止命令异常
        }
        ResetState();
    }

    public async Task SeekAsync(double timeSeconds)
    {
        double target = Math.Max(0, double.IsFinite(timeSeconds) ? timeSeconds : 0);
        await SendCommandAsync(["seek", target, "absolute+exact"]);
        _currentTimeSeconds = target;
        StateChanged?.Invoke();
    }

    public async Task SetVolumeAsync(float volume)
    {
        float normalizedVolume = Math.Clamp(volume, 0f, 1f);
        if (_lastLoadRequest != null)
        {
            _lastLoadRequest.Volume = normalizedVolume;
        }
        await SetPropertyAsync("volume", Math.Round(normalizedVolume * 100.0, 2));
    }

    public async Task SetPlaybackRateAsync(float rate)
    {
        float normalizedRate = Math.Clamp(rate, 0.25f, 4.0f);
        if (_lastLoadRequest != null)
        {
            _lastLoadRequest.PlaybackRate = normalizedRate;
        }
        await SetPropertyAsync("speed", Math.Round(normalizedRate, 2));
    }

    public async Task SetEqAsync(bool enabled, float[] bandGains)
    {
        if (_lastLoadRequest != null)
        {
            _lastLoadRequest.EqEnabled = enabled;
            _lastLoadRequest.EqBandGains = (float[])bandGains.Clone();
        }
        await ApplyEqAsync(enabled, bandGains);
    }

    private async Task LoadInternalAsync(MpvLoadRequest request, CancellationToken ct)
    {
        int sequence = Interlocked.Increment(ref _loadSequence);
        CancelEventWaiters("file-loaded", new OperationCanceledException("播放请求已被新的歌曲替代"));
        int generation = Interlocked.Increment(ref _generation);

        _currentTimeSeconds = 0;
        _durationSeconds = 0;
        _paused = true;
        _loading = true;
        _idle = false;
        _inputAudio = null;
        _outputAudio = null;
        _channelWarningGeneration = -1;
        StateChanged?.Invoke();

        await EnsureStartedAsync(ct);
        await SetPropertyAsync("volume", Math.Round(request.Volume * 100.0, 2));
        await SetPropertyAsync("speed", Math.Round(request.PlaybackRate, 2));
        await ApplyEqAsync(request.EqEnabled, request.EqBandGains);

        var fileLoadedTask = WaitForEventAsync("file-loaded", LoadTimeoutMs, sequence, ct);
        try
        {
            var loadCmdTask = SendCommandAsync(["loadfile", request.FilePath, "replace"]);
            await Task.WhenAll(loadCmdTask, fileLoadedTask);
        }
        catch (Exception ex)
        {
            CancelEventWaiters("file-loaded", ex, sequence);
            throw;
        }

        if (sequence != _loadSequence || ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("播放请求已被新的歌曲替代");
        }

        if (request.SeekTimeSeconds > 0)
        {
            await SendCommandAsync(["seek", request.SeekTimeSeconds, "absolute+exact"]);
            _currentTimeSeconds = request.SeekTimeSeconds;
        }

        await SetPropertyAsync("pause", !request.ShouldPlay);

        _loading = false;
        _idle = false;
        _paused = !request.ShouldPlay;
        Loaded?.Invoke();
        StateChanged?.Invoke();
    }

    private async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        if (_pipeStream is { IsConnected: true }) return;

        await _startLock.WaitAsync(ct);
        try
        {
            if (_pipeStream is { IsConnected: true }) return;
            if (_startTask != null)
            {
                await _startTask;
                return;
            }

            _startTask = StartProcessAsync();
            try
            {
                await _startTask;
            }
            finally
            {
                _startTask = null;
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task StartProcessAsync()
    {
        var executablePath = GetExecutablePath();
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"未找到内置 mpv: {executablePath}");
        }

        string pipeName = $"alger-mpv-{Environment.ProcessId}-{Guid.NewGuid()}";
        string pipePath = $@"\\.\pipe\{pipeName}";

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        // 1:1 严格还原 src/main/modules/mpvPlayback.ts 的启动参数（禁止混音下混，原样输出 5.1 或 stereo）
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("--idle=yes");
        psi.ArgumentList.Add("--force-window=no");
        psi.ArgumentList.Add("--video=no");
        psi.ArgumentList.Add("--terminal=no");
        psi.ArgumentList.Add("--ao=wasapi");
        psi.ArgumentList.Add("--audio-device=auto");
        psi.ArgumentList.Add("--audio-exclusive=no");
        psi.ArgumentList.Add("--audio-channels=5.1,stereo");
        psi.ArgumentList.Add("--ad-lavc-downmix=no");
        psi.ArgumentList.Add("--audio-fallback-to-null=no");
        psi.ArgumentList.Add($"--input-ipc-server={pipePath}");

        _shuttingDown = false;
        var child = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        child.Exited += (_, _) => HandleProcessExit(child);
        if (!child.Start())
        {
            throw new InvalidOperationException("无法启动内置 mpv 进程");
        }
        _child = child;

        try
        {
            _pipeStream = await ConnectPipeAsync(pipeName, child);
            _ = Task.Run(() => ReadPipeLoopAsync(_pipeStream));

            await Task.WhenAll(
                ObservePropertyAsync(1, "time-pos"),
                ObservePropertyAsync(2, "duration"),
                ObservePropertyAsync(3, "pause"),
                ObservePropertyAsync(4, "idle-active"),
                ObservePropertyAsync(5, "audio-params"),
                ObservePropertyAsync(6, "audio-out-params")
            );
            await SendCommandAsync(["request_log_messages", "warn"]);
        }
        catch
        {
            _suppressNextExit = true;
            try
            {
                if (!child.HasExited) child.Kill();
            }
            catch
            {
                // 忽略清理异常
            }
            _pipeStream?.Dispose();
            _pipeStream = null;
            throw;
        }
    }

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(string pipeName, Process child)
    {
        var sw = Stopwatch.StartNew();
        Exception? lastError = null;

        while (sw.ElapsedMilliseconds < PipeConnectTimeoutMs)
        {
            if (child.HasExited)
            {
                throw new InvalidOperationException("mpv 在 IPC 管道建立前已退出");
            }

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150);
                return pipe;
            }
            catch (Exception ex)
            {
                lastError = ex;
                pipe.Dispose();
                await Task.Delay(50);
            }
        }

        throw new TimeoutException($"连接 mpv IPC 超时: {lastError?.Message}");
    }

    private async Task ReadPipeLoopAsync(NamedPipeClientStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            while (!_shuttingDown && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    HandleMessage(doc.RootElement);
                }
                catch
                {
                    // 忽略单行解析异常
                }
            }
        }
        catch
        {
            // 管道断开或进程退出
        }
    }

    private void HandleMessage(JsonElement root)
    {
        if (root.TryGetProperty("request_id", out var reqIdEl) && reqIdEl.ValueKind == JsonValueKind.Number &&
            reqIdEl.TryGetInt32(out int reqId))
        {
            if (_pendingCommands.TryRemove(reqId, out var tcs))
            {
                string? error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                if (!string.IsNullOrEmpty(error) && error != "success")
                {
                    tcs.TrySetException(new InvalidOperationException($"mpv 命令失败: {error}"));
                }
                else
                {
                    JsonElement? clonedData = root.TryGetProperty("data", out var dataEl) ? dataEl.Clone() : null;
                    tcs.TrySetResult(clonedData);
                }
            }
        }

        string? ev = root.TryGetProperty("event", out var evEl) ? evEl.GetString() : null;
        if (string.IsNullOrEmpty(ev)) return;

        if (ev == "property-change" && root.TryGetProperty("name", out var nameEl))
        {
            var propName = nameEl.GetString();
            if (!string.IsNullOrEmpty(propName))
            {
                root.TryGetProperty("data", out var dataEl);
                HandlePropertyChange(propName, dataEl);
            }
        }
        else if (ev == "end-file")
        {
            string? reason = root.TryGetProperty("reason", out var rEl) ? rEl.GetString() : null;
            if (string.IsNullOrEmpty(reason) &&
                root.TryGetProperty("data", out var dEl) &&
                dEl.ValueKind == JsonValueKind.Object &&
                dEl.TryGetProperty("reason", out var drEl))
            {
                reason = drEl.GetString();
            }

            if (reason == "eof")
            {
                _paused = true;
                StateChanged?.Invoke();
                Ended?.Invoke();
            }
            else if (reason == "error")
            {
                ErrorReceived?.Invoke("mpv 无法继续播放当前文件");
            }
        }

        if (_eventWaiters.TryRemove(ev, out var waiters))
        {
            foreach (var w in waiters)
            {
                w.Tcs.TrySetResult(true);
            }
        }
    }

    private void HandlePropertyChange(string name, JsonElement data)
    {
        switch (name)
        {
            case "time-pos":
                if (data.ValueKind == JsonValueKind.Number && data.TryGetDouble(out var t))
                {
                    _currentTimeSeconds = t;
                }
                break;
            case "duration":
                if (data.ValueKind == JsonValueKind.Number && data.TryGetDouble(out var d))
                {
                    _durationSeconds = d;
                }
                break;
            case "pause":
                if (data.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    _paused = data.GetBoolean();
                }
                break;
            case "idle-active":
                if (data.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    _idle = data.GetBoolean();
                }
                break;
            case "audio-params":
                int? prevChannels = _inputAudio?.ChannelCount;
                _inputAudio = ParseAudioParams(data);
                InputAudioParamsChanged?.Invoke(_inputAudio);
                CheckChannelLayout();
                // 声道数变化后按实际声道数重建多声道增益滤镜
                if (_inputAudio?.ChannelCount != prevChannels && BuildChannelGainFilter(_channelGains) != null)
                {
                    _ = ApplyEqAsync(_lastLoadRequest?.EqEnabled ?? false, _lastLoadRequest?.EqBandGains ?? []);
                }
                break;
            case "audio-out-params":
                _outputAudio = ParseAudioParams(data);
                CheckChannelLayout();
                break;
        }
        StateChanged?.Invoke();
    }

    private void CheckChannelLayout()
    {
        if (_channelWarningGeneration == _generation) return;
        int? inputChannels = _inputAudio?.ChannelCount;
        int? outputChannels = _outputAudio?.ChannelCount;
        if (inputChannels == 6 && outputChannels is > 0 && outputChannels != 6)
        {
            _channelWarningGeneration = _generation;
            WarningReceived?.Invoke($"检测到 6 声道音源，但 Windows 默认输出当前为 {outputChannels} 声道");
        }
    }

    private static MpvAudioParams? ParseAudioParams(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        return new MpvAudioParams
        {
            Channels = data.TryGetProperty("channels", out var chEl) && chEl.ValueKind == JsonValueKind.String
                ? chEl.GetString()
                : null,
            ChannelCount = data.TryGetProperty("channel-count", out var ccEl) && ccEl.ValueKind == JsonValueKind.Number && ccEl.TryGetInt32(out int cc)
                ? cc
                : null,
            SampleRate = data.TryGetProperty("samplerate", out var srEl) && srEl.ValueKind == JsonValueKind.Number && srEl.TryGetInt32(out int sr)
                ? sr
                : null,
            Format = data.TryGetProperty("format", out var fmtEl) && fmtEl.ValueKind == JsonValueKind.String
                ? fmtEl.GetString()
                : null
        };
    }

    private void HandleProcessExit(Process exitedChild)
    {
        if (!ReferenceEquals(_child, exitedChild)) return;
        _child = null;
        try
        {
            _pipeStream?.Dispose();
        }
        catch
        {
            // 忽略
        }
        _pipeStream = null;

        RejectAllPending(new InvalidOperationException("mpv 进程已退出"));
        CancelEventWaiters("file-loaded", new InvalidOperationException("mpv 进程已退出"));

        if (_suppressNextExit)
        {
            _suppressNextExit = false;
            return;
        }

        if (_shuttingDown || _lastLoadRequest == null) return;

        if (_recoveryAttempts >= 1)
        {
            ErrorReceived?.Invoke("mpv 连续异常退出，本地播放已停止");
            _lastLoadRequest = null;
            ResetState();
            return;
        }

        _recoveryAttempts++;
        var resumeRequest = new MpvLoadRequest
        {
            FilePath = _lastLoadRequest.FilePath,
            SeekTimeSeconds = _currentTimeSeconds,
            ShouldPlay = !_paused,
            Volume = _lastLoadRequest.Volume,
            PlaybackRate = _lastLoadRequest.PlaybackRate,
            EqEnabled = _lastLoadRequest.EqEnabled,
            EqBandGains = (float[])_lastLoadRequest.EqBandGains.Clone()
        };
        _lastLoadRequest = resumeRequest;
        WarningReceived?.Invoke("mpv 异常退出，正在自动恢复一次");

        _ = Task.Run(async () =>
        {
            try
            {
                await LoadInternalAsync(resumeRequest, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke($"mpv 自动恢复失败: {ex.Message}");
                _lastLoadRequest = null;
                ResetState();
            }
        });
    }

    private Task ObservePropertyAsync(int id, string name) =>
        SendCommandAsync(["observe_property", id, name]);

    private Task SetPropertyAsync(string name, object value) =>
        SendCommandAsync(["set_property", name, value]);

    private Task ApplyEqAsync(bool enabled, float[] bandGains)
    {
        var parts = new List<string>(2);
        if (enabled) parts.Add(BuildEqFilter(bandGains));
        string? channelFilter = BuildChannelGainFilter(_channelGains);
        if (channelFilter != null) parts.Add(channelFilter);
        return SendCommandAsync(["af", "set", string.Join(',', parts)]);
    }

    // 多声道各音箱独立线性增益 (索引顺序：FL FR C LFE SL SR BL BR)
    private float[] _channelGains = [1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f];

    /// <summary>
    /// 实时更新 mpv 后端的多声道各音箱增益与静音
    /// </summary>
    public async Task SetChannelGainsAsync(float[] gains)
    {
        _channelGains = (float[])gains.Clone();
        try
        {
            await ApplyEqAsync(_lastLoadRequest?.EqEnabled ?? false, _lastLoadRequest?.EqBandGains ?? []);
        }
        catch
        {
            // mpv 尚未启动时忽略，加载时会自动应用
        }
    }

    private string? BuildChannelGainFilter(float[] gains)
    {
        if (gains.All(g => Math.Abs(g - 1f) < 0.0001f)) return null;
        // 表达式数量与实际输入声道数一致，aeval 按声道逐路乘以增益，c=same 保持原声道布局
        int channels = Math.Clamp(_inputAudio?.ChannelCount ?? 2, 1, gains.Length);
        var exprs = gains.Take(channels).Select((g, i) => string.Create(CultureInfo.InvariantCulture, $"val({i})*{g:0.#####}"));
        return $"lavfi=[aeval=exprs='{string.Join('|', exprs)}':c=same]";
    }

    private static string BuildEqFilter(float[] bandGains)
    {
        var filters = new List<string>(MpvEqFrequencies.Length + 1);
        for (int i = 0; i < MpvEqFrequencies.Length; i++)
        {
            int freq = MpvEqFrequencies[i];
            float gain = i < bandGains.Length ? Math.Clamp(bandGains[i], -12f, 12f) : 0f;
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"equalizer=f={freq}:t=q:w=1:g={gain:0.##}"));
        }
        filters.Add("alimiter=limit=0.98:level=false");
        return $"lavfi=[{string.Join(',', filters)}]";
    }

    private async Task<JsonElement?> SendCommandAsync(object[] command)
    {
        await EnsureStartedAsync();
        var pipe = _pipeStream;
        if (pipe == null || !pipe.IsConnected)
        {
            throw new InvalidOperationException("mpv IPC 尚未连接");
        }

        int requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[requestId] = tcs;

        var payload = JsonSerializer.Serialize(new
        {
            command,
            request_id = requestId
        }) + "\n";
        var bytes = Encoding.UTF8.GetBytes(payload);

        try
        {
            await _writeLock.WaitAsync();
            try
            {
                await pipe.WriteAsync(bytes);
                await pipe.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            using var cts = new CancellationTokenSource(CommandTimeoutMs);
            await using var reg = cts.Token.Register(() =>
            {
                if (_pendingCommands.TryRemove(requestId, out var pending))
                {
                    pending.TrySetException(new TimeoutException($"mpv 命令超时: {command[0]}"));
                }
            });

            return await tcs.Task;
        }
        catch
        {
            _pendingCommands.TryRemove(requestId, out _);
            throw;
        }
    }

    private async Task WaitForEventAsync(string eventName, int timeoutMs, int token, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bag = _eventWaiters.GetOrAdd(eventName, _ => []);
        bag.Add((token, tcs));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        await using var reg = timeoutCts.Token.Register(() =>
        {
            tcs.TrySetException(ct.IsCancellationRequested
                ? new OperationCanceledException(ct)
                : new TimeoutException($"等待 mpv 事件超时: {eventName}"));
        });

        await tcs.Task;
    }

    private void CancelEventWaiters(string eventName, Exception error, int? token = null)
    {
        if (!_eventWaiters.TryGetValue(eventName, out var bag)) return;
        foreach (var item in bag)
        {
            if (token.HasValue && item.Token != token.Value) continue;
            item.Tcs.TrySetException(error);
        }
    }

    private void RejectAllPending(Exception error)
    {
        foreach (var kv in _pendingCommands)
        {
            if (_pendingCommands.TryRemove(kv.Key, out var tcs))
            {
                tcs.TrySetException(error);
            }
        }
    }

    private void ResetState()
    {
        _currentTimeSeconds = 0;
        _durationSeconds = 0;
        _paused = true;
        _loading = false;
        _idle = true;
        StateChanged?.Invoke();
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _lastLoadRequest = null;
        Interlocked.Increment(ref _loadSequence);
        RejectAllPending(new ObjectDisposedException(nameof(MpvPlaybackService)));
        CancelEventWaiters("file-loaded", new ObjectDisposedException(nameof(MpvPlaybackService)));

        try
        {
            _pipeStream?.Dispose();
        }
        catch
        {
            // 忽略
        }
        _pipeStream = null;

        var child = _child;
        _child = null;
        if (child != null)
        {
            try
            {
                if (!child.HasExited)
                {
                    _suppressNextExit = true;
                    child.Kill();
                }
            }
            catch
            {
                // 忽略
            }
            child.Dispose();
        }
    }

    public void Dispose()
    {
        Shutdown();
        _startLock.Dispose();
        _writeLock.Dispose();
    }
}
