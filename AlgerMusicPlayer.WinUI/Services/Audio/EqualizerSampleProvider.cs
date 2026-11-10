using System.Reflection;
using AlgerMusicPlayer.WinUI.Models;
using NAudio.Dsp;
using NAudio.Wave;

namespace AlgerMusicPlayer.WinUI.Services.Audio;

/// <summary>
/// 音频目标输出声道配置（1:1 还原 .worktrees/local-music-folder-tree/src/renderer/utils/audioDestinationChannel.ts）
/// </summary>
public readonly record struct AudioDestinationChannelConfig(int ChannelCount, string ChannelInterpretation);

/// <summary>
/// 基于 NAudio BiQuadFilter 的 10 段参量均衡器采样处理器（保持源声道数，不做混解码下混）
/// </summary>
public class EqualizerSampleProvider : ISampleProvider
{
    private static readonly HashSet<int> SpeakerLayoutChannelCounts = [1, 2, 4, 6];

    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private readonly int _channels;
    private readonly BiQuadFilter[,] _filters;
    private readonly float[] _bandGains = new float[10];
    private float _preampLinear = 1.0f;
    private bool _isEnabled;
    private bool _isDirty = true;
    private float[] _rateBuffer = new float[65536];

    public float PlaybackRate { get; set; } = 1.0f;

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// 源音频流原始声道数（例如 6 代表 5.1 环绕声，8 代表 7.1 环绕声）
    /// </summary>
    public int SourceChannels => _sourceChannels;

    /// <summary>
    /// 实际输出声道数
    /// </summary>
    public int OutputChannels => _channels;

    /// <summary>
    /// 音频流是否已自然读取到末尾（EOF）
    /// </summary>
    public bool ReachedEndOfStream { get; private set; }

    /// <summary>
    /// 当前音频输出峰值（0.0 ~ 1.0，供沉浸式歌词频谱/律动动效使用）
    /// </summary>
    public float CurrentPeakLevel { get; private set; }

    /// <summary>
    /// 1:1 对应 src/renderer/utils/audioDestinationChannel.ts resolveAudioDestinationChannelConfig
    /// </summary>
    public static AudioDestinationChannelConfig? ResolveAudioDestinationChannelConfig(int? sourceChannelCount, int maxChannelCount)
    {
        if (maxChannelCount < 1) return null;
        int normalizedMax = Math.Max(1, maxChannelCount);
        int normalizedSource = sourceChannelCount is > 0 ? sourceChannelCount.Value : 2;
        return new AudioDestinationChannelConfig(
            ChannelCount: Math.Min(normalizedSource, normalizedMax),
            ChannelInterpretation: SpeakerLayoutChannelCounts.Contains(normalizedSource) ? "speakers" : "discrete");
    }

    public EqualizerSampleProvider(
        ISampleProvider source,
        EqualizerConfig initialConfig)
    {
        _source = source;
        _sourceChannels = Math.Max(1, source.WaveFormat.Channels);
        _channels = _sourceChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, _channels);
        _filters = new BiQuadFilter[_channels, EqualizerConfig.Frequencies.Length];
        ApplyConfig(initialConfig);
    }

    public void ResetEndOfStream()
    {
        ReachedEndOfStream = false;
    }

    public void ApplyConfig(EqualizerConfig config)
    {
        _isEnabled = config.IsEnabled;
        _preampLinear = MathF.Pow(10f, config.PreampDb / 20f);
        for (int i = 0; i < _bandGains.Length && i < config.BandGains.Length; i++)
        {
            _bandGains[i] = Math.Clamp(config.BandGains[i], -12f, 12f);
        }
        _isDirty = true;
    }

    private void RebuildFilters()
    {
        int sampleRate = _source.WaveFormat.SampleRate;
        for (int band = 0; band < EqualizerConfig.Frequencies.Length; band++)
        {
            float freq = EqualizerConfig.Frequencies[band];
            if (freq >= sampleRate * 0.48f)
            {
                freq = sampleRate * 0.45f;
            }

            for (int ch = 0; ch < _channels; ch++)
            {
                _filters[ch, band] = BiQuadFilter.PeakingEQ(sampleRate, freq, 1.41f, _bandGains[band]);
            }
        }
        _isDirty = false;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead;
        float rate = Math.Clamp(PlaybackRate, 0.5f, 2.0f);

        if (MathF.Abs(rate - 1.0f) < 0.02f || _channels <= 0)
        {
            samplesRead = _source.Read(buffer, offset, count);
        }
        else
        {
            int framesRequested = count / _channels;
            int sourceFramesNeeded = Math.Max(1, (int)MathF.Round(framesRequested * rate));
            int sourceSamplesNeeded = sourceFramesNeeded * _channels;
            if (_rateBuffer.Length < sourceSamplesNeeded)
            {
                _rateBuffer = new float[sourceSamplesNeeded * 2];
            }

            int srcRead = _source.Read(_rateBuffer, 0, sourceSamplesNeeded);
            int actualSourceFrames = srcRead / _channels;
            if (actualSourceFrames <= 0)
            {
                ReachedEndOfStream = true;
                CurrentPeakLevel = 0;
                return 0;
            }

            for (int f = 0; f < framesRequested; f++)
            {
                float srcPos = f * (float)actualSourceFrames / framesRequested;
                int i0 = Math.Min((int)srcPos, actualSourceFrames - 1);
                int i1 = Math.Min(i0 + 1, actualSourceFrames - 1);
                float frac = srcPos - i0;

                for (int ch = 0; ch < _channels; ch++)
                {
                    float s0 = _rateBuffer[i0 * _channels + ch];
                    float s1 = _rateBuffer[i1 * _channels + ch];
                    buffer[offset + f * _channels + ch] = s0 + (s1 - s0) * frac;
                }
            }
            samplesRead = framesRequested * _channels;
        }

        if (samplesRead <= 0)
        {
            ReachedEndOfStream = true;
            CurrentPeakLevel = 0;
            return samplesRead;
        }

        if (_isDirty)
        {
            RebuildFilters();
        }

        float maxAbs = 0f;
        int bandCount = EqualizerConfig.Frequencies.Length;

        for (int n = 0; n < samplesRead; n++)
        {
            int idx = offset + n;
            float sample = buffer[idx];

            if (_isEnabled)
            {
                int ch = n % _channels;
                sample *= _preampLinear;
                for (int band = 0; band < bandCount; band++)
                {
                    sample = _filters[ch, band].Transform(sample);
                }
                sample = Math.Clamp(sample, -1.0f, 1.0f);
                buffer[idx] = sample;
            }

            float abs = MathF.Abs(sample);
            if (abs > maxAbs) maxAbs = abs;
        }

        CurrentPeakLevel = maxAbs;
        return samplesRead;
    }
}

/// <summary>
/// 支持 5.1 (6声道) / 7.1 (8声道) WAVEFORMATEXTENSIBLE 扬声器掩码 (dwChannelMask) 的 IEEE Float 采样转字节流封装器。
/// 解决 Windows WASAPI 与 WinMM 对 >2 声道普通 WAVEFORMATEX 抛出 E_NOTIMPL / WaveBadFormat 的底层限制。
/// </summary>
public sealed class MultichannelSampleToWaveProvider : IWaveProvider
{
    private static readonly FieldInfo? ChannelMaskField =
        typeof(WaveFormatExtensible).GetField("dwChannelMask", BindingFlags.Instance | BindingFlags.NonPublic);

    // Windows ksmedia.h 标准多声道扬声器位掩码
    public const int SpeakerMask5Point1Back = 0x0000003F;      // FL | FR | FC | LFE | BL | BR
    public const int SpeakerMask5Point1Surround = 0x0000060F;  // FL | FR | FC | LFE | SL | SR
    public const int SpeakerMask7Point1Surround = 0x0000063F;  // FL | FR | FC | LFE | BL | BR | SL | SR

    private readonly ISampleProvider _source;

    public WaveFormat WaveFormat { get; }

    public MultichannelSampleToWaveProvider(ISampleProvider source, int? explicitChannelMask = null)
    {
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            throw new ArgumentException("Input sample provider must be 32-bit IEEE float", nameof(source));
        }

        _source = source;
        int channels = source.WaveFormat.Channels;
        int sampleRate = source.WaveFormat.SampleRate;

        if (channels > 2)
        {
            WaveFormat = CreateExtensibleFloatWaveFormat(sampleRate, channels, explicitChannelMask);
        }
        else
        {
            WaveFormat = source.WaveFormat;
        }
    }

    public static WaveFormatExtensible CreateExtensibleFloatWaveFormat(int sampleRate, int channels, int? explicitChannelMask = null)
    {
        var wfe = new WaveFormatExtensible(sampleRate, 32, channels);
        int mask = explicitChannelMask ?? GetDefaultSpeakerMask(channels);
        if (mask > 0 && ChannelMaskField != null)
        {
            ChannelMaskField.SetValue(wfe, mask);
        }
        return wfe;
    }

    public static int GetDefaultSpeakerMask(int channels) => channels switch
    {
        1 => 0x00000004, // SPEAKER_FRONT_CENTER
        2 => 0x00000003, // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
        3 => 0x00000007, // FL | FR | FC
        4 => 0x00000033, // FL | FR | BL | BR (Quad)
        5 => 0x00000037, // FL | FR | FC | BL | BR
        6 => SpeakerMask5Point1Back,
        8 => SpeakerMask7Point1Surround,
        _ => 0
    };

    public static int? TryGetChannelMask(WaveFormat? format)
    {
        if (format is WaveFormatExtensible wfe && ChannelMaskField?.GetValue(wfe) is int mask && mask > 0)
        {
            return mask;
        }
        return null;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesNeeded = count / 4;
        var wb = new WaveBuffer(buffer);
        int samplesRead = _source.Read(wb.FloatBuffer, offset / 4, samplesNeeded);
        return samplesRead * 4;
    }
}

