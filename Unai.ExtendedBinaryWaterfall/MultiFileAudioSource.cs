using System;
using System.Collections.Generic;
using System.IO;
using CSCore;
using Melanchall.DryWetMidi.Interaction;

namespace Unai.ExtendedBinaryWaterfall;

// 多文件音频源：支持顺序读取多个音频文件，自动切换到下一个文件
// 支持 FFmpeg 解码器和 MIDI (FluidSynth) 渲染
// 所有文件会被重采样到统一的目标采样率和声道数
public class MultiFileAudioSource : ISampleSource
{
    private readonly List<string> _filePaths;
    private readonly List<long> _fileLengths;      // 每个文件的采样数长度（基于目标采样率）
    private readonly List<long> _fileOffsets;      // 每个文件的起始偏移量
    private readonly List<bool> _isMidiFile;       // 每个文件是否为 MIDI 格式
    private int _currentFileIndex = 0;
    private ISampleSource _currentSource;          // 当前音频源（FFmpeg 或 MIDI）
    private WaveFormat _waveFormat;
    private long _totalLength;
    private long _position;
    private bool _isDisposed;
    
    // 目标采样率和声道数（用于统一所有文件的输出格式）
    private readonly int _targetSampleRate;
    private readonly int _targetChannels;

    // 当前正在播放的文件索引
    public int CurrentFileIndex => _currentFileIndex;
    
    // 文件总数
    public int FileCount => _filePaths.Count;

    // 构造函数：支持指定目标采样率和声道数
    // targetSampleRate <= 0 时使用第一个文件的采样率
    // targetChannels <= 0 时使用第一个文件的声道数
    public MultiFileAudioSource(List<string> filePaths, int targetSampleRate = 0, int targetChannels = 0)
    {
        _filePaths = filePaths ?? throw new ArgumentNullException(nameof(filePaths));
        _fileLengths = new List<long>();
        _fileOffsets = new List<long>();
        _isMidiFile = new List<bool>();
        
        if (_filePaths.Count == 0)
            throw new ArgumentException("至少需要一个音频文件", nameof(filePaths));

        // 确定目标格式：如果未指定，使用第一个非 MIDI 文件的格式，或默认值
        if (targetSampleRate <= 0 || targetChannels <= 0)
        {
            // 查找第一个非 MIDI 文件来获取格式
            foreach (var path in _filePaths)
            {
                if (!MidiAudioSourceFactory.IsMidiFile(path))
                {
                    using var firstDecoder = new FfmpegAudioDecoder(path);
                    targetSampleRate = targetSampleRate > 0 ? targetSampleRate : firstDecoder.WaveFormat.SampleRate;
                    targetChannels = targetChannels > 0 ? targetChannels : firstDecoder.WaveFormat.Channels;
                    break;
                }
            }
            // 如果全是 MIDI 文件，使用默认值
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

        foreach (var filePath in _filePaths)
        {
            if (!File.Exists(filePath)) continue;

            bool isMidi = MidiAudioSourceFactory.IsMidiFile(filePath);
            _isMidiFile.Add(isMidi);

            try
            {
                long fileLength;
                if (isMidi)
                {
                    // MIDI 文件：直接使用 DryWetMidi 计算时长
                    var midiFile = Melanchall.DryWetMidi.Core.MidiFile.Read(filePath);
                    var duration = midiFile.GetDuration<Melanchall.DryWetMidi.Interaction.MetricTimeSpan>();
                    double durationSeconds = duration.TotalMicroseconds / 1_000_000.0;
                    // 计算采样数：时长 * 采样率 * 声道数
                    fileLength = (long)(durationSeconds * _targetSampleRate * _targetChannels);
                    Logger.Debug($"[MultiFileAudioSource] MIDI 文件 {Path.GetFileName(filePath)}: {durationSeconds:F2}s, {fileLength} 采样");
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

        // 关闭当前音频源
        _currentSource?.Dispose();

        string filePath = _filePaths[index];
        bool isMidi = index < _isMidiFile.Count && _isMidiFile[index];
        
        try
        {
            if (isMidi)
            {
                // MIDI 文件：使用 FluidSynth 渲染
                Logger.Info($"[MultiFileAudioSource] 打开 MIDI: {Path.GetFileName(filePath)}");
                _currentSource = MidiAudioSourceFactory.Create(filePath, _targetSampleRate, _targetChannels);
            }
            else
            {
                // 其他格式：使用 FFmpeg 解码器
                _currentSource = new FfmpegAudioDecoder(filePath, _targetSampleRate, _targetChannels);
            }
            _currentFileIndex = index;
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
                    
                    // 打开新文件
                    OpenFile(nextIndex);
                    
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
}
