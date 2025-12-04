using System;
using System.Runtime.InteropServices;
using CSCore;

namespace Unai.ExtendedBinaryWaterfall;

// VGM 音频源（基于 libvgm），实现 ISampleSource 接口
public sealed unsafe class VgmAudioSource : ISampleSource
{
    private IntPtr _player;
    private byte[] _vgmData;
    private GCHandle _dataHandle;
    private bool _disposed;
    private float[] _preRenderedBuffer;
    private int _preRenderedPosition;
    private int _preRenderedLength;
    private bool _isPreRendered;
    private long _position;
    private bool _fadeStarted;
    private long _fadeStartPosition;
    private bool _playbackEnded;

    private readonly int _sampleRate;
    private readonly int _channels = 2;
    private readonly WaveFormat _waveFormat;

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public bool IsLoaded => _player != IntPtr.Zero;
    public double Duration { get; private set; }
    public VgmHeader Header { get; private set; }
    public Gd3Tag Gd3 { get; private set; }
    public VgmChipInfo[] ChipList { get; private set; }
    
    // 循环和淡出设置
    public int LoopCount { get; set; } = 2;
    public bool FadeOutEnabled { get; set; } = true;
    public double FadeOutDuration { get; set; } = 5.0;
    
    // ISampleSource 接口实现
    public WaveFormat WaveFormat => _waveFormat;
    public long Length => CalculateTotalLength();
    public long Position 
    { 
        get => _position; 
        set 
        {
            _position = value;
            Seek(value / (double)(_sampleRate * _channels));
        }
    }
    public bool CanSeek => true;

    public VgmAudioSource(int sampleRate = 44100)
    {
        _sampleRate = sampleRate;
        _waveFormat = new WaveFormat(_sampleRate, 32, _channels, AudioEncoding.IeeeFloat);
    }
    
    // 是否有循环点
    public bool HasLoop => Header.LoopSamples > 0;
    
    // 计算总长度（考虑循环和淡出）
    private long CalculateTotalLength()
    {
        if (Header.TotalSamples == 0) return 0;
        
        double baseDuration = Header.TotalSamples / 44100.0;
        double loopDuration = Header.LoopSamples / 44100.0;
        
        // 总时长 = 基础时长 + (循环次数-1) * 循环长度 + 淡出时长
        double totalSeconds = baseDuration;
        
        // 只有有循环点的 VGM 才添加额外循环和淡出
        if (HasLoop)
        {
            if (LoopCount > 1)
            {
                totalSeconds += loopDuration * (LoopCount - 1);
            }
            if (FadeOutEnabled)
            {
                totalSeconds += FadeOutDuration;
            }
        }
        
        Duration = totalSeconds;
        return (long)(totalSeconds * _sampleRate * _channels);
    }

    // 从文件加载 VGM
    public bool LoadFile(string path)
    {
        try
        {
            _vgmData = VgmFormat.LoadVgmFile(path);
            return LoadFromMemory(_vgmData);
        }
        catch (Exception ex)
        {
            Logger.Error($"[VgmAudioSource] 加载文件失败: {ex.Message}");
            return false;
        }
    }

    // 从内存加载 VGM
    public bool LoadFromMemory(byte[] data)
    {
        Unload();

        try
        {
            // 解析头部和标签
            Header = VgmFormat.ParseHeader(data);
            Gd3 = VgmFormat.ParseGd3(data, Header.Gd3Offset);
            ChipList = VgmFormat.GetChipList(Header);
            Duration = VgmFormat.GetDurationSeconds(Header);

            _vgmData = data;

            // 检查 libvgm 是否可用
            if (!LibVgm.IsAvailable())
            {
                Logger.Warning("[VgmAudioSource] libvgm.dll 不可用，使用预渲染模式");
                return PreRenderAudio();
            }

            // 创建播放器
            _player = LibVgm.VgmPlayer_Create();
            if (_player == IntPtr.Zero)
            {
                Logger.Error("[VgmAudioSource] 创建播放器失败");
                return false;
            }

            // 固定数据内存
            _dataHandle = GCHandle.Alloc(_vgmData, GCHandleType.Pinned);
            byte* dataPtr = (byte*)_dataHandle.AddrOfPinnedObject();

            // 加载数据
            byte result = LibVgm.VgmPlayer_LoadData(_player, dataPtr, (uint)_vgmData.Length);
            if (result != 0)
            {
                Logger.Error($"[VgmAudioSource] 加载数据失败: {result}");
                Unload();
                return false;
            }

            // 设置采样率
            LibVgm.VgmPlayer_SetSampleRate(_player, (uint)_sampleRate);

            // 启动播放
            LibVgm.VgmPlayer_Start(_player);
            
            // 计算总长度（包含循环和淡出）
            CalculateTotalLength();
            if (HasLoop)
            {
                Logger.Info($"[VGM] 有循环点, 循环: {LoopCount}次, 淡出: {(FadeOutEnabled ? $"{FadeOutDuration}s" : "禁用")}, 总时长: {Duration:F2}s");
            }
            else
            {
                Logger.Info($"[VGM] 无循环点, 直接播放到结束, 总时长: {Duration:F2}s");
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[VgmAudioSource] 加载失败: {ex.Message}");
            Unload();
            return false;
        }
    }

    // 预渲染音频（libvgm 不可用时的回退方案）
    private bool PreRenderAudio()
    {
        // 暂时返回 false，等待实现纯 C# 芯片模拟
        Logger.Warning("[VgmAudioSource] 预渲染模式尚未实现");
        _isPreRendered = false;
        return false;
    }

    // 卸载
    public void Unload()
    {
        if (_player != IntPtr.Zero)
        {
            LibVgm.VgmPlayer_Stop(_player);
            LibVgm.VgmPlayer_Unload(_player);
            LibVgm.VgmPlayer_Destroy(_player);
            _player = IntPtr.Zero;
        }

        if (_dataHandle.IsAllocated)
        {
            _dataHandle.Free();
        }

        _vgmData = null;
        _preRenderedBuffer = null;
        _preRenderedPosition = 0;
        _preRenderedLength = 0;
        _isPreRendered = false;
    }

    // 读取音频采样（交错立体声浮点格式）
    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed || _playbackEnded) return 0;

        // 预渲染模式
        if (_isPreRendered)
        {
            return ReadPreRendered(buffer, offset, count);
        }

        // 实时渲染模式
        if (_player == IntPtr.Zero) return 0;

        int samplesPerChannel = count / _channels;
        int samplesRendered;

        // 检查循环次数（只有有循环点的 VGM 才会触发淡出）
        if (HasLoop && !_fadeStarted)
        {
            uint currentLoop = LibVgm.VgmPlayer_GetCurLoop(_player);
            if (currentLoop >= LoopCount)
            {
                _fadeStarted = true;
                _fadeStartPosition = _position;
                Logger.Debug($"[VGM] 开始淡出 (循环 {currentLoop}/{LoopCount})");
            }
        }

        // 分配临时缓冲区
        var tempBuffer = stackalloc LibVgm.Wave32Bs[samplesPerChannel];
        
        samplesRendered = (int)LibVgm.VgmPlayer_Render(_player, (uint)samplesPerChannel, tempBuffer);

        // 转换为交错浮点格式，并应用淡出
        int outIdx = offset;
        long fadeSamples = (long)(FadeOutDuration * _sampleRate * _channels);
        
        for (int i = 0; i < samplesRendered; i++)
        {
            float left = tempBuffer[i].Left / 8388608f;
            float right = tempBuffer[i].Right / 8388608f;
            
            // 应用淡出
            if (_fadeStarted && FadeOutEnabled)
            {
                long fadePos = _position + i * _channels - _fadeStartPosition;
                if (fadePos >= fadeSamples)
                {
                    // 淡出结束
                    _playbackEnded = true;
                    Array.Clear(buffer, outIdx, (samplesRendered - i) * _channels);
                    _position += samplesRendered * _channels;
                    return samplesRendered * _channels;
                }
                
                float fadeGain = 1.0f - (float)fadePos / fadeSamples;
                left *= fadeGain;
                right *= fadeGain;
            }
            
            buffer[outIdx++] = left;
            buffer[outIdx++] = right;
        }

        _position += samplesRendered * _channels;
        return samplesRendered * _channels;
    }

    // 读取预渲染缓冲区
    private int ReadPreRendered(float[] buffer, int offset, int count)
    {
        if (_preRenderedBuffer == null) return 0;

        int available = _preRenderedLength - _preRenderedPosition;
        int toRead = Math.Min(count, available);

        if (toRead <= 0) return 0;

        Array.Copy(_preRenderedBuffer, _preRenderedPosition, buffer, offset, toRead);
        _preRenderedPosition += toRead;

        return toRead;
    }

    // 跳转到指定时间（秒）
    public void Seek(double seconds)
    {
        if (_isPreRendered)
        {
            _preRenderedPosition = (int)(seconds * _sampleRate * _channels);
            _preRenderedPosition = Math.Clamp(_preRenderedPosition, 0, _preRenderedLength);
            return;
        }

        if (_player == IntPtr.Zero) return;

        uint tick = (uint)(seconds * 44100);
        LibVgm.VgmPlayer_Seek(_player, LibVgm.UNIT_TICK, tick);
    }

    // 获取当前播放位置（秒）
    public double GetPosition()
    {
        if (_isPreRendered)
        {
            return _preRenderedPosition / (double)(_sampleRate * _channels);
        }

        if (_player == IntPtr.Zero) return 0;

        uint tick = LibVgm.VgmPlayer_GetCurPos(_player, LibVgm.UNIT_TICK);
        return tick / 44100.0;
    }

    // 获取当前循环次数
    public int GetCurrentLoop()
    {
        if (_player == IntPtr.Zero) return 0;
        return (int)LibVgm.VgmPlayer_GetCurLoop(_player);
    }

    // 获取播放状态
    public bool IsPlaying()
    {
        if (_isPreRendered)
        {
            return _preRenderedPosition < _preRenderedLength;
        }

        if (_player == IntPtr.Zero) return false;
        byte state = LibVgm.VgmPlayer_GetState(_player);
        return (state & LibVgm.PLAYSTATE_PLAY) != 0;
    }

    // 是否已到达结尾
    public bool IsEnded()
    {
        if (_isPreRendered)
        {
            return _preRenderedPosition >= _preRenderedLength;
        }

        if (_player == IntPtr.Zero) return true;
        byte state = LibVgm.VgmPlayer_GetState(_player);
        return (state & LibVgm.PLAYSTATE_END) != 0;
    }

    // 设置通道静音
    public void SetChannelMute(int deviceIndex, uint muteMask)
    {
        if (_player == IntPtr.Zero) return;
        LibVgm.VgmPlayer_SetDeviceMute(_player, (uint)deviceIndex, muteMask);
    }

    // 重置播放
    public void Reset()
    {
        if (_isPreRendered)
        {
            _preRenderedPosition = 0;
            return;
        }

        if (_player == IntPtr.Zero) return;
        LibVgm.VgmPlayer_Reset(_player);
        LibVgm.VgmPlayer_Start(_player);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }
}
