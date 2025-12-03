using System;
using System.Collections.Generic;
using System.IO;
using CSCore;

namespace Unai.ExtendedBinaryWaterfall;

// 多文件音频源：支持顺序读取多个音频文件，自动切换到下一个文件
// 使用 FFmpeg 解码器，支持任何 FFmpeg 支持的音频格式
// 所有文件会被重采样到统一的目标采样率和声道数
public class MultiFileAudioSource : ISampleSource
{
    private readonly List<string> _filePaths;
    private readonly List<long> _fileLengths;      // 每个文件的采样数长度（基于目标采样率）
    private readonly List<long> _fileOffsets;      // 每个文件的起始偏移量
    private int _currentFileIndex = 0;
    private FfmpegAudioDecoder _currentDecoder;    // 使用 FFmpeg 解码器
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
        
        if (_filePaths.Count == 0)
            throw new ArgumentException("至少需要一个音频文件", nameof(filePaths));

        // 先打开第一个文件获取默认格式（不指定目标参数）
        using (var firstDecoder = new FfmpegAudioDecoder(_filePaths[0]))
        {
            // 如果未指定目标采样率/声道数，使用第一个文件的格式
            _targetSampleRate = targetSampleRate > 0 ? targetSampleRate : firstDecoder.WaveFormat.SampleRate;
            _targetChannels = targetChannels > 0 ? targetChannels : firstDecoder.WaveFormat.Channels;
        }
        
        // 创建统一的 WaveFormat（所有文件都会重采样到这个格式）
        _waveFormat = new WaveFormat(_targetSampleRate, 32, _targetChannels, AudioEncoding.IeeeFloat);
        
        Logger.Info($"[MultiFileAudioSource] 目标格式: {_targetSampleRate}Hz {_targetChannels}ch (用户导出设置)");

        // 计算所有文件的总长度（基于目标采样率）
        CalculateTotalLength();
        
        // 打开第一个文件（使用目标参数）
        OpenFile(0);
        if (_currentDecoder.CanSeek)
        {
            _currentDecoder.Position = 0;
        }
    }

    // 计算所有文件的总采样数（基于目标采样率）
    private void CalculateTotalLength()
    {
        _totalLength = 0;
        _fileLengths.Clear();
        _fileOffsets.Clear();

        foreach (var filePath in _filePaths)
        {
            if (!File.Exists(filePath)) continue;

            try
            {
                // 使用 FFmpeg 解码器获取文件信息（传入目标参数以获得正确的重采样后长度）
                using var decoder = new FfmpegAudioDecoder(filePath, _targetSampleRate, _targetChannels);
                long fileLength = decoder.Length;
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

        // 关闭当前解码器
        _currentDecoder?.Dispose();

        string filePath = _filePaths[index];
        
        try
        {
            // 传入目标参数，确保所有文件输出统一的采样率和声道数
            _currentDecoder = new FfmpegAudioDecoder(filePath, _targetSampleRate, _targetChannels);
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
            if (_currentDecoder != null && targetIndex < _fileOffsets.Count)
            {
                long localPosition = value - _fileOffsets[targetIndex];
                if (localPosition >= 0 && localPosition <= _currentDecoder.Length)
                {
                    _currentDecoder.Position = localPosition;
                }
            }
        }
    }

    public long Length => _totalLength;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_isDisposed || _currentDecoder == null)
            return 0;

        int totalRead = 0;

        while (totalRead < count && _position < _totalLength)
        {
            int read = _currentDecoder.Read(buffer, offset + totalRead, count - totalRead);

            if (read > 0)
            {
                totalRead += read;
                _position += read;
            }
            else
            {
                // Read 返回 0，使用 IsEof 属性检查是否真的到达文件末尾
                // 这比比较 Position 和 Length 更可靠（避免重采样导致的长度误差）
                if (!_currentDecoder.IsEof)
                {
                    // 还没到文件末尾，只是无法读取完整声道数据（剩余请求数 < 声道数）
                    Logger.Debug($"[MultiFileAudioSource] Read=0 但 IsEof=false, pos={_currentDecoder.Position}, len={_currentDecoder.Length}");
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
            _currentDecoder?.Dispose();
            _isDisposed = true;
        }
    }
}
