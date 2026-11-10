using AlgerMusicPlayer.WinUI.Models;
using NAudio.Wave;

namespace AlgerMusicPlayer.WinUI.Services.Audio;

/// <summary>
/// 多声道 5.1 / 7.1 独立音箱通道增益与静音校准采样处理器
/// </summary>
public class MultichannelGainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _channelGains = new float[8];
    private readonly object _lock = new();

    public WaveFormat WaveFormat => _source.WaveFormat;

    public MultichannelGainSampleProvider(ISampleProvider source, MultichannelConfig config)
    {
        _source = source;
        ApplyConfig(config);
    }

    public void ApplyConfig(MultichannelConfig config)
    {
        var gains = ComputeGains(config);
        lock (_lock)
        {
            Array.Copy(gains, _channelGains, _channelGains.Length);
        }
    }

    /// <summary>
    /// 按标准 Windows WAVEFORMATEXTENSIBLE 声道索引计算 8 路线性增益：
    /// 0: FL, 1: FR, 2: C, 3: LFE, 4: SL/BL, 5: SR/BR, 6: 7.1 SL, 7: 7.1 SR
    /// </summary>
    public static float[] ComputeGains(MultichannelConfig config)
    {
        var g = new float[8];
        if (!config.IsEnabled)
        {
            Array.Fill(g, 1.0f);
            return g;
        }

        g[0] = config.MuteFrontLeft ? 0f : DbToLinear(config.FrontLeftDb);
        g[1] = config.MuteFrontRight ? 0f : DbToLinear(config.FrontRightDb);
        g[2] = config.MuteCenter ? 0f : DbToLinear(config.CenterDb);
        g[3] = config.MuteSubwoofer ? 0f : DbToLinear(config.SubwooferDb);
        g[4] = (config.MuteSurroundLeft || config.MuteBackLeft) ? 0f : DbToLinear((config.SurroundLeftDb + config.BackLeftDb) / 2f);
        g[5] = (config.MuteSurroundRight || config.MuteBackRight) ? 0f : DbToLinear((config.SurroundRightDb + config.BackRightDb) / 2f);
        g[6] = config.MuteSurroundLeft ? 0f : DbToLinear(config.SurroundLeftDb);
        g[7] = config.MuteSurroundRight ? 0f : DbToLinear(config.SurroundRightDb);
        return g;
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10.0, Math.Clamp(db, -24f, 18f) / 20.0);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        int channels = WaveFormat.Channels;
        if (channels < 1) return read;

        lock (_lock)
        {
            for (int i = 0; i < read; i++)
            {
                int ch = i % channels;
                if (ch < _channelGains.Length)
                {
                    buffer[offset + i] *= _channelGains[ch];
                }
            }
        }

        return read;
    }
}
