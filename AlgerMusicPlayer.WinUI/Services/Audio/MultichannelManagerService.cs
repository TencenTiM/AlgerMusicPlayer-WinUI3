using System.Diagnostics;
using AlgerMusicPlayer.WinUI.Models;
using AlgerMusicPlayer.WinUI.Services.System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AlgerMusicPlayer.WinUI.Services.Audio;

/// <summary>
/// 多声道输出设备硬件属性快照
/// </summary>
public class MultichannelDeviceInfo
{
    public string DeviceName { get; set; } = "默认音频设备";
    public int Channels { get; set; } = 2;
    public string ChannelTypeName { get; set; } = "2.0 双声道立体声";
    public int SampleRate { get; set; } = 48000;
    public int BitsPerSample { get; set; } = 32;
    public int ChannelMask { get; set; } = 0x3;
    public string LayoutDescription { get; set; } = "标准双声道 (左/右)";
    public bool IsMultichannelActive { get; set; }
    public bool HardwareSupportsMultichannel { get; set; }
    public string DiagnosticMessage { get; set; } = string.Empty;
}

/// <summary>
/// 多声道 5.1 / 7.1 设备侦测、空间声场校准与单声道独立发声测试管理器
/// </summary>
public class MultichannelManagerService : IDisposable
{
    private readonly ConfigService _configService;
    private WasapiOut? _activeTestDevice;
    private readonly object _testLock = new();

    public MultichannelManagerService(ConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>
    /// 精准侦测当前 Windows 音频输出端点的声道数、格式及 5.1/7.1 硬件支持能力
    /// </summary>
    public MultichannelDeviceInfo DetectCurrentDeviceInfo(int waveOutDeviceNumber = -1)
    {
        var info = new MultichannelDeviceInfo();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? dev = null;

            if (waveOutDeviceNumber >= 0 && waveOutDeviceNumber < WaveOut.DeviceCount)
            {
                var caps = WaveOut.GetCapabilities(waveOutDeviceNumber);
                var prodName = caps.ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(prodName))
                {
                    foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        if (d.FriendlyName.Contains(prodName, StringComparison.OrdinalIgnoreCase) ||
                            prodName.Contains(d.FriendlyName, StringComparison.OrdinalIgnoreCase))
                        {
                            dev = d;
                            break;
                        }
                        d.Dispose();
                    }
                }
            }

            dev ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            info.DeviceName = dev.FriendlyName ?? "默认输出设备";

            var mixFormat = dev.AudioClient.MixFormat;
            info.Channels = Math.Max(1, mixFormat.Channels);
            info.SampleRate = mixFormat.SampleRate;
            info.BitsPerSample = mixFormat.BitsPerSample;

            int mask = MultichannelSampleToWaveProvider.TryGetChannelMask(mixFormat) ??
                       MultichannelSampleToWaveProvider.GetDefaultSpeakerMask(info.Channels);
            info.ChannelMask = mask;

            if (info.Channels >= 8)
            {
                info.ChannelTypeName = "7.1 环绕声 (8 声道)";
                info.LayoutDescription = "前左/中/前右 + 低音炮 + 侧左/侧右 + 后左/后右 (FL/FR/C/LFE/SL/SR/BL/BR)";
                info.IsMultichannelActive = true;
                info.HardwareSupportsMultichannel = true;
                info.DiagnosticMessage = "当前设备已原生配置为 7.1 全景声输出，所有音频声道均可无损点对点送达独立音箱。";
            }
            else if (info.Channels == 6)
            {
                info.ChannelTypeName = "5.1 环绕声 (6 声道)";
                bool isSide = (mask & 0x600) != 0;
                info.LayoutDescription = isSide
                    ? "前左/中/前右 + 低音炮 + 侧左/侧右 (FL/FR/C/LFE/SL/SR)"
                    : "前左/中/前右 + 低音炮 + 后左/后右 (FL/FR/C/LFE/BL/BR)";
                info.IsMultichannelActive = true;
                info.HardwareSupportsMultichannel = true;
                info.DiagnosticMessage = "当前设备已原生配置为 5.1 环绕声输出，本播放器将以 WASAPI 共享直通输出各卫星箱与重低音。";
            }
            else
            {
                info.ChannelTypeName = $"{info.Channels}.0 立体声";
                info.LayoutDescription = "前左 + 前右 (FL/FR)";
                info.IsMultichannelActive = false;

                // 探测当前声卡在 WASAPI 共享模式下是否支持 5.1 或 7.1 格式
                var test51 = MultichannelSampleToWaveProvider.CreateExtensibleFloatWaveFormat(
                    mixFormat.SampleRate, 6, MultichannelSampleToWaveProvider.SpeakerMask5Point1Back);
                bool can51 = dev.AudioClient.IsFormatSupported(AudioClientShareMode.Shared, test51, out _);

                var test71 = MultichannelSampleToWaveProvider.CreateExtensibleFloatWaveFormat(
                    mixFormat.SampleRate, 8, MultichannelSampleToWaveProvider.SpeakerMask7Point1Surround);
                bool can71 = dev.AudioClient.IsFormatSupported(AudioClientShareMode.Shared, test71, out _);

                if (can51 || can71)
                {
                    info.HardwareSupportsMultichannel = true;
                    info.DiagnosticMessage = $"硬件支持 {(can71 ? "7.1 / 5.1" : "5.1")} 环绕声，但 Windows 混音器当前设为 2 声道。可在系统声音设置中将扬声器配置设为 5.1/7.1 获得最佳效果。";
                }
                else
                {
                    info.HardwareSupportsMultichannel = false;
                    info.DiagnosticMessage = "当前音频设备处于 2 声道立体声模式。播放多声道音源时播放器将智能映射输出。";
                }
            }

            dev.Dispose();
        }
        catch (Exception ex)
        {
            info.DeviceName = "系统默认扬声器";
            info.DiagnosticMessage = $"检测信息受限: {ex.Message}";
        }

        return info;
    }

    /// <summary>
    /// 打开 Windows 经典声音控制面板扬声器配置 (mmsys.cpl)
    /// </summary>
    public static void OpenWindowsSoundControlPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,0",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:sound",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    /// <summary>
    /// 单个独立音箱发声测试（仅指定音箱发声，其他音箱全部完全静音）
    /// speakerIndex: 0=FL, 1=FR, 2=C, 3=LFE, 4=SL/BL, 5=SR/BR, 6=BL, 7=BR
    /// </summary>
    public async Task PlaySingleSpeakerTestAsync(int speakerIndex, int totalChannels = 6)
    {
        await Task.Run(() =>
        {
            lock (_testLock)
            {
                StopTestTone();

                int sampleRate = 48000;
                int channels = Math.Max(2, totalChannels);
                int mask = channels == 8
                    ? MultichannelSampleToWaveProvider.SpeakerMask7Point1Surround
                    : (channels == 6 ? MultichannelSampleToWaveProvider.SpeakerMask5Point1Back : 0x3);

                // 低音炮使用 80Hz 沉浸重低音震荡，其他卫星音箱使用 440Hz / 600Hz / 800Hz 纯音
                double frequency = speakerIndex == 3 ? 80.0 : (speakerIndex == 2 ? 600.0 : 440.0);
                double durationSeconds = speakerIndex == 3 ? 0.75 : 0.55;
                int totalFrames = (int)(sampleRate * durationSeconds);

                var sampleProvider = new SingleChannelTestSampleProvider(
                    sampleRate, channels, speakerIndex, frequency, totalFrames);

                var waveProvider = new MultichannelSampleToWaveProvider(sampleProvider, mask);

                try
                {
                    var wasapi = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 100);
                    wasapi.Init(waveProvider);
                    _activeTestDevice = wasapi;
                    wasapi.Play();
                }
                catch
                {
                    // 若不支持 WASAPI 扩展格式测试，回退至 WaveOut
                    try
                    {
                        var waveOut = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 100 };
                        waveOut.Init(waveProvider);
                        waveOut.Play();
                    }
                    catch { }
                }
            }
        });
    }

    /// <summary>
    /// 停止正在发声的测试音
    /// </summary>
    public void StopTestTone()
    {
        lock (_testLock)
        {
            if (_activeTestDevice != null)
            {
                try
                {
                    _activeTestDevice.Stop();
                }
                catch { }
                try
                {
                    _activeTestDevice.Dispose();
                }
                catch { }
                _activeTestDevice = null;
            }
        }
    }

    public void Dispose()
    {
        StopTestTone();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 纯内存单声道测试正弦脉冲音频采样源
/// </summary>
internal class SingleChannelTestSampleProvider : ISampleProvider
{
    private readonly int _channels;
    private readonly int _targetChannel;
    private readonly double _frequency;
    private readonly int _totalFrames;
    private int _currentFrame;

    public WaveFormat WaveFormat { get; }

    public SingleChannelTestSampleProvider(int sampleRate, int channels, int targetChannel, double frequency, int totalFrames)
    {
        _channels = channels;
        _targetChannel = Math.Clamp(targetChannel, 0, channels - 1);
        _frequency = frequency;
        _totalFrames = totalFrames;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int sampleRate = WaveFormat.SampleRate;
        int framesNeeded = count / _channels;
        int framesToRead = Math.Min(framesNeeded, _totalFrames - _currentFrame);

        if (framesToRead <= 0) return 0;

        int sampleIndex = 0;
        for (int f = 0; f < framesToRead; f++)
        {
            int frameIdx = _currentFrame + f;
            double t = (double)frameIdx / sampleRate;

            // 汉宁窗 (Hanning Window) 平滑淡入淡出，彻底杜绝爆音 (Click/Pop)
            double window = 1.0;
            int fadeFrames = sampleRate / 20; // 50ms 平滑
            if (frameIdx < fadeFrames)
            {
                window = 0.5 * (1.0 - Math.Cos(Math.PI * frameIdx / fadeFrames));
            }
            else if (frameIdx > _totalFrames - fadeFrames)
            {
                window = 0.5 * (1.0 - Math.Cos(Math.PI * (_totalFrames - frameIdx) / fadeFrames));
            }

            float sampleValue = (float)(Math.Sin(2.0 * Math.PI * _frequency * t) * 0.45 * window);

            for (int ch = 0; ch < _channels; ch++)
            {
                buffer[offset + sampleIndex++] = (ch == _targetChannel) ? sampleValue : 0f;
            }
        }

        _currentFrame += framesToRead;
        return framesToRead * _channels;
    }
}
