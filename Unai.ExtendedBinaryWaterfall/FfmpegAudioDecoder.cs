using System;
using System.IO;
using System.Runtime.InteropServices;
using CSCore;
using FFmpeg.AutoGen;

namespace Unai.ExtendedBinaryWaterfall;

// 基于 FFmpeg 的音频解码器，实现 ISampleSource 接口
// 支持任何 FFmpeg 支持的音频格式，包括任意采样率、位深、声道数
// 使用 SwrContext 统一处理所有格式转换，确保稳定可靠
public unsafe class FfmpegAudioDecoder : ISampleSource
{
    private readonly string _filePath;
    private AVFormatContext* _formatCtx;
    private AVCodecContext* _codecCtx;
    private AVFrame* _frame;
    private AVFrame* _convertedFrame;   // 转换后的帧（interleaved float）
    private AVPacket* _packet;
    private SwrContext* _swrCtx;
    private int _audioStreamIndex = -1;
    
    private WaveFormat _waveFormat;
    private long _length;
    private long _position;
    private bool _isDisposed;
    private int _channels;
    private int _sampleRate;
    
    // 简单线性缓冲区（取代复杂的环形缓冲区）
    private float[] _buffer;
    private int _bufferOffset;      // 当前读取位置
    private int _bufferLength;      // 缓冲区中有效数据长度
    private bool _eof = false;
    
    // FFmpeg 初始化标志
    private static bool _ffmpegInitialized = false;
    private static readonly object _initLock = new object();
    
    public FfmpegAudioDecoder(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        
        if (!File.Exists(filePath))
            throw new FileNotFoundException("音频文件不存在", filePath);
        
        InitializeFfmpeg();
        OpenFile();
    }
    
    // 初始化 FFmpeg 库
    private void InitializeFfmpeg()
    {
        lock (_initLock)
        {
            if (_ffmpegInitialized) return;
            
            var ffmpegPath = FfmpegUtils.GetFfmpegLibraryPath();
            if (!string.IsNullOrEmpty(ffmpegPath))
            {
                ffmpeg.RootPath = ffmpegPath;
            }
            
            _ffmpegInitialized = true;
        }
    }
    
    // 打开音频文件
    private void OpenFile()
    {
        // 打开输入文件
        AVFormatContext* fmtCtx = null;
        int ret = ffmpeg.avformat_open_input(&fmtCtx, _filePath, null, null);
        if (ret < 0)
        {
            throw new InvalidOperationException($"无法打开音频文件: {GetErrorMessage(ret)}");
        }
        _formatCtx = fmtCtx;
        
        // 读取流信息
        ret = ffmpeg.avformat_find_stream_info(_formatCtx, null);
        if (ret < 0)
        {
            throw new InvalidOperationException($"无法读取流信息: {GetErrorMessage(ret)}");
        }
        
        // 查找音频流
        for (int i = 0; i < _formatCtx->nb_streams; i++)
        {
            if (_formatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                _audioStreamIndex = i;
                break;
            }
        }
        
        if (_audioStreamIndex < 0)
        {
            throw new InvalidOperationException("文件中没有音频流");
        }
        
        var audioStream = _formatCtx->streams[_audioStreamIndex];
        var codecPar = audioStream->codecpar;
        
        // 查找解码器
        var codec = ffmpeg.avcodec_find_decoder(codecPar->codec_id);
        if (codec == null)
        {
            throw new InvalidOperationException("找不到音频解码器");
        }
        
        // 分配解码器上下文
        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
        {
            throw new InvalidOperationException("无法分配解码器上下文");
        }
        
        // 复制编解码器参数
        ret = ffmpeg.avcodec_parameters_to_context(_codecCtx, codecPar);
        if (ret < 0)
        {
            throw new InvalidOperationException($"无法复制编解码器参数: {GetErrorMessage(ret)}");
        }
        
        // 启用多线程解码
        _codecCtx->thread_count = Math.Min(Environment.ProcessorCount, 4);
        _codecCtx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        
        // 打开解码器
        ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0)
        {
            throw new InvalidOperationException($"无法打开解码器: {GetErrorMessage(ret)}");
        }
        
        // 获取音频参数
        _channels = _codecCtx->ch_layout.nb_channels;
        if (_channels <= 0) _channels = 2;
        _sampleRate = _codecCtx->sample_rate;
        
        // 创建 WaveFormat（输出为 interleaved float）
        _waveFormat = new WaveFormat(_sampleRate, 32, _channels, AudioEncoding.IeeeFloat);
        
        // 计算总长度（采样数 × 声道数）
        long duration = audioStream->duration;
        if (duration <= 0 && _formatCtx->duration > 0)
        {
            var avTimeBase = new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
            duration = ffmpeg.av_rescale_q(_formatCtx->duration, avTimeBase, audioStream->time_base);
        }
        
        if (duration > 0)
        {
            _length = ffmpeg.av_rescale_q(duration, audioStream->time_base, 
                new AVRational { num = 1, den = _sampleRate }) * _channels;
        }
        else
        {
            _length = (long)(_formatCtx->duration / 1000000.0 * _sampleRate * _channels);
        }
        
        // 分配帧和包
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        
        // 分配转换后的帧
        _convertedFrame = ffmpeg.av_frame_alloc();
        _convertedFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT; // interleaved float
        _convertedFrame->ch_layout = _codecCtx->ch_layout;
        _convertedFrame->sample_rate = _sampleRate;
        _convertedFrame->nb_samples = 8192; // 初始大小
        ret = ffmpeg.av_frame_get_buffer(_convertedFrame, 0);
        if (ret < 0)
        {
            throw new InvalidOperationException($"无法分配转换帧缓冲区: {GetErrorMessage(ret)}");
        }
        
        // 初始化重采样器（统一转换为 interleaved float）
        InitializeResampler();
        
        // 初始化简单缓冲区（足够容纳多帧数据）
        _buffer = new float[_sampleRate * _channels * 2]; // 约 2 秒的数据
        _bufferOffset = 0;
        _bufferLength = 0;
        
        Logger.Info($"[FfmpegAudioDecoder] 打开文件: {Path.GetFileName(_filePath)}, " +
                   $"SampleRate={_sampleRate}, Channels={_channels}, Duration={TimeSpan.FromSeconds((double)_length / _channels / _sampleRate)}");
    }
    
    // 初始化重采样器：将任何输入格式转换为 interleaved float
    private void InitializeResampler()
    {
        // 使用 swr_alloc_set_opts2 一次性设置所有参数
        SwrContext* swrCtx = null;
        AVChannelLayout inLayout = _codecCtx->ch_layout;
        AVChannelLayout outLayout = _codecCtx->ch_layout;
        
        int ret = ffmpeg.swr_alloc_set_opts2(
            &swrCtx,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _sampleRate,  // 输出：interleaved float
            &inLayout, _codecCtx->sample_fmt, _sampleRate,              // 输入：原始格式
            0, null);
        
        if (ret < 0 || swrCtx == null)
        {
            throw new InvalidOperationException($"无法配置重采样器: {GetErrorMessage(ret)}");
        }
        
        _swrCtx = swrCtx;
        
        ret = ffmpeg.swr_init(_swrCtx);
        if (ret < 0)
        {
            throw new InvalidOperationException($"无法初始化重采样器: {GetErrorMessage(ret)}");
        }
    }
    
    public bool CanSeek => true;
    
    public WaveFormat WaveFormat => _waveFormat;
    
    public long Position
    {
        get => _position;
        set
        {
            if (value < 0) value = 0;
            if (value > _length) value = _length;
            
            // 转换采样位置为时间戳
            long samplePos = value / _channels;
            var audioStream = _formatCtx->streams[_audioStreamIndex];
            long timestamp = ffmpeg.av_rescale_q(samplePos, 
                new AVRational { num = 1, den = _sampleRate }, audioStream->time_base);
            
            int ret = ffmpeg.av_seek_frame(_formatCtx, _audioStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (ret >= 0)
            {
                ffmpeg.avcodec_flush_buffers(_codecCtx);
                _position = value;
                // 清空缓冲区和状态
                _bufferOffset = 0;
                _bufferLength = 0;
                _eof = false;
                _flushSent = false;
            }
        }
    }
    
    public long Length => _length;
    
    public int Read(float[] buffer, int offset, int count)
    {
        if (_isDisposed) return 0;
        
        int totalRead = 0;
        
        while (totalRead < count)
        {
            // 从缓冲区读取
            int available = _bufferLength - _bufferOffset;
            if (available > 0)
            {
                int toCopy = Math.Min(count - totalRead, available);
                // 确保是声道数的整数倍
                toCopy = (toCopy / _channels) * _channels;
                
                if (toCopy > 0)
                {
                    Array.Copy(_buffer, _bufferOffset, buffer, offset + totalRead, toCopy);
                    _bufferOffset += toCopy;
                    totalRead += toCopy;
                    _position += toCopy;
                }
                else
                {
                    // 剩余请求数 < 声道数，无法再复制完整的声道数据
                    // 退出循环，返回已读取的数据
                    break;
                }
            }
            
            // 如果缓冲区为空，解码更多数据
            if (_bufferOffset >= _bufferLength)
            {
                if (_eof)
                {
                    break;
                }
                
                // 解码新数据到缓冲区
                if (!FillBuffer())
                {
                    _eof = true;
                    break;
                }
            }
        }
        
        return totalRead;
    }
    
    // 是否已经发送了 EOF 包
    private bool _flushSent = false;
    
    // 解码数据填充缓冲区
    private bool FillBuffer()
    {
        _bufferOffset = 0;
        _bufferLength = 0;
        
        // 解码数据直到缓冲区有足够数据或 EOF
        int targetSamples = _buffer.Length / 2;
        int loopCount = 0;
        
        while (_bufferLength < targetSamples)
        {
            loopCount++;
            int prevLength = _bufferLength;
            int result = DecodeUntilFrame();
            
            if (result < 0)
            {
                break;
            }
            
            // 如果没有新数据写入，说明缓冲区可能满了或有问题，退出避免无限循环
            if (_bufferLength == prevLength)
            {
                break;
            }
            
            // 防止无限循环
            if (loopCount > 10000)
            {
                Logger.Error("[FfmpegAudioDecoder] FillBuffer 循环次数过多，强制退出");
                break;
            }
        }
        
        return _bufferLength > 0;
    }
    
    // 解码直到获取到至少一帧数据，返回值：>0 成功解码，0 没有解码但可继续，<0 EOF/错误
    private int DecodeUntilFrame()
    {
        const int maxIterations = 1000;
        int iterations = 0;
        
        while (iterations++ < maxIterations)
        {
            // 尝试从解码器获取帧
            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            
            if (ret == 0)
            {
                // 成功获取帧，转换并存储
                return ConvertAndBuffer();
            }
            
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // 需要发送更多数据，读取下一个 packet
                if (!SendNextPacket())
                {
                    // 文件结束，排空解码器
                    if (DrainDecoderOnce())
                    {
                        return 1;
                    }
                    return -1;
                }
                continue;
            }
            
            // AVERROR_EOF 或其他错误
            return -1;
        }
        
        Logger.Warning("[FfmpegAudioDecoder] 达到最大解码迭代次数");
        return -1;
    }
    
    // 读取并发送一个 packet，返回 true 表示成功，false 表示 EOF
    private bool SendNextPacket()
    {
        while (true)
        {
            int ret = ffmpeg.av_read_frame(_formatCtx, _packet);
            
            if (ret < 0)
            {
                // 文件结束，发送 flush 包
                if (!_flushSent)
                {
                    _flushSent = true;
                    ffmpeg.avcodec_send_packet(_codecCtx, null);
                }
                return false;
            }
            
            if (_packet->stream_index != _audioStreamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }
            
            // 发送 packet 到解码器
            ret = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            ffmpeg.av_packet_unref(_packet);
            
            // 无论成功与否都返回 true，让调用者继续尝试 receive
            return true;
        }
    }
    
    // 从解码器排空一帧，返回 true 表示成功获取帧
    private bool DrainDecoderOnce()
    {
        int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
        if (ret == 0)
        {
            ConvertAndBuffer();
            return true;
        }
        return false;
    }
    
    // 将解码的帧转换为 interleaved float 并写入缓冲区
    private int ConvertAndBuffer()
    {
        int nbSamples = _frame->nb_samples;
        int totalFloats = nbSamples * _channels;
        
        // 确保缓冲区有足够空间
        if (_bufferLength + totalFloats > _buffer.Length)
        {
            // 缓冲区已满
            ffmpeg.av_frame_unref(_frame);
            return 0;
        }
        
        // 确保转换帧有足够空间
        if (_convertedFrame->nb_samples < nbSamples)
        {
            ffmpeg.av_frame_unref(_convertedFrame);
            _convertedFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
            _convertedFrame->ch_layout = _codecCtx->ch_layout;
            _convertedFrame->sample_rate = _sampleRate;
            _convertedFrame->nb_samples = nbSamples + 256; // 预留空间
            int ret = ffmpeg.av_frame_get_buffer(_convertedFrame, 0);
            if (ret < 0)
            {
                ffmpeg.av_frame_unref(_frame);
                return 0;
            }
        }
        
        // 使用 swr_convert 进行格式转换
        byte** outData = (byte**)&_convertedFrame->data;
        int converted = ffmpeg.swr_convert(_swrCtx, outData, nbSamples, _frame->extended_data, nbSamples);
        
        if (converted > 0)
        {
            // 复制转换后的数据到缓冲区
            // AV_SAMPLE_FMT_FLT 是 interleaved 格式，数据在 data[0]
            float* srcPtr = (float*)_convertedFrame->data[0];
            int floatsToCopy = converted * _channels;
            
            for (int i = 0; i < floatsToCopy; i++)
            {
                _buffer[_bufferLength + i] = srcPtr[i];
            }
            _bufferLength += floatsToCopy;
        }
        
        ffmpeg.av_frame_unref(_frame);
        return converted > 0 ? converted * _channels : 0;
    }
    
    // 获取 FFmpeg 错误消息
    private static string GetErrorMessage(int errorCode)
    {
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(errorCode, buffer, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Error {errorCode}";
    }
    
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        if (_swrCtx != null)
        {
            fixed (SwrContext** ptr = &_swrCtx)
            {
                ffmpeg.swr_free(ptr);
            }
        }
        
        if (_convertedFrame != null)
        {
            var frame = _convertedFrame;
            ffmpeg.av_frame_free(&frame);
            _convertedFrame = null;
        }
        
        if (_frame != null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }
        
        if (_packet != null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }
        
        if (_codecCtx != null)
        {
            var ctx = _codecCtx;
            ffmpeg.avcodec_free_context(&ctx);
            _codecCtx = null;
        }
        
        if (_formatCtx != null)
        {
            var fmt = _formatCtx;
            ffmpeg.avformat_close_input(&fmt);
            _formatCtx = null;
        }
    }
}
