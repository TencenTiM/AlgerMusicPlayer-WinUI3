using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace AlgerMusicPlayer.WinUI.Services.Audio;

/// <summary>
/// 实时音频多频段 FFT 频谱与鼓点脉冲采集器（零帧内 GC，优先使用 WASAPI 环回采集以同时兼容 NAudio 与 MPV 后端，
/// 若系统无可用环回端点则自动降级为音频管线样本推送）
/// </summary>
public sealed class AudioReactiveAnalyzer : IDisposable
{
    private const int FftSize = 1024;
    private const int FftBits = 10;
    private const int RingCapacity = 2048;
    private const int BandCount = 32;

    private readonly object _ringLock = new();
    private readonly object _captureLock = new();

    private readonly float[] _ringBuffer = new float[RingCapacity];
    private int _ringWriteIndex;

    private readonly float[] _fftSnapshot = new float[FftSize];
    private readonly float[] _hannWindow = new float[FftSize];
    private readonly Complex[] _fftComplex = new Complex[FftSize];
    private readonly float[] _smoothedBands = new float[BandCount];
    private readonly float[] _weightedBands = new float[BandCount];
    private readonly int[] _bandStartBin = new int[BandCount];
    private readonly int[] _bandEndBin = new int[BandCount];

    private int _cachedBandTableRate;
    private int _currentSampleRate = 48000;
    private long _lastLoopbackTickMs;
    private long _lastPipelineTickMs;
    private float _agcPeakRef = 0.012f;
    private float _bassAvg;
    private float _beatImpulse;

    private volatile bool _isActive;
    private volatile bool _acceptSamples;
    private volatile bool _disposed;
    private WasapiLoopbackCapture? _loopback;

    /// <summary>
    /// 最近一帧计算得到的综合音频能量（0.0 ~ 1.0）
    /// </summary>
    public float CurrentOverallLevel { get; private set; }

    public AudioReactiveAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _hannWindow[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (FftSize - 1)));
        }
        EnsureBandTable(48000);
    }

    /// <summary>
    /// 按需启停 WASAPI 环回采集；暂停播放仅关闭采样门控避免频繁销毁重连引发 NAudio 并发空引用，
    /// 仅在窗口隐藏或关闭音频律动时才安全异步停机释放
    /// </summary>
    public void SetMonitoringActive(bool active, bool isPlaying = true)
    {
        if (_disposed)
        {
            active = false;
            isPlaying = false;
        }

        _acceptSamples = active && isPlaying;
        WasapiLoopbackCapture? toStop = null;
        lock (_captureLock)
        {
            _isActive = active;
            if (active)
            {
                if (_loopback == null)
                {
                    StartLoopbackLocked();
                }
            }
            else if (_loopback != null)
            {
                toStop = _loopback;
                _loopback = null;
                toStop.DataAvailable -= OnLoopbackDataAvailable;
                toStop.RecordingStopped -= OnLoopbackRecordingStopped;
            }
        }

        if (toStop != null)
        {
            // 在确认 CaptureState 已完全进入 Stopped 后再 Dispose，规避 NAudio 录音线程置空 captureThread 导致的竞态空引用
            _ = StopAndDisposeCaptureAsync(toStop);
        }
    }

    private void StartLoopbackLocked()
    {
        WasapiLoopbackCapture? cap = null;
        try
        {
            cap = new WasapiLoopbackCapture();
            cap.DataAvailable += OnLoopbackDataAvailable;
            cap.RecordingStopped += OnLoopbackRecordingStopped;
            cap.StartRecording();
            _loopback = cap;
        }
        catch
        {
            // 系统无活跃播放端点或驱动不支持环回时，静默回退到管线样本源
            if (cap != null)
            {
                try
                {
                    cap.DataAvailable -= OnLoopbackDataAvailable;
                    cap.RecordingStopped -= OnLoopbackRecordingStopped;
                    cap.Dispose();
                }
                catch
                {
                }
            }
            _loopback = null;
        }
    }

    private void OnLoopbackRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture cap) return;
        bool shouldDispose = false;
        lock (_captureLock)
        {
            if (ReferenceEquals(_loopback, cap))
            {
                _loopback = null;
                cap.DataAvailable -= OnLoopbackDataAvailable;
                cap.RecordingStopped -= OnLoopbackRecordingStopped;
                shouldDispose = true;
            }
        }
        if (shouldDispose)
        {
            _ = StopAndDisposeCaptureAsync(cap);
        }
    }

    private static async Task StopAndDisposeCaptureAsync(WasapiLoopbackCapture cap)
    {
        try
        {
            cap.StopRecording();
        }
        catch
        {
        }

        var sw = Stopwatch.StartNew();
        while (cap.CaptureState != CaptureState.Stopped && sw.ElapsedMilliseconds < 1500)
        {
            await Task.Delay(15).ConfigureAwait(false);
        }

        if (cap.CaptureState == CaptureState.Stopped)
        {
            try
            {
                cap.Dispose();
            }
            catch
            {
            }
        }
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isActive || !_acceptSamples || _disposed || e.BytesRecorded <= 0 || sender is not WasapiLoopbackCapture cap)
            return;

        // 当 NAudio 音频管线正在活跃推流时优先使用无衰减管线样本，仅在 MPV 后端或管线静默时由 WASAPI 环回接管
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastPipelineTickMs) < 180)
            return;

        var fmt = cap.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        int sr = fmt.SampleRate > 0 ? fmt.SampleRate : 48000;
        var byteSpan = e.Buffer.AsSpan(0, e.BytesRecorded);

        bool isFloat32 = fmt.BitsPerSample == 32 &&
                         (fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                          fmt.Encoding == WaveFormatEncoding.Extensible);

        lock (_ringLock)
        {
            _currentSampleRate = sr;

            if (isFloat32)
            {
                var samples = MemoryMarshal.Cast<byte, float>(byteSpan);
                int frames = samples.Length / channels;
                if (channels == 1)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        WriteMonoSampleLocked(samples[f]);
                    }
                }
                else
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int b = f * channels;
                        WriteMonoSampleLocked((samples[b] + samples[b + 1]) * 0.5f);
                    }
                }
            }
            else if (fmt.BitsPerSample == 16)
            {
                var samples = MemoryMarshal.Cast<byte, short>(byteSpan);
                int frames = samples.Length / channels;
                const float inv16 = 1.0f / 32768f;
                if (channels == 1)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        WriteMonoSampleLocked(samples[f] * inv16);
                    }
                }
                else
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int b = f * channels;
                        WriteMonoSampleLocked((samples[b] + samples[b + 1]) * (0.5f * inv16));
                    }
                }
            }
        }

        Volatile.Write(ref _lastLoopbackTickMs, now);
    }

    /// <summary>
    /// 来自 NAudio 内部管线的样本推送（优先供频谱采样，不受系统输出音量衰减影响）
    /// </summary>
    public void PushPipelineSamples(float[] buffer, int offset, int samplesRead, int channels, int sampleRate = 48000)
    {
        if (!_isActive || !_acceptSamples || _disposed || samplesRead <= 0)
            return;

        int ch = Math.Max(1, channels);
        int frames = samplesRead / ch;
        if (frames <= 0)
            return;

        lock (_ringLock)
        {
            _currentSampleRate = sampleRate > 0 ? sampleRate : 48000;
            if (ch == 1)
            {
                for (int f = 0; f < frames; f++)
                {
                    WriteMonoSampleLocked(buffer[offset + f]);
                }
            }
            else
            {
                for (int f = 0; f < frames; f++)
                {
                    int b = offset + f * ch;
                    WriteMonoSampleLocked((buffer[b] + buffer[b + 1]) * 0.5f);
                }
            }
        }

        Volatile.Write(ref _lastPipelineTickMs, Environment.TickCount64);
    }

    private void WriteMonoSampleLocked(float sample)
    {
        _ringBuffer[_ringWriteIndex] = sample;
        _ringWriteIndex = (_ringWriteIndex + 1) & (RingCapacity - 1);
    }

    private void EnsureBandTable(int sampleRate)
    {
        if (_cachedBandTableRate == sampleRate) return;
        _cachedBandTableRate = sampleRate;

        const double minFreq = 42.0;
        double maxFreq = Math.Min(13000.0, sampleRate * 0.45);
        double ratio = maxFreq / minFreq;
        double binHz = sampleRate / (double)FftSize;

        for (int i = 0; i < BandCount; i++)
        {
            double f0 = minFreq * Math.Pow(ratio, i / (double)BandCount);
            double f1 = minFreq * Math.Pow(ratio, (i + 1) / (double)BandCount);
            int b0 = Math.Clamp((int)Math.Floor(f0 / binHz), 1, FftSize / 2 - 1);
            int b1 = Math.Clamp((int)Math.Ceiling(f1 / binHz), b0, FftSize / 2 - 1);
            _bandStartBin[i] = b0;
            _bandEndBin[i] = b1;
        }
    }

    /// <summary>
    /// 在 UI VSync 帧回调中调用：零堆分配计算 32 段平滑频谱、低/中/高频能量与鼓点瞬态脉冲
    /// </summary>
    public void ComputeFrame(
        double dt,
        bool isPlaying,
        double sensitivity,
        Span<float> outBands32,
        out float bass,
        out float mid,
        out float treble,
        out float overall,
        out float beatImpulse)
    {
        float fdt = (float)Math.Clamp(dt, 0.001, 0.10);
        long now = Environment.TickCount64;
        long latestSampleTick = Math.Max(
            Volatile.Read(ref _lastLoopbackTickMs),
            Volatile.Read(ref _lastPipelineTickMs));

        if (!isPlaying || now - latestSampleTick > 250)
        {
            float decay = MathF.Exp(-8.0f * fdt);
            for (int i = 0; i < BandCount; i++)
            {
                _smoothedBands[i] *= decay;
                if (_smoothedBands[i] < 0.002f) _smoothedBands[i] = 0f;
                if (i < outBands32.Length) outBands32[i] = _smoothedBands[i];
            }
            _beatImpulse *= MathF.Exp(-9.5f * fdt);
            if (_beatImpulse < 0.002f) _beatImpulse = 0f;
            _bassAvg *= decay;
            _agcPeakRef = MathF.Max(0.008f, _agcPeakRef * MathF.Exp(-1.5f * fdt));

            bass = 0f;
            mid = 0f;
            treble = 0f;
            overall = 0f;
            CurrentOverallLevel = 0f;
            beatImpulse = _beatImpulse;
            return;
        }

        int sampleRate;
        lock (_ringLock)
        {
            sampleRate = _currentSampleRate;
            int readPos = (_ringWriteIndex - FftSize + RingCapacity) & (RingCapacity - 1);
            for (int i = 0; i < FftSize; i++)
            {
                _fftSnapshot[i] = _ringBuffer[(readPos + i) & (RingCapacity - 1)];
            }
        }

        EnsureBandTable(sampleRate);

        for (int i = 0; i < FftSize; i++)
        {
            _fftComplex[i].X = _fftSnapshot[i] * _hannWindow[i];
            _fftComplex[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftBits, _fftComplex);

        // 第一遍：峰值加权聚合 + 粉红噪声高频倾斜补偿，同时追踪当前帧动态峰值
        float framePeak = 0f;
        for (int i = 0; i < BandCount; i++)
        {
            int b0 = _bandStartBin[i];
            int b1 = _bandEndBin[i];
            float sum = 0f;
            float peak = 0f;
            int count = b1 - b0 + 1;

            for (int k = b0; k <= b1; k++)
            {
                float rx = _fftComplex[k].X;
                float ry = _fftComplex[k].Y;
                float mag = MathF.Sqrt(rx * rx + ry * ry);
                sum += mag;
                if (mag > peak) peak = mag;
            }

            float avg = sum / Math.Max(1, count);
            float rawMag = 0.35f * avg + 0.65f * peak;
            if (rawMag < 0.00005f) rawMag = 0f;

            float tilt = 1.0f + MathF.Pow(i / 31.0f, 1.15f) * 3.4f;
            float weighted = rawMag * tilt;
            _weightedBands[i] = weighted;
            if (weighted > framePeak) framePeak = weighted;
        }

        // 自适应峰值参考包络（快攻慢放 AGC，兼容低音量与高动态曲目同时保留节拍起伏）
        float targetRef = MathF.Max(0.0032f, framePeak * 1.18f);
        float agcAlpha = targetRef > _agcPeakRef
            ? (1.0f - MathF.Exp(-10.0f * fdt))
            : (1.0f - MathF.Exp(-0.65f * fdt));
        _agcPeakRef = MathF.Max(0.0032f, _agcPeakRef + (targetRef - _agcPeakRef) * agcAlpha);

        float sens = (float)Math.Clamp(sensitivity, 0.3, 2.5);
        float atk = 1.0f - MathF.Exp(-28.0f * fdt);
        float rel = 1.0f - MathF.Exp(-9.0f * fdt);

        // 第二遍：3-Tap 邻域微平滑 + 对数视觉映射
        for (int i = 0; i < BandCount; i++)
        {
            float center = _weightedBands[i];
            float left = i > 0 ? _weightedBands[i - 1] : center;
            float right = i < BandCount - 1 ? _weightedBands[i + 1] : center;
            float wVal = center * 0.68f + (left + right) * 0.16f;

            float ratio = (wVal / _agcPeakRef) * sens;
            float logNorm = Math.Clamp(MathF.Log10(1.0f + ratio * 4.5f) * 1.3507f, 0f, 1f);
            float norm = MathF.Pow(logNorm, 1.08f);

            float prev = _smoothedBands[i];
            float alpha = norm > prev ? atk : rel;
            float next = prev + (norm - prev) * alpha;
            _smoothedBands[i] = next;

            if (i < outBands32.Length)
            {
                outBands32[i] = next;
            }
        }

        float bassSum = 0f;
        for (int i = 0; i <= 4; i++) bassSum += _smoothedBands[i];
        bass = bassSum / 5.0f;

        float midSum = 0f;
        for (int i = 5; i <= 17; i++) midSum += _smoothedBands[i];
        mid = midSum / 13.0f;

        float trebleSum = 0f;
        for (int i = 18; i <= 31; i++) trebleSum += _smoothedBands[i];
        treble = trebleSum / 14.0f;

        overall = Math.Clamp(bass * 0.45f + mid * 0.38f + treble * 0.17f, 0f, 1f);
        CurrentOverallLevel = overall;

        float avgAlpha = 1.0f - MathF.Exp(-4.0f * fdt);
        _bassAvg += (bass - _bassAvg) * avgAlpha;
        if (bass > _bassAvg * 1.28f + 0.05f)
        {
            float instant = Math.Clamp((bass - _bassAvg) * 3.4f, 0f, 1f);
            _beatImpulse = MathF.Max(_beatImpulse, instant);
        }
        else
        {
            _beatImpulse *= MathF.Exp(-9.5f * fdt);
            if (_beatImpulse < 0.002f) _beatImpulse = 0f;
        }
        beatImpulse = _beatImpulse;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetMonitoringActive(false);
    }
}
