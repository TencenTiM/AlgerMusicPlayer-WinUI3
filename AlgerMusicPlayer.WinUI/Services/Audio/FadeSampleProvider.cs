using NAudio.Wave;

namespace AlgerMusicPlayer.WinUI.Services.Audio;

/// <summary>
/// 音频淡入淡出采样处理器（支持多声道原生保持与样本级线性平滑增益渐变）
/// </summary>
public class FadeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _fadeFactor = 1.0f;
    private float _targetFadeFactor = 1.0f;
    private float _fadeStep = 0.0f;
    private readonly object _lock = new();

    public WaveFormat WaveFormat => _source.WaveFormat;

    public FadeSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public void StartFadeIn(double durationMs)
    {
        lock (_lock)
        {
            if (durationMs <= 20)
            {
                _fadeFactor = 1.0f;
                _targetFadeFactor = 1.0f;
                _fadeStep = 0.0f;
                return;
            }

            _fadeFactor = 0.0f;
            _targetFadeFactor = 1.0f;
            double sampleCount = (durationMs / 1000.0) * WaveFormat.SampleRate * WaveFormat.Channels;
            _fadeStep = sampleCount > 0 ? (float)(1.0 / sampleCount) : 1.0f;
        }
    }

    public void StartFadeOut(double durationMs)
    {
        lock (_lock)
        {
            if (durationMs <= 20)
            {
                _fadeFactor = 0.0f;
                _targetFadeFactor = 0.0f;
                _fadeStep = 0.0f;
                return;
            }

            _targetFadeFactor = 0.0f;
            double sampleCount = (durationMs / 1000.0) * WaveFormat.SampleRate * WaveFormat.Channels;
            _fadeStep = sampleCount > 0 ? (float)(-1.0 / sampleCount) : -1.0f;
        }
    }

    public void ResetToFullVolume()
    {
        lock (_lock)
        {
            _fadeFactor = 1.0f;
            _targetFadeFactor = 1.0f;
            _fadeStep = 0.0f;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        lock (_lock)
        {
            if (_fadeStep == 0.0f && _fadeFactor >= 0.999f)
            {
                return read;
            }

            for (int i = 0; i < read; i++)
            {
                buffer[offset + i] *= _fadeFactor;

                if (_fadeStep > 0 && _fadeFactor < _targetFadeFactor)
                {
                    _fadeFactor = Math.Min(_targetFadeFactor, _fadeFactor + _fadeStep);
                    if (_fadeFactor >= _targetFadeFactor) _fadeStep = 0.0f;
                }
                else if (_fadeStep < 0 && _fadeFactor > _targetFadeFactor)
                {
                    _fadeFactor = Math.Max(_targetFadeFactor, _fadeFactor + _fadeStep);
                    if (_fadeFactor <= _targetFadeFactor) _fadeStep = 0.0f;
                }
            }
        }

        return read;
    }
}
