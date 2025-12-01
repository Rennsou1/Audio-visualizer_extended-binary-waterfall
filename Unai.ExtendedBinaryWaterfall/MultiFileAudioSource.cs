using System;
using System.Collections.Generic;
using System.IO;
using CSCore;
using CSCore.Codecs;

namespace Unai.ExtendedBinaryWaterfall;

// 多文件音频源：支持顺序读取多个音频文件，自动切换到下一个文件
public class MultiFileAudioSource : ISampleSource
{
    private readonly List<string> _filePaths;
    private readonly List<long> _fileLengths;      // 每个文件的采样数长度
    private readonly List<long> _fileOffsets;      // 每个文件的起始偏移量
    private int _currentFileIndex = 0;
    private IWaveSource _currentWaveSource;
    private ISampleSource _currentSampleSource;
    private WaveFormat _waveFormat;
    private long _totalLength;
    private long _position;
    private bool _isDisposed;

    // 当前正在播放的文件索引
    public int CurrentFileIndex => _currentFileIndex;
    
    // 文件总数
    public int FileCount => _filePaths.Count;

    public MultiFileAudioSource(List<string> filePaths)
    {
        _filePaths = filePaths ?? throw new ArgumentNullException(nameof(filePaths));
        _fileLengths = new List<long>();
        _fileOffsets = new List<long>();
        
        if (_filePaths.Count == 0)
            throw new ArgumentException("至少需要一个音频文件", nameof(filePaths));

        // 初始化第一个文件以获取格式信息
        OpenFile(0);
        _waveFormat = _currentSampleSource.WaveFormat;

        // 计算所有文件的总长度
        CalculateTotalLength();
    }

    // 计算所有文件的总采样数
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
                using var tempSource = CodecFactory.Instance.GetCodec(filePath);
                using var tempSample = tempSource.ToSampleSource();
                
                long fileLength = tempSample.Length;
                _fileOffsets.Add(_totalLength);
                _fileLengths.Add(fileLength);
                _totalLength += fileLength;
            }
            catch
            {
                // 无法读取的文件跳过
                _fileOffsets.Add(_totalLength);
                _fileLengths.Add(0);
            }
        }
    }

    // 打开指定索引的文件
    private void OpenFile(int index)
    {
        if (index < 0 || index >= _filePaths.Count)
            return;

        // 关闭当前文件
        _currentSampleSource?.Dispose();
        _currentWaveSource?.Dispose();

        // 打开新文件
        _currentWaveSource = CodecFactory.Instance.GetCodec(_filePaths[index]);
        _currentSampleSource = _currentWaveSource.ToSampleSource();
        _currentFileIndex = index;
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
            if (_currentSampleSource != null && targetIndex < _fileOffsets.Count)
            {
                long localPosition = value - _fileOffsets[targetIndex];
                if (localPosition >= 0 && localPosition <= _currentSampleSource.Length)
                {
                    _currentSampleSource.Position = localPosition;
                }
            }
        }
    }

    public long Length => _totalLength;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_isDisposed || _currentSampleSource == null)
            return 0;

        int totalRead = 0;

        while (totalRead < count && _position < _totalLength)
        {
            int read = _currentSampleSource.Read(buffer, offset + totalRead, count - totalRead);

            if (read > 0)
            {
                totalRead += read;
                _position += read;
            }
            else
            {
                // 当前文件读完，切换到下一个
                if (_currentFileIndex < _filePaths.Count - 1)
                {
                    OpenFile(_currentFileIndex + 1);
                }
                else
                {
                    // 所有文件都读完了
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
            _currentSampleSource?.Dispose();
            _currentWaveSource?.Dispose();
            _isDisposed = true;
        }
    }
}
