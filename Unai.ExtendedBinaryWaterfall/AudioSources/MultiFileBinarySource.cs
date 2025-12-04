using System;
using System.Collections.Generic;
using System.IO;

namespace Unai.ExtendedBinaryWaterfall;

// 多文件二进制数据源：直接按顺序读取多个文件的原始字节数据（用于瀑布可视化）
// 这个类简单地将多个文件视为一个连续的字节流
public class MultiFileBinarySource : IDisposable
{
    private readonly List<string> _filePaths;
    private readonly List<long> _fileByteLengths;    // 每个文件的实际字节长度
    private readonly List<long> _fileByteOffsets;    // 每个文件的起始字节偏移
    private int _currentFileIndex = 0;
    private FileStream _currentStream;
    private long _totalByteLength;
    private long _position;
    private bool _isDisposed;

    // 当前正在读取的文件索引
    public int CurrentFileIndex => _currentFileIndex;
    
    // 文件总数
    public int FileCount => _filePaths.Count;
    
    // 总字节长度（所有文件实际大小之和）
    public long TotalLength => _totalByteLength;

    public MultiFileBinarySource(List<string> filePaths, int bytesPerSecond)
    {
        _filePaths = filePaths ?? throw new ArgumentNullException(nameof(filePaths));
        _fileByteLengths = new List<long>();
        _fileByteOffsets = new List<long>();
        
        if (_filePaths.Count == 0)
            throw new ArgumentException("至少需要一个文件", nameof(filePaths));

        // 计算所有文件的总长度（使用实际文件大小）
        CalculateTotalLength();
        
        // 打开第一个文件
        if (_filePaths.Count > 0)
        {
            OpenFile(0);
        }
    }

    // 计算所有文件的总字节长度（直接使用文件实际大小）
    private void CalculateTotalLength()
    {
        _totalByteLength = 0;
        _fileByteLengths.Clear();
        _fileByteOffsets.Clear();

        foreach (var filePath in _filePaths)
        {
            _fileByteOffsets.Add(_totalByteLength);
            
            if (File.Exists(filePath))
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    long fileLength = fileInfo.Length;
                    _fileByteLengths.Add(fileLength);
                    _totalByteLength += fileLength;
                }
                catch
                {
                    _fileByteLengths.Add(0);
                }
            }
            else
            {
                _fileByteLengths.Add(0);
            }
        }
    }

    // 打开指定索引的文件
    private void OpenFile(int index)
    {
        if (index < 0 || index >= _filePaths.Count)
            return;

        // 关闭当前文件
        _currentStream?.Dispose();
        _currentStream = null;

        // 打开新文件
        if (File.Exists(_filePaths[index]))
        {
            _currentStream = File.OpenRead(_filePaths[index]);
        }
        _currentFileIndex = index;
    }

    // 获取指定全局字节位置对应的文件索引
    private int GetFileIndexForPosition(long position)
    {
        for (int i = 0; i < _filePaths.Count; i++)
        {
            if (i < _fileByteOffsets.Count && i < _fileByteLengths.Count)
            {
                long fileStart = _fileByteOffsets[i];
                long fileEnd = fileStart + _fileByteLengths[i];
                if (position >= fileStart && position < fileEnd)
                    return i;
            }
        }
        // 如果超出范围，返回最后一个文件
        return Math.Max(0, _filePaths.Count - 1);
    }

    // 获取文件内的本地位置（直接计算，不做比例转换）
    private long GetLocalPosition(int fileIndex, long globalPosition)
    {
        if (fileIndex < 0 || fileIndex >= _fileByteOffsets.Count)
            return 0;
        return globalPosition - _fileByteOffsets[fileIndex];
    }

    // 当前全局位置
    public long Position
    {
        get => _position;
        set
        {
            if (value < 0) value = 0;
            if (value > _totalByteLength) value = _totalByteLength;

            _position = value;

            // 找到对应的文件
            int targetIndex = GetFileIndexForPosition(value);
            if (targetIndex != _currentFileIndex)
            {
                OpenFile(targetIndex);
            }

            // 设置文件内的位置
            if (_currentStream != null)
            {
                long localPosition = GetLocalPosition(targetIndex, value);
                localPosition = Math.Clamp(localPosition, 0, _currentStream.Length);
                _currentStream.Position = localPosition;
            }
        }
    }

    // 从当前位置读取指定数量的字节
    public int Read(byte[] buffer, int offset, int count)
    {
        if (_isDisposed)
            return 0;

        int totalRead = 0;

        while (totalRead < count && _position < _totalByteLength)
        {
            // 确保打开正确的文件
            int targetIndex = GetFileIndexForPosition(_position);
            if (targetIndex != _currentFileIndex || _currentStream == null)
            {
                OpenFile(targetIndex);
                if (_currentStream != null)
                {
                    long localPos = GetLocalPosition(targetIndex, _position);
                    _currentStream.Position = Math.Clamp(localPos, 0, _currentStream.Length);
                }
            }

            if (_currentStream == null)
            {
                // 当前文件无法打开，跳过到下一个文件
                if (_currentFileIndex < _filePaths.Count - 1)
                {
                    _position = _fileByteOffsets[_currentFileIndex + 1];
                    continue;
                }
                break;
            }

            int read = _currentStream.Read(buffer, offset + totalRead, count - totalRead);

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
                    _position = _fileByteOffsets[_currentFileIndex + 1];
                }
                else
                {
                    break;
                }
            }
        }

        return totalRead;
    }

    // 从指定全局位置读取指定数量的字节（不改变当前位置状态）
    public int ReadAt(long globalPosition, byte[] buffer, int offset, int count)
    {
        if (_isDisposed || globalPosition < 0 || globalPosition >= _totalByteLength)
            return 0;

        // 找到起始文件
        int fileIndex = GetFileIndexForPosition(globalPosition);
        int totalRead = 0;
        long currentPos = globalPosition;

        while (totalRead < count && fileIndex < _filePaths.Count)
        {
            if (fileIndex >= _fileByteLengths.Count)
                break;

            long fileStart = _fileByteOffsets[fileIndex];
            long fileLength = _fileByteLengths[fileIndex];
            long localPos = currentPos - fileStart;

            if (localPos >= fileLength)
            {
                // 移动到下一个文件
                fileIndex++;
                if (fileIndex < _fileByteOffsets.Count)
                    currentPos = _fileByteOffsets[fileIndex];
                continue;
            }

            // 打开文件读取
            try
            {
                using var stream = File.OpenRead(_filePaths[fileIndex]);
                stream.Position = localPos;
                
                // 计算本次最多能读多少
                long remainingInFile = fileLength - localPos;
                int toRead = (int)Math.Min(count - totalRead, remainingInFile);
                
                int read = stream.Read(buffer, offset + totalRead, toRead);
                if (read > 0)
                {
                    totalRead += read;
                    currentPos += read;
                }
                else
                {
                    // 文件读完了
                    fileIndex++;
                    if (fileIndex < _fileByteOffsets.Count)
                        currentPos = _fileByteOffsets[fileIndex];
                }
            }
            catch
            {
                // 读取失败，跳到下一个文件
                fileIndex++;
                if (fileIndex < _fileByteOffsets.Count)
                    currentPos = _fileByteOffsets[fileIndex];
            }
        }

        return totalRead;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _currentStream?.Dispose();
            _isDisposed = true;
        }
    }
}
