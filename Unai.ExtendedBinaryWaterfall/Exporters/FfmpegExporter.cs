using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Unai.ExtendedBinaryWaterfall.Exporters;

// 硬件加速类型枚举
public enum HardwareAccelType
{
	Auto,    // 自动检测可用的硬件加速
	None,    // 不使用硬件加速（纯软件编码）
	NVENC,   // NVIDIA NVENC
	QSV,     // Intel Quick Sync Video
	AMF      // AMD Advanced Media Framework
}

[Exporter("ffmpeg", "FFmpeg Stream", "Use FFmpeg libraries to encode audio and video data and output it in Matroska format.")]
public class FfmpegExporter : IExporter
{
	private bool _init = false;

	private unsafe AVFormatContext* _fmtCtx;

	private unsafe AVStream* _videoStream;
	private unsafe AVCodecContext* _videoCtx;
	private unsafe AVFrame* _videoAvFrame;
	private unsafe AVFrame* _videoAvFramePre;
	private unsafe AVPacket* _videoAvPacket;

	private unsafe AVStream* _audioStream;
	private unsafe AVCodecContext* _audioCtx;
	private unsafe AVFrame* _audioAvFrame;
	private unsafe AVPacket* _audioAvPacket;

	private unsafe SwsContext* _swsCtx;
	// 复用的像素数据缓冲区，避免每帧分配
	private byte[] _pixelDataBuffer = null;

	private readonly AudioFrameResizer<float> _audioQueue = new();

	private int _frameNum = 0;
	// 音频样本计数器（用于正确计算音频 PTS）
	private long _audioSampleCount = 0;

	public Generator Generator { get; set; }
	
	#region User-defined properties

	[CliParameter("FFmpeg Log Level", "ffloglevel")]
	public int LogLevel { get; set; } = ffmpeg.AV_LOG_INFO;
	// [CliParameter("Output Video File Path", "output", 'o')]
	// public string OutputPath { get; set; } = null;
	[CliParameter("Output Video Bitrate", "output-bitrate")]
	public uint OutputVideoBitRate { get; set; } = 9_000_000;

	// 硬件加速类型：Auto=自动检测, None=软件编码, NVENC=NVIDIA, QSV=Intel, AMF=AMD
	[CliParameter("Hardware Acceleration", "hwaccel")]
	public HardwareAccelType HardwareAccel { get; set; } = HardwareAccelType.Auto;

	// NVENC 编码配置（默认为最快速度）
	public string NvencPreset { get; set; } = "p1";         // p1-p7, 默认 p1 最快
	public string NvencTune { get; set; } = "hq";           // hq, ll, ull, lossless
	public string NvencRateControl { get; set; } = "vbr";   // vbr, cbr, cq
	public bool NvencTemporalAQ { get; set; } = false;      // 时域自适应量化（关闭更快）
	public bool NvencSpatialAQ { get; set; } = false;       // 空域自适应量化（关闭更快）
	public int NvencLookahead { get; set; } = 0;            // lookahead 帧数（0=最快）

	#endregion

	// 尝试查找可用的硬件编码器，返回编码器名称
	private unsafe AVCodec* FindVideoEncoder()
	{
		AVCodec* encoder = null;
		
		// 按优先级尝试不同的硬件编码器
		string[] encodersToTry = HardwareAccel switch
		{
			HardwareAccelType.NVENC => new[] { "h264_nvenc" },
			HardwareAccelType.QSV => new[] { "h264_qsv" },
			HardwareAccelType.AMF => new[] { "h264_amf" },
			HardwareAccelType.Auto => new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" },
			_ => new[] { "libx264" }
		};

		foreach (var encoderName in encodersToTry)
		{
			encoder = ffmpeg.avcodec_find_encoder_by_name(encoderName);
			if (encoder != null)
			{
				Logger.Info($"使用视频编码器: {encoderName}");
				return encoder;
			}
		}

		// 回退到默认 H.264 编码器
		encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
		if (encoder != null)
		{
			Logger.Info("使用默认 H.264 软件编码器");
		}
		return encoder;
	}

	public void InitializeFfmpeg()
	{
		unsafe
		{
			// 获取 FFmpeg 库路径
			var ffmpegPath = FfmpegUtils.GetFfmpegLibraryPath();
			if (string.IsNullOrEmpty(ffmpegPath))
			{
				throw new InvalidOperationException(
					"找不到 FFmpeg 库文件！\n\n" +
					"请安装 FFmpeg：\n" +
					"1. 运行命令: winget install \"FFmpeg (Shared)\"\n" +
					"2. 或下载 FFmpeg 并放到 C:\\ffmpeg 目录\n" +
					"3. 重启应用程序");
			}
			ffmpeg.RootPath = ffmpegPath;
			Logger.Debug($"FFmpeg library path: '{ffmpeg.RootPath}'.");
			
			ffmpeg.av_log_set_level(LogLevel);
			av_log_set_callback_callback logCb = (p0, level, format, v1) =>
			{
				if (level > ffmpeg.av_log_get_level()) return;
				var messageBufferLen = 1024; // is this too much for the stack?
				var messageBuffer = stackalloc byte[messageBufferLen];
				var printPrefix = 1;
				ffmpeg.av_log_format_line(p0, level, format, v1, messageBuffer, messageBufferLen, &printPrefix);
				var message = Marshal.PtrToStringAnsi((nint)messageBuffer);
				Console.Error.Write(message);
			};
			ffmpeg.av_log_set_callback(logCb);

			// format
			// ======

			{
				AVFormatContext* fmtCtx = null;
				ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "matroska", Generator.OutputFilePath ?? "/dev/stdout");
				if (fmtCtx == null) Console.Error.WriteLine("cannot allocate AVFormatContext");
				_fmtCtx = fmtCtx;
			}
			if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
			{
				Logger.Debug("Format requested global stream headers.");
			}

			// encoders
			// ========

			AVRational videoFps; videoFps.num = Generator.OutputFps; videoFps.den = 1;
			// 使用音频采样率作为视频 time_base，确保音视频完美同步
			// 视频 PTS = 帧号 × (采样率 / fps)
			int videoTimeBaseDen = Generator.AudioOutputSampleRate;

			// 使用硬件加速编码器（如果可用）
			var videoEnc = FindVideoEncoder();
			if (videoEnc == null)
			{
				throw new InvalidOperationException("找不到可用的视频编码器！");
			}
			var audioEnc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);

			_videoCtx = ffmpeg.avcodec_alloc_context3(videoEnc);
			_videoCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
			_videoCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
			_videoCtx->width = Generator.OutputVideoWidth;
			_videoCtx->height = Generator.OutputVideoHeight;
			// 视频 time_base = 1/采样率，与音频一致
			_videoCtx->time_base.num = 1;
			_videoCtx->time_base.den = videoTimeBaseDen;
			_videoCtx->framerate.num = videoFps.num;
			_videoCtx->framerate.den = videoFps.den;
			_videoCtx->bit_rate = OutputVideoBitRate;
			// _videoCtx->thread_count = Environment.ProcessorCount / 2;
			// Console.Error.WriteLine($"using {_videoCtx->thread_count} threads");
			if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
			{
				_videoCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
			}
			// ffmpeg.av_opt_set(_videoCtx->priv_data, "crf", "23", 0);
			// h264 codec fails with EINVAL/11 if extradata does not get allocated manually.
			if (_videoCtx->codec->id == AVCodecID.AV_CODEC_ID_H264)
			{
				_videoCtx->extradata = (byte*)ffmpeg.av_malloc(32);
				_videoCtx->extradata_size = 24;
			}
			
			// 为 NVENC 设置特定的编码选项
			AVDictionary* videoEncOpts = null;
			string encoderName = Marshal.PtrToStringAnsi((nint)videoEnc->name);
			if (encoderName == "h264_nvenc" || encoderName == "hevc_nvenc")
			{
				// NVENC 预设：使用 GUI 配置的值
				ffmpeg.av_dict_set(&videoEncOpts, "preset", NvencPreset, 0);
				// 调优模式
				ffmpeg.av_dict_set(&videoEncOpts, "tune", NvencTune, 0);
				// 码率控制
				ffmpeg.av_dict_set(&videoEncOpts, "rc", NvencRateControl, 0);
				// 启用 B 帧以提高压缩效率
				ffmpeg.av_dict_set(&videoEncOpts, "b_ref_mode", "middle", 0);
				// 时域自适应量化
				ffmpeg.av_dict_set(&videoEncOpts, "temporal-aq", NvencTemporalAQ ? "1" : "0", 0);
				// 空域自适应量化
				ffmpeg.av_dict_set(&videoEncOpts, "spatial-aq", NvencSpatialAQ ? "1" : "0", 0);
				// lookahead 帧数
				ffmpeg.av_dict_set(&videoEncOpts, "rc-lookahead", NvencLookahead.ToString(), 0);
				// 设置 GPU 设备
				ffmpeg.av_dict_set(&videoEncOpts, "gpu", "0", 0);
				
				Logger.Info($"NVENC 选项: preset={NvencPreset}, tune={NvencTune}, rc={NvencRateControl}, lookahead={NvencLookahead}");
			}
			else if (encoderName == "h264_qsv" || encoderName == "hevc_qsv")
			{
				// Intel QSV 选项
				ffmpeg.av_dict_set(&videoEncOpts, "preset", "medium", 0);
				Logger.Info($"QSV 选项已配置: preset=medium");
			}
			else if (encoderName == "h264_amf" || encoderName == "hevc_amf")
			{
				// AMD AMF 选项
				ffmpeg.av_dict_set(&videoEncOpts, "quality", "balanced", 0);
				Logger.Info($"AMF 选项已配置: quality=balanced");
			}
			
			var ret = ffmpeg.avcodec_open2(_videoCtx, videoEnc, &videoEncOpts);
			FfmpegUtils.LogIfAvError(ret, "cannot open video codec");

			_audioCtx = ffmpeg.avcodec_alloc_context3(audioEnc);
			_audioCtx->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
			_audioCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
			_audioCtx->sample_rate = Generator.AudioOutputSampleRate;
			_audioCtx->time_base.num = 1;
			_audioCtx->time_base.den = Generator.AudioOutputSampleRate;
			_audioCtx->ch_layout.nb_channels = 2;
			_audioCtx->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
			_audioCtx->ch_layout.u.mask = ffmpeg.AV_CH_LAYOUT_STEREO;
			_audioCtx->bit_rate = 128_000;
			_audioCtx->extradata = (byte*)ffmpeg.av_mallocz(32);
			_audioCtx->extradata_size = 24;
			if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
			{
				_audioCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
			}
			ret = ffmpeg.avcodec_open2(_audioCtx, audioEnc, null);
			FfmpegUtils.LogIfAvError(ret, "cannot open audio codec");

			// streams
			// =======

			_videoStream = ffmpeg.avformat_new_stream(_fmtCtx, null);
			if (_videoStream == null) Logger.Error("cannot allocate video output stream");
			_videoStream->index = (int)(_fmtCtx->nb_streams - 1);
			_videoStream->time_base = _videoCtx->time_base;
			_videoStream->r_frame_rate = videoFps;

			ret = ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _videoCtx);
			FfmpegUtils.LogIfAvError(ret, "cannot set video codec params from codec context");

			_audioStream = ffmpeg.avformat_new_stream(_fmtCtx, null);
			if (_videoStream == null) Logger.Error("cannot allocate audio output stream");
			_audioStream->index = (int)(_fmtCtx->nb_streams - 1);
			_audioStream->time_base = FfmpegUtils.GetRational(1, _audioCtx->sample_rate);
			
			ret = ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audioCtx);
			FfmpegUtils.LogIfAvError(ret, "cannot set audio codec params from codec context");
			if (_audioStream->codecpar->extradata == null)
			{
				Logger.Error("audio codec did not create extradata buffer");
			}

			// output file/stream
			// ==================

			ret = ffmpeg.avio_open(&_fmtCtx->pb, Generator.OutputFilePath ?? "pipe:", Generator.OutputFilePath != null ? ffmpeg.AVIO_FLAG_READ_WRITE : ffmpeg.AVIO_FLAG_WRITE);
			FfmpegUtils.LogIfAvError(ret, "cannot open stdout");
			AVDictionary* fmtOpts;
			ret = ffmpeg.avformat_write_header(_fmtCtx, &fmtOpts);
			FfmpegUtils.LogIfAvError(ret, "cannot write header");

			byte* dictBuf = (byte*)ffmpeg.av_malloc(1024);
			ffmpeg.av_dict_get_string(fmtOpts, &dictBuf, (byte)'=', (byte)':');

			// video frames
			// ============

			_videoAvFrame = ffmpeg.av_frame_alloc();
			_videoAvFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
			_videoAvFrame->width = Generator.OutputVideoWidth;
			_videoAvFrame->height = Generator.OutputVideoHeight;
			_videoAvFrame->time_base = _videoStream->time_base;

			ret = ffmpeg.av_frame_get_buffer(_videoAvFrame, 0);
			FfmpegUtils.LogIfAvError(ret, "cannot allocate video pixel buffer");

			_videoAvFramePre = ffmpeg.av_frame_alloc();
			_videoAvFramePre->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
			_videoAvFramePre->width = Generator.OutputVideoWidth;
			_videoAvFramePre->height = Generator.OutputVideoHeight;
			_videoAvFramePre->time_base = _videoStream->time_base;

			ret = ffmpeg.av_frame_get_buffer(_videoAvFramePre, 0);
			FfmpegUtils.LogIfAvError(ret, "cannot allocate video pixel buffer");

			// audio frames
			// ============

			_audioAvFrame = ffmpeg.av_frame_alloc();
			_audioAvFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
			ffmpeg.av_channel_layout_copy(&_audioAvFrame->ch_layout, &_audioCtx->ch_layout);
			_audioAvFrame->sample_rate = _audioCtx->sample_rate;
			_audioAvFrame->nb_samples = _audioCtx->frame_size;
			_audioAvFrame->ch_layout.nb_channels = 2;
			_audioAvFrame->ch_layout.u.mask = 3;
			_audioAvFrame->time_base.num = _audioCtx->time_base.num;
			_audioAvFrame->time_base.den = _audioCtx->time_base.den;

			if ((_audioCtx->codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) == 0)
			{
				Logger.Warning("audio codec does not support variable frame size");
			}

			ret = ffmpeg.av_frame_get_buffer(_audioAvFrame, 0);
			FfmpegUtils.LogIfAvError(ret, "cannot allocate audio sample buffer");
			_audioQueue.BufferLength = _audioAvFrame->nb_samples * _audioAvFrame->ch_layout.nb_channels;
			_audioQueue.OutputCallback = (buf) =>
			{
				// 设置正确的音频 PTS（基于已编码的样本数）
				_audioAvFrame->pts = _audioSampleCount;
				
				float* ab0 = (float*)_audioAvFrame->data[0];
				float* ab1 = (float*)_audioAvFrame->data[1];
				int samplesPerChannel = _audioAvFrame->nb_samples;
				for (int i = 0; i < samplesPerChannel; i++)
				{
					ab0[i] = buf[i * 2];
					ab1[i] = buf[i * 2 + 1];
				}
				DoEncode(_audioCtx, _audioStream, _audioAvFrame, _audioAvPacket);
				
				// 更新音频样本计数
				_audioSampleCount += samplesPerChannel;
			};

			Logger.Debug($"video original linesize = {_videoAvFramePre->linesize[0]} {_videoAvFramePre->linesize[1]}");
			Logger.Debug($"video target linesize =   {_videoAvFrame->linesize[0]} {_videoAvFrame->linesize[1]} {_videoAvFrame->linesize[2]}");
			Logger.Debug($"req. audio frame size =   {_audioCtx->frame_size} * {_audioCtx->ch_layout.nb_channels}ch");
			Logger.Debug($"audio ch layout =         {_audioAvFrame->ch_layout.nb_channels} {_audioAvFrame->ch_layout.order} {_audioAvFrame->ch_layout.u.mask}");
			Logger.Debug($"audio linesizes =         {_audioAvFrame->linesize[0]} {_audioAvFrame->linesize[1]} {_audioAvFrame->linesize[2]} {_audioAvFrame->linesize[3]} {_audioAvFrame->linesize[4]} {_audioAvFrame->linesize[5]} {_audioAvFrame->linesize[6]} {_audioAvFrame->linesize[7]}");

			_videoAvPacket = ffmpeg.av_packet_alloc();
			_audioAvPacket = ffmpeg.av_packet_alloc();

			ffmpeg.av_dump_format(_fmtCtx, 0, Generator.OutputFilePath ?? "pipe:", 1);
		}
		_init = true;
	}

	private unsafe void DoEncode(AVCodecContext* cCtx, AVStream* stream, AVFrame* frame, AVPacket* packet)
	{
		int ret;

		FfmpegUtils.LogFrameData(frame);

		ret = ffmpeg.avcodec_send_frame(cCtx, frame);
		FfmpegUtils.LogIfAvError(ret, "cannot send frame to encoder");

		while (ret >= 0)
		{
			ret = ffmpeg.avcodec_receive_packet(cCtx, packet);

			if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
			{
				break;
			}
			else if (ret < 0)
			{
				// if frame is null, `EOF` code is expected, don't treat it as an error.
				if (frame != null)
				{
					FfmpegUtils.LogIfAvError(ret, "cannot encode");
				}
				break;
			}

			ffmpeg.av_packet_rescale_ts(packet, cCtx->time_base, stream->time_base);
			packet->stream_index = stream->index;
			packet->time_base.num = stream->time_base.num;
			packet->time_base.den = stream->time_base.den;
			FfmpegUtils.LogPacketData(packet);

			ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, packet);
			FfmpegUtils.LogIfAvError(ret, "cannot write packet");
			if (ret == -32) Generator._exitRequested = true;
		}
	}

	public void PushNewFrame(Image videoFrame, AudioBuffer audioFrame, double delta)
	{
		PushNewFrame((Image<Rgba32>)videoFrame, audioFrame, delta);
	}

	public unsafe void PushNewFrame(Image<Rgba32> videoFrame, AudioBuffer audioFrame, double delta)
	{
		if (!_init)
		{
			InitializeFfmpeg();
		}

		var ret = ffmpeg.av_frame_make_writable(_videoAvFrame);
		FfmpegUtils.LogIfAvError(ret, "cannot make video pixel data writable");
		ret = ffmpeg.av_frame_make_writable(_audioAvFrame);
		FfmpegUtils.LogIfAvError(ret, "cannot make audio sample buffer writable");

		// 音频 PTS 现在在 _audioQueue.OutputCallback 中设置

		// TODO: move to init method
		if (_swsCtx == null)
		{
			// SWS_BILINEAR = 2
			_swsCtx = ffmpeg.sws_getContext(videoFrame.Width, videoFrame.Height, (AVPixelFormat)_videoAvFramePre->format, videoFrame.Width, videoFrame.Height, (AVPixelFormat)_videoAvFrame->format, 2, null, null, null);
			if (_swsCtx == null)
			{
				Logger.Error("cannot initialize sws context");
			}
		}

		if (_swsCtx != null)
		{
			// 复用像素数据缓冲区
			int requiredSize = videoFrame.Width * videoFrame.Height * 4;
			if (_pixelDataBuffer == null || _pixelDataBuffer.Length < requiredSize)
			{
				_pixelDataBuffer = new byte[requiredSize];
			}
			videoFrame.CopyPixelDataTo(_pixelDataBuffer);

			// 使用正确的 linesize 进行 sws_scale
			// 输入数据的 linesize 是 width * 4 (RGBA)
			int srcLinesize = videoFrame.Width * 4;
			
			fixed (byte* srcData = _pixelDataBuffer)
			{
				// 创建源数据指针数组
				byte_ptrArray8 srcDataArray = new byte_ptrArray8();
				srcDataArray[0] = srcData;
				
				// 创建源 linesize 数组
				int_array8 srcLinesizeArray = new int_array8();
				srcLinesizeArray[0] = srcLinesize;
				
				ffmpeg.sws_scale(_swsCtx, srcDataArray, srcLinesizeArray, 0, videoFrame.Height, _videoAvFrame->data, _videoAvFrame->linesize);
			}
		}
		else if (_videoAvFrame->format == (int)AVPixelFormat.AV_PIX_FMT_GBRP)
		{
			// Unoptimized pixel copy.
			videoFrame.ProcessPixelRows((pa) =>
			{
				for (int y = 0; y < pa.Height; y++)
				{
					var row = pa.GetRowSpan(y);

					for (int x = 0; x < pa.Width; x++)
					{
						var p = row[x];
						_videoAvFrame->data[0][_videoAvFrame->linesize[0] * y + x] = p.G;
						_videoAvFrame->data[1][_videoAvFrame->linesize[1] * y + x] = p.B;
						_videoAvFrame->data[2][_videoAvFrame->linesize[2] * y + x] = p.R;
					}
				}
			});
		}

		_videoAvFrame->time_base.num = _videoCtx->time_base.num;
		_videoAvFrame->time_base.den = _videoCtx->time_base.den;
		// 视频 PTS = 帧号 × 每帧采样数，与音频完美同步
		// 例如 60fps @ 48kHz: 每帧 800 采样
		long samplesPerFrame = Generator.AudioOutputSampleRate / Generator.OutputFps;
		_videoAvFrame->pts = _frameNum * samplesPerFrame;
		_videoAvFrame->duration = samplesPerFrame;
		DoEncode(_videoCtx, _videoStream, _videoAvFrame, _videoAvPacket);

		_audioQueue.Push(audioFrame.ToArray());

		if (_frameNum % 10 == 0)
		{
			Logger.Trace($"frame {_frameNum}, ts {_frameNum / Generator.OutputFps}, framegen speed {(int)(1/delta)} fps\x1b[K\x1b[G");
		}

		_frameNum++;
	}

	public unsafe void Finish()
	{
		Logger.Debug("Flushing streams…");
		DoEncode(_videoCtx, _videoStream, null, _videoAvPacket);
		DoEncode(_audioCtx, _audioStream, null, _audioAvPacket);

		Logger.Debug("Freeing FFmpeg resources…");
		ffmpeg.sws_freeContext(_swsCtx);
		var videoCtx = _videoCtx;
		ffmpeg.avcodec_free_context(&videoCtx);
		var audioCtx = _audioCtx;
		ffmpeg.avcodec_free_context(&audioCtx);

		ffmpeg.av_write_trailer(_fmtCtx);

		if ((_fmtCtx->flags & ffmpeg.AVFMT_NOFILE) == 0)
		{
			ffmpeg.avio_closep(&_fmtCtx->pb);
		}

		ffmpeg.avformat_free_context(_fmtCtx);
	}
}
