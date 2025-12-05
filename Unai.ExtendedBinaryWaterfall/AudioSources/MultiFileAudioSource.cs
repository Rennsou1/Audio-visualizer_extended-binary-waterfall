using System;
using System.Collections.Generic;
using System.IO;
using CSCore;
using Melanchall.DryWetMidi.Interaction;

namespace Unai.ExtendedBinaryWaterfall;

// 多文件音频源：支持顺序读取多个音频文件，自动切换到下一个文件
// 支持 FFmpeg 解码器、MIDI (FluidSynth) 渲染和 VGM (libvgm) 渲染
// 所有文件会被重采样到统一的目标采样率和声道数
public class MultiFileAudioSource : ISampleSource
{
    private readonly List<string> _filePaths;
    private readonly List<long> _fileLengths;      // 每个文件的采样数长度（基于目标采样率）
    private readonly List<long> _fileOffsets;      // 每个文件的起始偏移量
    private readonly List<bool> _isMidiFile;       // 每个文件是否为 MIDI 格式
    private readonly List<bool> _isVgmFile;        // 每个文件是否为 VGM 格式
    private int _currentFileIndex = 0;
    private ISampleSource _currentSource;          // 当前音频源（FFmpeg 或 MIDI）
    private WaveFormat _waveFormat;
    private long _totalLength;
    private long _position;
    private bool _isDisposed;
    
    // 目标采样率和声道数（用于统一所有文件的输出格式）
    private readonly int _targetSampleRate;
    private readonly int _targetChannels;
    
    // VGM 循环设置（从 Generator 传入）
    private readonly int _vgmLoopCount;
    private readonly bool _vgmFadeOutEnabled;
    private readonly double _vgmFadeOutDuration;

    // 当前正在播放的文件索引
    public int CurrentFileIndex => _currentFileIndex;
    
    // 文件总数
    public int FileCount => _filePaths.Count;
    
    // 文件切换版本号（每次切换文件时递增，用于检测文件切换）
    private int _fileChangeVersion = 0;
    public int FileChangeVersion => _fileChangeVersion;
    
    // 当前 VGM 音频源（用于 Generator 获取进度信息，如果当前不是 VGM 则返回 null）
    public VgmAudioSource CurrentVgmAudioSource
    {
        get
        {
            if (_currentSource is VgmAudioSource vgm) return vgm;
            if (_currentSource is VgmResamplingSource resample) return resample.VgmSource;
            return null;
        }
    }

    // 构造函数：支持指定目标采样率和声道数，以及 VGM 循环设置
    // targetSampleRate <= 0 时使用第一个文件的采样率
    // targetChannels <= 0 时使用第一个文件的声道数
    public MultiFileAudioSource(List<string> filePaths, int targetSampleRate = 0, int targetChannels = 0,
        int vgmLoopCount = 2, bool vgmFadeOutEnabled = true, double vgmFadeOutDuration = 5.0)
    {
        _filePaths = filePaths ?? throw new ArgumentNullException(nameof(filePaths));
        _fileLengths = new List<long>();
        _fileOffsets = new List<long>();
        _isMidiFile = new List<bool>();
        _isVgmFile = new List<bool>();
        
        // 保存 VGM 设置
        _vgmLoopCount = vgmLoopCount;
        _vgmFadeOutEnabled = vgmFadeOutEnabled;
        _vgmFadeOutDuration = vgmFadeOutDuration;
        
        if (_filePaths.Count == 0)
            throw new ArgumentException("至少需要一个音频文件", nameof(filePaths));

        // 确定目标格式：如果未指定，使用第一个普通音频文件的格式，或默认值
        if (targetSampleRate <= 0 || targetChannels <= 0)
        {
            // 查找第一个普通音频文件来获取格式（排除 MIDI 和 VGM）
            foreach (var path in _filePaths)
            {
                if (!MidiAudioSourceFactory.IsMidiFile(path) && !IsVgmFile(path))
                {
                    using var firstDecoder = new FfmpegAudioDecoder(path);
                    targetSampleRate = targetSampleRate > 0 ? targetSampleRate : firstDecoder.WaveFormat.SampleRate;
                    targetChannels = targetChannels > 0 ? targetChannels : firstDecoder.WaveFormat.Channels;
                    break;
                }
            }
            // 如果全是 MIDI/VGM 文件，使用默认值
            if (targetSampleRate <= 0) targetSampleRate = 48000;
            if (targetChannels <= 0) targetChannels = 2;
        }
        
        _targetSampleRate = targetSampleRate;
        _targetChannels = targetChannels;
        
        // 创建统一的 WaveFormat（所有文件都会重采样/渲染到这个格式）
        _waveFormat = new WaveFormat(_targetSampleRate, 32, _targetChannels, AudioEncoding.IeeeFloat);
        
        Logger.Info($"[MultiFileAudioSource] 目标格式: {_targetSampleRate}Hz {_targetChannels}ch");

        // 计算所有文件的总长度（基于目标采样率）
        CalculateTotalLength();
        
        // 打开第一个文件
        OpenFile(0);
        if (_currentSource != null && _currentSource.CanSeek)
        {
            _currentSource.Position = 0;
        }
    }

    // 计算所有文件的总采样数（基于目标采样率）
    private void CalculateTotalLength()
    {
        _totalLength = 0;
        _fileLengths.Clear();
        _fileOffsets.Clear();
        _isMidiFile.Clear();
        _isVgmFile.Clear();

        foreach (var filePath in _filePaths)
        {
            if (!File.Exists(filePath)) continue;

            bool isMidi = MidiAudioSourceFactory.IsMidiFile(filePath);
            bool isVgm = IsVgmFile(filePath);
            _isMidiFile.Add(isMidi);
            _isVgmFile.Add(isVgm);

            try
            {
                long fileLength;
                if (isMidi)
                {
                    // MIDI 文件：直接使用 DryWetMidi 计算时长
                    var midiFile = Melanchall.DryWetMidi.Core.MidiFile.Read(filePath);
                    var duration = midiFile.GetDuration<Melanchall.DryWetMidi.Interaction.MetricTimeSpan>();
                    double durationSeconds = duration.TotalMicroseconds / 1_000_000.0;
                    fileLength = (long)(durationSeconds * _targetSampleRate * _targetChannels);
                    Logger.Debug($"[MultiFileAudioSource] MIDI 文件 {Path.GetFileName(filePath)}: {durationSeconds:F2}s, {fileLength} 采样");
                }
                else if (isVgm)
                {
                    // VGM 文件：使用 VgmFormat 快速解析头部获取时长（不初始化播放器）
                    var header = VgmFormat.LoadHeader(filePath);
                    double durationSeconds = VgmFormat.GetDurationSeconds(header);
                    
                    // 使用用户设置的循环次数和淡出时长计算总时长
                    // 与 VgmAudioSource.CalculateTotalLength 保持一致
                    if (header.LoopSamples > 0)
                    {
                        double loopSec = header.LoopSamples / 44100.0;
                        // 循环时长 = (循环次数 - 1) * 循环段时长
                        if (_vgmLoopCount > 1)
                        {
                            durationSeconds += loopSec * (_vgmLoopCount - 1);
                        }
                        // 淡出时长（只要有循环点就添加淡出）
                        if (_vgmFadeOutEnabled)
                        {
                            durationSeconds += _vgmFadeOutDuration;
                        }
                    }
                    
                    if (durationSeconds < 0.1) durationSeconds = 60.0;
                    fileLength = (long)(durationSeconds * _targetSampleRate * _targetChannels);
                    Logger.Info($"[MultiFileAudioSource] VGM 文件 {Path.GetFileName(filePath)}: {durationSeconds:F2}s (loop={_vgmLoopCount}, fade={_vgmFadeOutDuration}s)");
                }
                else
                {
                    // 其他格式：使用 FFmpeg 解码器获取长度
                    using var decoder = new FfmpegAudioDecoder(filePath, _targetSampleRate, _targetChannels);
                    fileLength = decoder.Length;
                }
                _fileOffsets.Add(_totalLength);
                _fileLengths.Add(fileLength);
                _totalLength += fileLength;
            }
            catch (Exception ex)
            {
                // 无法读取的文件跳过
                Logger.Warning($"[音频] 无法读取 {Path.GetFileName(filePath)}: {ex.Message}");
                _fileOffsets.Add(_totalLength);
                _fileLengths.Add(0);
            }
        }
    }

    // 打开指定索引的文件（使用目标采样率和声道数）
    private void OpenFile(int index)
    {
        if (index < 0 || index >= _filePaths.Count)
            return;

        // 释放旧的音频源（防止 libvgm 等资源冲突）
        if (_currentSource != null)
        {
            try
            {
                Logger.Debug($"[MultiFileAudioSource] 准备释放旧音频源: {_currentSource.GetType().Name}");
                _currentSource.Dispose();
                Logger.Debug($"[MultiFileAudioSource] 已释放旧音频源");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MultiFileAudioSource] 释放旧音频源时出错: {ex.Message}\n{ex.StackTrace}");
            }
            _currentSource = null;
        }
        Logger.Debug($"[MultiFileAudioSource] 开始打开新文件: index={index}");

        string filePath = _filePaths[index];
        bool isMidi = index < _isMidiFile.Count && _isMidiFile[index];
        bool isVgm = index < _isVgmFile.Count && _isVgmFile[index];
        
        try
        {
            if (isMidi)
            {
                // MIDI 文件：使用 FluidSynth 渲染
                Logger.Info($"[MultiFileAudioSource] 打开 MIDI: {Path.GetFileName(filePath)}");
                _currentSource = MidiAudioSourceFactory.Create(filePath, _targetSampleRate, _targetChannels);
            }
            else if (isVgm)
            {
                // VGM 文件：使用原生 44100Hz 渲染
                Logger.Info($"[MultiFileAudioSource] 打开 VGM: {Path.GetFileName(filePath)}");
                var vgmSource = new VgmAudioSource(44100);  // 始终使用原生 44100Hz
                vgmSource.LoopCount = _vgmLoopCount;
                vgmSource.FadeOutEnabled = _vgmFadeOutEnabled;
                vgmSource.FadeOutDuration = _vgmFadeOutDuration;
                if (vgmSource.LoadFile(filePath))
                {
                    // 如果目标采样率与原生不同，使用重采样包装器
                    if (_targetSampleRate != 44100)
                    {
                        Logger.Debug($"[MultiFileAudioSource] VGM 重采样: 44100Hz -> {_targetSampleRate}Hz");
                        _currentSource = new VgmResamplingSource(vgmSource, _targetSampleRate, _targetChannels);
                    }
                    else
                    {
                        _currentSource = vgmSource;
                    }
                    Logger.Debug($"[MultiFileAudioSource] VGM 打开完成");
                }
                else
                {
                    Logger.Error($"[MultiFileAudioSource] VGM 加载失败: {filePath}");
                    throw new Exception("VGM 文件加载失败");
                }
            }
            else
            {
                // 其他格式：使用 FFmpeg 解码器
                _currentSource = new FfmpegAudioDecoder(filePath, _targetSampleRate, _targetChannels);
            }
            _currentFileIndex = index;
            _fileChangeVersion++;
        }
        catch (Exception ex)
        {
            Logger.Error($"[音频] 无法打开 {Path.GetFileName(filePath)}: {ex.Message}");
            throw;
        }
    }

    // 获取指定全局位置对应的文件索引
    private int GetFileIndexForPosition(long position)
    {
        for (int i = 0; i < _filePaths.Count; i++)
        {
            if (i < _fileOffsets.Count && i < _fileLengths.Count)
            {
                long fileStart = _fileOffsets[i];
                long fileEnd = fileStart + _fileLengths[i];
                if (position >= fileStart && position < fileEnd)
                    return i;
            }
        }
        return _filePaths.Count - 1;
    }

    public bool CanSeek => true;

    public WaveFormat WaveFormat => _waveFormat;

    public long Position
    {
        get => _position;
        set
        {
            if (value < 0) value = 0;
            if (value > _totalLength) value = _totalLength;

            _position = value;

            // 找到对应的文件
            int targetIndex = GetFileIndexForPosition(value);
            if (targetIndex != _currentFileIndex)
            {
                OpenFile(targetIndex);
            }

            // 设置文件内的位置
            if (_currentSource != null && targetIndex < _fileOffsets.Count)
            {
                long localPosition = value - _fileOffsets[targetIndex];
                if (localPosition >= 0 && localPosition <= _currentSource.Length)
                {
                    _currentSource.Position = localPosition;
                }
            }
        }
    }

    public long Length => _totalLength;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_isDisposed || _currentSource == null)
            return 0;

        int totalRead = 0;

        while (totalRead < count && _position < _totalLength)
        {
            int read = _currentSource.Read(buffer, offset + totalRead, count - totalRead);

            if (read > 0)
            {
                totalRead += read;
                _position += read;
            }
            else
            {
                // Read 返回 0，检查是否真的到达文件末尾
                // 对于 FfmpegAudioDecoder 使用 IsEof 属性，对于 MIDI 使用 Position >= Length
                bool isEof = false;
                if (_currentSource is FfmpegAudioDecoder decoder)
                {
                    isEof = decoder.IsEof;
                }
                else if (_currentSource is MidiAudioSource midiSource)
                {
                    isEof = midiSource.IsEof;
                }
                else if (_currentSource is VgmResamplingSource vgmResample)
                {
                    // VGM 重采样源：使用 IsEof 属性
                    isEof = vgmResample.IsEof;
                }
                else if (_currentSource is VgmAudioSource vgmDirect)
                {
                    // VGM 直接源：使用 IsEof 属性
                    isEof = vgmDirect.IsEof;
                }
                else
                {
                    // 其他类型：使用位置判断
                    isEof = _currentSource.Position >= _currentSource.Length;
                }
                
                if (!isEof)
                {
                    // 还没到文件末尾，只是无法读取完整声道数据
                    Logger.Debug($"[MultiFileAudioSource] Read=0 但 IsEof=false, pos={_currentSource.Position}, len={_currentSource.Length}");
                    break;
                }
                
                // 真的到达文件末尾，切换到下一个文件
                if (_currentFileIndex < _filePaths.Count - 1)
                {
                    int nextIndex = _currentFileIndex + 1;
                    Logger.Info($"[MultiFileAudioSource] 切换到文件 {nextIndex + 1}/{_filePaths.Count}: {Path.GetFileName(_filePaths[nextIndex])}");
                    
                    // 记录当前位置用于调试
                    long expectedPosition = _fileOffsets[nextIndex];
                    Logger.Debug($"[MultiFileAudioSource] 切换前: _position={_position}, 期望={expectedPosition}, 差值={_position - expectedPosition}");
                    
                    // 打开新文件（包含释放旧文件和创建新播放器）
                    try
                    {
                        Logger.Debug($"[MultiFileAudioSource] 调用 OpenFile({nextIndex})...");
                        OpenFile(nextIndex);
                        Logger.Debug($"[MultiFileAudioSource] OpenFile 完成");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MultiFileAudioSource] OpenFile 失败: {ex.Message}\n{ex.StackTrace}");
                        throw;
                    }
                    
                    // 重要：重置位置到新文件的起始偏移量（避免累积误差）
                    _position = expectedPosition;
                }
                else
                {
                    Logger.Debug("[MultiFileAudioSource] 已到达最后一个文件末尾");
                    break;
                }
            }
        }

        return totalRead;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _currentSource?.Dispose();
            _isDisposed = true;
        }
    }
    
    // 检测是否为 VGM 文件
    private static bool IsVgmFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".vgm" || ext == ".vgz";
    }
}

// VGM 重采样包装器：将 VgmAudioSource (44100Hz 2ch) 重采样到目标格式
public class VgmResamplingSource : ISampleSource
{
    private readonly VgmAudioSource _vgmSource;
    private readonly WaveFormat _targetFormat;
    private readonly double _resampleRatio;  // 源采样率 / 目标采样率
    private readonly float[] _sourceBuffer;
    private int _sourceBufferLength;
    private int _sourceBufferPos;
    private double _fractionalPos;
    private float _prevL, _prevR;  // 前一个采样（用于线性插值）
    private bool _isDisposed;
    
    public VgmResamplingSource(VgmAudioSource source, int targetSampleRate, int targetChannels)
    {
        _vgmSource = source;
        _targetFormat = new WaveFormat(targetSampleRate, 32, targetChannels, AudioEncoding.IeeeFloat);
        _resampleRatio = (double)source.WaveFormat.SampleRate / targetSampleRate;
        _sourceBuffer = new float[16384];
        _sourceBufferLength = 0;
        _sourceBufferPos = 0;
        _fractionalPos = 0;
        _prevL = 0;
        _prevR = 0;
    }
    
    public bool CanSeek => _vgmSource.CanSeek;
    public WaveFormat WaveFormat => _targetFormat;
    public VgmAudioSource VgmSource => _vgmSource;
    
    public long Length
    {
        get
        {
            long sourceLength = _vgmSource.Length;
            return (long)(sourceLength / _resampleRatio);
        }
    }
    
    public long Position
    {
        get => (long)(_vgmSource.Position / _resampleRatio);
        set => _vgmSource.Position = (long)(value * _resampleRatio);
    }
    
    public bool IsEof => _isDisposed || _vgmSource.IsEof;
    
    public int Read(float[] buffer, int offset, int count)
    {
        if (_isDisposed) return 0;
        
        int samplesWritten = 0;
        int channels = _targetFormat.Channels;
        
        while (samplesWritten < count)
        {
            // 确保源缓冲区有数据
            if (_sourceBufferPos >= _sourceBufferLength)
            {
                _sourceBufferLength = _vgmSource.Read(_sourceBuffer, 0, _sourceBuffer.Length);
                _sourceBufferPos = 0;
                if (_sourceBufferLength == 0) break;
            }
            
            // 获取当前和下一个采样（用于线性插值）
            int idx = _sourceBufferPos;
            float curL = _sourceBuffer[idx];
            float curR = _sourceBuffer[idx + 1];
            
            // 下一个采样（如果有）
            float nextL = curL, nextR = curR;
            if (idx + 2 < _sourceBufferLength)
            {
                nextL = _sourceBuffer[idx + 2];
                nextR = _sourceBuffer[idx + 3];
            }
            
            // 线性插值输出
            while (_fractionalPos < 1.0 && samplesWritten < count)
            {
                float t = (float)_fractionalPos;
                float outL = curL + (nextL - curL) * t;
                float outR = curR + (nextR - curR) * t;
                
                if (channels == 2)
                {
                    buffer[offset + samplesWritten++] = outL;
                    buffer[offset + samplesWritten++] = outR;
                }
                else if (channels == 1)
                {
                    buffer[offset + samplesWritten++] = (outL + outR) * 0.5f;
                }
                
                _fractionalPos += _resampleRatio;
            }
            
            // 移动到下一个源采样
            while (_fractionalPos >= 1.0)
            {
                _fractionalPos -= 1.0;
                _sourceBufferPos += 2;
                if (_sourceBufferPos >= _sourceBufferLength) break;
            }
        }
        
        return samplesWritten;
    }
    
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _vgmSource?.Dispose();
            _isDisposed = true;
        }
    }
}
