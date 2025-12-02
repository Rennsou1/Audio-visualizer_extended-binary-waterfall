using System;
using System.Collections.Generic;
using System.IO;
using CSCore;
using CSCore.Codecs;
using CSCore.MediaFoundation;

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

        // 计算所有文件的总长度（使用临时解码器）
        CalculateTotalLength();
        
        // 重新打开第一个文件，确保从头开始读取
        // 某些解码器在 CalculateTotalLength 期间可能会被影响
        OpenFile(0);
        if (_currentSampleSource.CanSeek)
        {
            _currentSampleSource.Position = 0;
        }
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
                // 安全转换为 ISampleSource（支持 ADPCM 等特殊格式）
                using var tempSample = SafeToSampleSource(tempSource, filePath);
                
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
        // 安全转换为 ISampleSource（支持 ADPCM 等特殊格式）
        _currentSampleSource = SafeToSampleSource(_currentWaveSource, _filePaths[index]);
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

    // 用于限制日志输出的标志（只在第一次切换时打印）
    private bool _firstReadLogged = false;
    private bool _switchLogged = false;
    
    public int Read(float[] buffer, int offset, int count)
    {
        if (_isDisposed || _currentSampleSource == null)
            return 0;

        int totalRead = 0;
        
        // 诊断：第一次读取时打印状态
        if (!_firstReadLogged)
        {
            _firstReadLogged = true;
            Logger.Info($"[MultiFileAudioSource] 首次读取: fileIndex={_currentFileIndex}, position={_position}, totalLength={_totalLength}, sourcePos={_currentSampleSource.Position}, sourceLen={_currentSampleSource.Length}");
        }

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
                // Read 返回 0，检查是否真的到达文件末尾
                bool fileEnded = _currentSampleSource.Position >= _currentSampleSource.Length;
                
                if (fileEnded)
                {
                    // 当前文件确实读完，切换到下一个
                    if (_currentFileIndex < _filePaths.Count - 1)
                    {
                        // 诊断：打印切换信息（只打印一次）
                        if (!_switchLogged)
                        {
                            _switchLogged = true;
                            Logger.Info($"[MultiFileAudioSource] 切换文件: {_currentFileIndex} -> {_currentFileIndex + 1}, position={_position}, sourcePos={_currentSampleSource.Position}, sourceLen={_currentSampleSource.Length}");
                        }
                        OpenFile(_currentFileIndex + 1);
                    }
                    else
                    {
                        // 所有文件都读完了
                        break;
                    }
                }
                else
                {
                    // 文件没读完但 Read 返回 0，可能是解码器内部缓冲问题
                    // 尝试 Seek 到当前位置来"唤醒"解码器
                    if (_currentSampleSource.CanSeek)
                    {
                        long currentPos = _currentSampleSource.Position;
                        _currentSampleSource.Position = currentPos;
                        
                        // 再次尝试读取
                        read = _currentSampleSource.Read(buffer, offset + totalRead, count - totalRead);
                        if (read > 0)
                        {
                            totalRead += read;
                            _position += read;
                            continue; // 成功读取，继续循环
                        }
                    }
                    
                    // Seek 后仍然返回 0，记录警告并结束当前读取循环
                    if (!_switchLogged)
                    {
                        _switchLogged = true;
                        Logger.Warning($"[MultiFileAudioSource] Read 返回 0 但文件未结束: pos={_currentSampleSource.Position}, len={_currentSampleSource.Length}");
                    }
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
    
    // 安全地将 IWaveSource 转换为 ISampleSource，支持更多音频格式
    private static ISampleSource SafeToSampleSource(IWaveSource waveSource, string filePath)
    {
        var wf = waveSource.WaveFormat;
        // 记录音频格式信息（使用 Info 确保 Release 版本也输出）
        string fileName = Path.GetFileName(filePath);
        Logger.Info($"[音频] {fileName}: SampleRate={wf.SampleRate}, Channels={wf.Channels}, Bits={wf.BitsPerSample}, Tag={wf.WaveFormatTag}");
        
        try
        {
            // 首先尝试直接转换
            var result = waveSource.ToSampleSource();
            Logger.Info($"[音频] {fileName}: 转换成功 (直接)");
            return result;
        }
        catch (NotSupportedException ex)
        {
            // 格式不支持时，尝试使用 MediaFoundationDecoder 重新解码
            Logger.Warning($"[音频] {fileName}: 直接转换失败 ({ex.Message})，尝试 MediaFoundation");
            try
            {
                waveSource.Dispose();
                var mfDecoder = new MediaFoundationDecoder(filePath);
                var mfWf = mfDecoder.WaveFormat;
                Logger.Info($"[音频] {fileName}: MF 格式: SampleRate={mfWf.SampleRate}, Channels={mfWf.Channels}, Bits={mfWf.BitsPerSample}");
                var result = mfDecoder.ToSampleSource();
                Logger.Info($"[音频] {fileName}: 转换成功 (MediaFoundation)");
                return result;
            }
            catch (Exception mfEx)
            {
                Logger.Error($"[音频] {fileName}: MediaFoundation 也失败: {mfEx.Message}");
                throw;
            }
        }
    }
}
