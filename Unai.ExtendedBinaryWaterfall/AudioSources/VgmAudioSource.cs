using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CSCore;

namespace Unai.ExtendedBinaryWaterfall;

// VGM 音频源（基于 libvgm），实现 ISampleSource 接口
// 使用预渲染模式：加载时将整个 VGM 渲染到内存
public sealed unsafe class VgmAudioSource : ISampleSource
{
    private byte[] _vgmData;
    private bool _disposed;
    private float[] _preRenderedBuffer;
    private int _preRenderedPosition;
    private int _preRenderedLength;
    private bool _isPreRendered;
    private long _position;
    private bool _playbackEnded;

    private readonly int _sampleRate;
    private readonly int _channels = 2;
    private readonly WaveFormat _waveFormat;

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public bool IsLoaded => _isPreRendered && _preRenderedBuffer != null && _preRenderedLength > 0;
    public double Duration { get; private set; }
    public VgmHeader Header { get; private set; }
    public Gd3Tag Gd3 { get; private set; }
    public VgmChipInfo[] ChipList { get; private set; }
    
    // 循环和淡出设置
    public int LoopCount { get; set; } = 2;
    public bool FadeOutEnabled { get; set; } = true;
    public double FadeOutDuration { get; set; } = 5.0;
    
    // 播放结束标记（用于 MultiFileAudioSource EOF 检测）
    public bool IsEof => _playbackEnded || _disposed || _preRenderedPosition >= _preRenderedLength;
    
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
    // 淡出逻辑：
    // - 无循环点：直接播放完成，不淡出
    // - 有循环点：遵守用户设置的循环数，无论设置多少都必须淡出
    private long CalculateTotalLength()
    {
        if (Header.TotalSamples == 0) return 0;
        
        double baseDuration = Header.TotalSamples / 44100.0;
        double loopDuration = Header.LoopSamples / 44100.0;
        double totalSeconds = baseDuration;
        
        if (HasLoop)
        {
            // 有循环点：遵守用户设置的循环数，总是淡出
            // LoopCount=1 表示播放1次（到循环结束点），然后淡出
            // LoopCount=2 表示播放2次（额外循环1次），然后淡出
            if (LoopCount > 1)
            {
                totalSeconds += loopDuration * (LoopCount - 1);
            }
            if (FadeOutEnabled)
            {
                totalSeconds += FadeOutDuration;
            }
        }
        // 无循环点：直接播放 baseDuration，不添加淡出
        
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

    // 从内存加载 VGM（使用预渲染模式避免多文件崩溃）
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
                Logger.Warning("[VgmAudioSource] libvgm.dll 不可用");
                return false;
            }

            // 计算总长度（包含循环和淡出）
            CalculateTotalLength();
            
            // 预渲染整个音频到内存（关键：避免运行时切换 libvgm 导致崩溃）
            return PreRenderAudioWithLibvgm();
        }
        catch (Exception ex)
        {
            Logger.Error($"[VgmAudioSource] 加载失败: {ex.Message}");
            Unload();
            return false;
        }
    }

    // 使用 libvgm 预渲染整个音频到内存
    private bool PreRenderAudioWithLibvgm()
    {
        IntPtr player = IntPtr.Zero;
        GCHandle dataHandle = default;
        
        try
        {
            // 创建临时播放器
            player = LibVgm.VgmPlayer_Create();
            if (player == IntPtr.Zero)
            {
                Logger.Error("[VgmAudioSource] 创建播放器失败");
                return false;
            }

            // 固定数据内存
            dataHandle = GCHandle.Alloc(_vgmData, GCHandleType.Pinned);
            byte* dataPtr = (byte*)dataHandle.AddrOfPinnedObject();

            // 加载数据
            byte result = LibVgm.VgmPlayer_LoadData(player, dataPtr, (uint)_vgmData.Length);
            if (result != 0)
            {
                Logger.Error($"[VgmAudioSource] 加载数据失败: {result}");
                return false;
            }

            // 设置采样率
            LibVgm.VgmPlayer_SetSampleRate(player, (uint)_sampleRate);
            LibVgm.VgmPlayer_Start(player);
            
            // 调试：输出libvgm检测到的设备信息
            uint deviceCount = LibVgm.VgmPlayer_GetDeviceCount(player);
            Logger.Debug($"[VgmAudioSource] libvgm检测到 {deviceCount} 个设备");
            for (uint i = 0; i < deviceCount; i++)
            {
                if (LibVgm.VgmPlayer_GetDeviceInfo(player, i, out var devInfo) == 0)
                {
                    string name = devInfo.Name != IntPtr.Zero 
                        ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi(devInfo.Name) ?? "Unknown"
                        : "Unknown";
                    Logger.Debug($"  设备{i}: {name} (Type=0x{devInfo.Type:X}, Clock={devInfo.Clock})");
                }
            }

            // 计算需要渲染的总采样数
            long totalSamples = (long)(Duration * _sampleRate);
            _preRenderedBuffer = new float[totalSamples * _channels];
            _preRenderedLength = 0;
            
            // 渲染音频
            int chunkSize = 4096;
            var tempBuffer = stackalloc LibVgm.Wave32Bs[chunkSize];
            long fadeSamples = (long)(FadeOutDuration * _sampleRate);
            long fadeStartSample = totalSamples - fadeSamples;
            // 淡出逻辑：有循环点时总是淡出，无循环点时不淡出
            bool fadeEnabled = FadeOutEnabled && HasLoop;
            
            Logger.Debug($"[VgmAudioSource] 开始预渲染: {totalSamples} samples, fade={fadeEnabled}, loop={LoopCount}");
            
            bool renderComplete = false;
            // 计算缓冲区大小（字节数）
            int bufferByteSize = chunkSize * sizeof(LibVgm.Wave32Bs);
            
            while (_preRenderedLength < _preRenderedBuffer.Length && !renderComplete)
            {
                // 有循环点时，使用淡出来结束渲染（通过 fadeEnabled 控制）
                // 无循环点时，libvgm 会自动在 PLAYSTATE_END 时停止
                
                int samplesToRender = Math.Min(chunkSize, (_preRenderedBuffer.Length - _preRenderedLength) / _channels);
                if (samplesToRender <= 0) break;
                
                // 关键：每次渲染前必须清零缓冲区
                // libvgm 的 Render 函数会将音频数据累加到缓冲区，而不是覆盖
                // 如果不清零，会导致音频被不断叠加，产生"回声"故障
                Unsafe.InitBlock(tempBuffer, 0, (uint)bufferByteSize);
                
                int rendered = (int)LibVgm.VgmPlayer_Render(player, (uint)samplesToRender, tempBuffer);
                if (rendered <= 0) break;
                
                // 转换并写入缓冲区
                for (int i = 0; i < rendered && !renderComplete; i++)
                {
                    float left = tempBuffer[i].Left / 8388608f;
                    float right = tempBuffer[i].Right / 8388608f;
                    
                    // 应用淡出
                    if (fadeEnabled && _preRenderedLength / _channels >= fadeStartSample)
                    {
                        long fadePos = _preRenderedLength / _channels - fadeStartSample;
                        if (fadePos >= fadeSamples)
                        {
                            // 淡出结束，完全停止渲染
                            renderComplete = true;
                            break;
                        }
                        float fadeGain = 1.0f - (float)fadePos / fadeSamples;
                        left *= fadeGain;
                        right *= fadeGain;
                    }
                    
                    _preRenderedBuffer[_preRenderedLength++] = left;
                    _preRenderedBuffer[_preRenderedLength++] = right;
                }
                
                // 检查播放器状态
                byte state = LibVgm.VgmPlayer_GetState(player);
                if ((state & LibVgm.PLAYSTATE_END) != 0 && !HasLoop)
                    break;
            }
            
            _isPreRendered = true;
            _preRenderedPosition = 0;
            
            string loopInfo = HasLoop 
                ? (LoopCount == 1 ? "1次（不循环）" : $"{LoopCount}次（循环{LoopCount - 1}次）")
                : "无循环";
            Logger.Info($"[VGM] 预渲染完成: {_preRenderedLength / _channels / (float)_sampleRate:F2}s, 循环: {loopInfo}");
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[VgmAudioSource] 预渲染失败: {ex.Message}");
            return false;
        }
        finally
        {
            // 警告：不要释放 libvgm 的任何资源（Stop/Unload/Destroy），否则会导致崩溃
            // libvgm 内部状态管理存在问题，调用清理函数会导致访问违规
            // 这会造成少量内存泄漏，但可以避免崩溃
            
            // GCHandle 也不能释放，因为 libvgm 可能仍在引用该内存
            // dataHandle 保持 pinned 状态，确保数据不被 GC 移动或回收
            
            // 预渲染完成后，_vgmData 引用可以设为 null
            // 但实际内存由于 GCHandle pinned 不会被回收（这是预期的）
            _vgmData = null;
        }
    }

    // 卸载
    public void Unload()
    {
        // 预渲染模式下只需清理缓冲区
        _vgmData = null;
        _preRenderedBuffer = null;
        _preRenderedPosition = 0;
        _preRenderedLength = 0;
        _isPreRendered = false;
        _playbackEnded = false;
        _position = 0;
    }

    // 读取音频采样（交错立体声浮点格式）
    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed || _playbackEnded) return 0;
        if (!_isPreRendered || _preRenderedBuffer == null) return 0;
        return ReadPreRendered(buffer, offset, count);
    }

    // 读取预渲染缓冲区
    private int ReadPreRendered(float[] buffer, int offset, int count)
    {
        if (_preRenderedBuffer == null) return 0;

        int available = _preRenderedLength - _preRenderedPosition;
        int toRead = Math.Min(count, available);

        if (toRead <= 0)
        {
            _playbackEnded = true;
            return 0;
        }

        Array.Copy(_preRenderedBuffer, _preRenderedPosition, buffer, offset, toRead);
        _preRenderedPosition += toRead;
        _position = _preRenderedPosition;  // 同步 Position 属性

        return toRead;
    }

    // 跳转到指定时间（秒）
    public void Seek(double seconds)
    {
        _preRenderedPosition = (int)(seconds * _sampleRate * _channels);
        _preRenderedPosition = Math.Clamp(_preRenderedPosition, 0, _preRenderedLength);
        _position = _preRenderedPosition;
    }

    // 获取当前播放位置（秒）
    public double GetPosition()
    {
        return _preRenderedPosition / (double)(_sampleRate * _channels);
    }

    // 获取当前循环次数（预渲染模式下根据位置计算）
    // 返回值范围：1 到 LoopCount，淡出期间显示为 LoopCount
    public int GetCurrentLoop() => GetCurrentLoopAtTime(GetPosition());
    
    // 获取指定时间的循环次数（用于视频渲染时传入帧号计算的时间）
    public int GetCurrentLoopAtTime(double timeSec)
    {
        if (!HasLoop || Header.LoopSamples == 0) return 1;
        double loopDuration = Header.LoopSamples / 44100.0;
        double introLength = (Header.TotalSamples - Header.LoopSamples) / 44100.0;
        if (timeSec <= introLength) return 1;
        int currentLoop = 1 + (int)((timeSec - introLength) / loopDuration);
        // 限制最大值为 LoopCount（淡出期间显示为最后一次循环）
        return Math.Min(currentLoop, LoopCount);
    }
    
    // 获取当前采样偏移（基于预渲染位置计算）
    public uint GetCurrentSampleOffset() => GetCurrentSampleOffsetAtTime(GetPosition());
    
    // 获取指定时间的采样偏移（用于视频渲染时传入帧号计算的时间）
    public uint GetCurrentSampleOffsetAtTime(double timeSec)
    {
        if (!HasLoop) return (uint)(timeSec * 44100);
        
        double loopDuration = Header.LoopSamples / 44100.0;
        double introLength = (Header.TotalSamples - Header.LoopSamples) / 44100.0;
        
        if (timeSec <= introLength)
            return (uint)(timeSec * 44100);
        
        double timeInLoop = (timeSec - introLength) % loopDuration;
        return (uint)((introLength + timeInLoop) * 44100);
    }
    
    // 获取循环点采样偏移
    public uint GetLoopSampleOffset()
    {
        return Header.LoopSamples > 0 ? Header.TotalSamples - Header.LoopSamples : 0;
    }
    
    // 获取最大采样偏移
    public uint GetMaxSampleOffset()
    {
        return Header.TotalSamples;
    }

    // 获取播放状态
    public bool IsPlaying()
    {
        return _preRenderedPosition < _preRenderedLength;
    }

    // 是否已到达结尾
    public bool IsEnded()
    {
        return _preRenderedPosition >= _preRenderedLength;
    }

    // 设置通道静音（预渲染模式下不支持）
    public void SetChannelMute(int deviceIndex, uint muteMask)
    {
        // 预渲染模式不支持动态静音
    }

    // 重置播放
    public void Reset()
    {
        _preRenderedPosition = 0;
        _position = 0;
        _playbackEnded = false;
    }
    
    // 计算预渲染音频的波形 RMS 值（用于底部进度条显示）
    // rmsCount: 波形分段数量
    // 返回: (rmsValues, audioPeak) - RMS 数组和最大峰值
    public (float[] rmsValues, float audioPeak) ComputeWaveform(int rmsCount = 256)
    {
        if (!_isPreRendered || _preRenderedBuffer == null || _preRenderedLength <= 0)
            return (null, 1.0f);
        
        long totalSamples = _preRenderedLength;
        long samplesPerSegment = totalSamples / rmsCount;
        if (samplesPerSegment < 1) samplesPerSegment = 1;
        
        // RMS 累加器
        double[] sumSquares = new double[rmsCount];
        long[] sampleCounts = new long[rmsCount];
        float maxPeak = 0f;
        
        // 遍历预渲染缓冲区计算 RMS
        for (int i = 0; i < _preRenderedLength; i++)
        {
            int segmentIndex = (int)(i / samplesPerSegment);
            if (segmentIndex >= rmsCount) segmentIndex = rmsCount - 1;
            
            float sample = _preRenderedBuffer[i];
            float absVal = Math.Abs(sample);
            sumSquares[segmentIndex] += sample * sample;
            sampleCounts[segmentIndex]++;
            if (absVal > maxPeak) maxPeak = absVal;
        }
        
        // 计算每段的 RMS 值
        float[] rmsValues = new float[rmsCount];
        float globalMaxRms = 0f;
        
        for (int i = 0; i < rmsCount; i++)
        {
            if (sampleCounts[i] > 0)
            {
                float rms = (float)Math.Sqrt(sumSquares[i] / sampleCounts[i]);
                rmsValues[i] = rms;
                if (rms > globalMaxRms) globalMaxRms = rms;
            }
        }
        
        // 归一化到 0-1 范围
        if (globalMaxRms > 0.0001f)
        {
            for (int i = 0; i < rmsCount; i++)
            {
                rmsValues[i] = rmsValues[i] / globalMaxRms;
            }
        }
        
        float audioPeak = maxPeak > 0.001f ? maxPeak : 1.0f;
        return (rmsValues, audioPeak);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }
}
