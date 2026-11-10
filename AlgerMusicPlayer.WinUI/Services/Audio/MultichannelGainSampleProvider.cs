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
        lock (_lock)
        {
            if (!config.IsEnabled)
            {
                Array.Fill(_channelGains, 1.0f);
                return;
            }

            // 标准 Windows WAVEFORMATEXTENSIBLE 声道索引映射：
            // 0: 前置左 (FL)
            // 1: 前置右 (FR)
            // 2: 前置中置 (C)
            // 3: 重低音 (LFE / Subwoofer)
            // 4: 环绕/后置左 (SL / BL)
            // 5: 环绕/后置右 (SR / BR)
            // 6: 侧环绕左 (7.1 SL)
            // 7: 侧环绕右 (7.1 SR)
            _channelGains[0] = config.MuteFrontLeft ? 0f : DbToLinear(config.FrontLeftDb);
            _channelGains[1] = config.MuteFrontRight ? 0f : DbToLinear(config.FrontRightDb);
            _channelGains[2] = config.MuteCenter ? 0f : DbToLinear(config.CenterDb);
            _channelGains[3] = config.MuteSubwoofer ? 0f : DbToLinear(config.SubwooferDb);
            _channelGains[4] = (config.MuteSurroundLeft || config.MuteBackLeft) ? 0f : DbToLinear((config.SurroundLeftDb + config.BackLeftDb) / 2f);
            _channelGains[5] = (config.MuteSurroundRight || config.MuteBackRight) ? 0f : DbToLinear((config.SurroundRightDb + config.BackRightDb) / 2f);
            _channelGains[6] = config.MuteSurroundLeft ? 0f : DbToLinear(config.SurroundLeftDb);
            _channelGains[7] = config.MuteSurroundRight ? 0f : DbToLinear(config.SurroundRightDb);
        }
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
