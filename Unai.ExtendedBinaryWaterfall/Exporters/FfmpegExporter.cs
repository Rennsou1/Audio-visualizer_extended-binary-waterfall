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
	// 标记音频编码是否可用
	private bool _audioEnabled = true;
	// 实际使用的音频采样率（AAC 标准采样率）
	private int _actualAudioSampleRate = 48000;

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
	// 音频重采样上下文（从原始采样率重采样到 AAC 标准采样率）
	private unsafe SwrContext* _swrCtx;
	// 重采样后的音频帧
	private unsafe AVFrame* _resampledAudioFrame;
	// 复用的像素数据缓冲区，避免每帧分配
	private byte[] _pixelDataBuffer = null;
	// 输入音频的原始采样率
	private int _inputAudioSampleRate = 48000;
	// 是否需要重采样
	private bool _needResample = false;

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

	// NVENC 编码配置（基于 NVIDIA Video Codec SDK 10 最佳实践）
	// P1=最快/低质量, P4=平衡, P7=最慢/高质量
	public string NvencPreset { get; set; } = "p4";      // P4 平衡速度和质量
	public string NvencTune { get; set; } = "hq";        // hq=高质量, ll=低延迟, ull=超低延迟
	public string NvencRateControl { get; set; } = "vbr"; // vbr=可变比特率
	public int NvencBFrames { get; set; } = 2;           // B帧数量（减少以提高速度）
	public bool NvencTemporalAQ { get; set; } = true;    // 时域自适应量化（改善动态场景）
	public bool NvencSpatialAQ { get; set; } = false;    // 空域AQ关闭以提高速度
	public int NvencAQStrength { get; set; } = 8;        // AQ强度（1-15, 8=默认）
	public int NvencLookahead { get; set; } = 8;         // Lookahead帧数（8帧平衡质量和速度）
	
	// 视频编码参数
	public int VideoCodecIndex { get; set; } = 0;
	public int RateControlMode { get; set; } = 0;
	public int CrfValue { get; set; } = 23;
	public int VideoProfile { get; set; } = 0;
	public int VideoLevel { get; set; } = 0;
	public int KeyframeInterval { get; set; } = 0;
	
	// 音频编码参数
	public uint OutputAudioBitRate { get; set; } = 256_000;
	public int AudioCodecIndex { get; set; } = 0;
	
	// 输出格式
	public string OutputFormat { get; set; } = "matroska";

	#endregion

	// AAC 支持的标准采样率
	private static readonly int[] SupportedSampleRates = { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000 };
	
	// 获取最接近的 AAC 支持采样率
	private static int GetNearestSupportedSampleRate(int inputRate)
	{
		int nearest = 48000; // 默认使用 48kHz
		int minDiff = int.MaxValue;
		
		foreach (var rate in SupportedSampleRates)
		{
			int diff = Math.Abs(rate - inputRate);
			if (diff < minDiff)
			{
				minDiff = diff;
				nearest = rate;
			}
		}
		return nearest;
	}

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
		Generator.DebugLog("[FFmpeg] 开始初始化...");
		unsafe
		{
			// 获取 FFmpeg 库路径
			Generator.DebugLog("[FFmpeg] 查找库路径...");
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
			Generator.DebugLog($"[FFmpeg] 库路径: {ffmpegPath}");
			ffmpeg.RootPath = ffmpegPath;
			Logger.Debug($"FFmpeg library path: '{ffmpeg.RootPath}'.");
			
			Generator.DebugLog("[FFmpeg] 加载 FFmpeg 库...");
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
			Generator.DebugLog("[FFmpeg] 日志回调设置完成");

			// format
			// ======
			Generator.DebugLog("[FFmpeg] 创建输出格式上下文...");
			{
				AVFormatContext* fmtCtx = null;
				// 根据输出格式选择正确的 FFmpeg 格式名称
				string formatName = OutputFormat switch
				{
					"mp4" => "mp4",
					"mkv" or "matroska" => "matroska",
					"webm" => "webm",
					"mov" => "mov",
					"avi" => "avi",
					_ => "matroska"
				};
				Generator.DebugLog($"[FFmpeg] 输出格式: {formatName}, 文件: {Generator.OutputFilePath}");
				ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, formatName, Generator.OutputFilePath ?? "/dev/stdout");
				if (fmtCtx == null)
				{
					Generator.DebugLog("[FFmpeg] 错误: 无法分配 AVFormatContext");
					Console.Error.WriteLine("cannot allocate AVFormatContext");
				}
				_fmtCtx = fmtCtx;
			}
			Generator.DebugLog("[FFmpeg] 输出格式上下文创建完成");
			if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
			{
				Logger.Debug("Format requested global stream headers.");
			}

			// encoders
			// ========
			Generator.DebugLog("[FFmpeg] 查找视频编码器...");

			AVRational videoFps; videoFps.num = Generator.OutputFps; videoFps.den = 1;
			// 先计算实际使用的音频采样率（AAC 支持的标准采样率）
			_actualAudioSampleRate = GetNearestSupportedSampleRate(Generator.AudioOutputSampleRate);
			// 视频 time_base = 1/fps，PTS 直接使用帧号
			int videoTimeBaseDen = Generator.OutputFps;

			// 使用硬件加速编码器（如果可用）
			var videoEnc = FindVideoEncoder();
			if (videoEnc == null)
			{
				Generator.DebugLog("[FFmpeg] 错误: 找不到视频编码器！");
				throw new InvalidOperationException("找不到可用的视频编码器！");
			}
			string vEncName = Marshal.PtrToStringAnsi((nint)videoEnc->name);
			Generator.DebugLog($"[FFmpeg] 视频编码器: {vEncName}");
			
			Generator.DebugLog("[FFmpeg] 查找音频编码器...");
			var audioEnc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
			Generator.DebugLog($"[FFmpeg] 音频编码器: {(audioEnc != null ? "AAC" : "null")}");

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
				// 预设名称（某些 FFmpeg 版本好像不支持 p1-p7？！）
				string preset = NvencPreset switch
				{
					"p1" => "fastest",
					"p2" => "faster",
					"p3" => "fast",
					"p4" => "medium",
					"p5" => "slow",
					"p6" => "slower",
					"p7" => "slowest",
					_ => NvencPreset
				};
				ffmpeg.av_dict_set(&videoEncOpts, "preset", preset, 0);
				
				// 码率控制
				string rc = NvencRateControl switch
				{
					"vbr_hq" => "vbr",
					"cbr_hq" => "cbr",
					_ => NvencRateControl
				};
				ffmpeg.av_dict_set(&videoEncOpts, "rc", rc, 0);
				
				// B帧数量
				ffmpeg.av_dict_set(&videoEncOpts, "bf", NvencBFrames.ToString(), 0);
				
				// 自适应量化
				if (NvencSpatialAQ)
					ffmpeg.av_dict_set(&videoEncOpts, "spatial-aq", "1", 0);
				if (NvencTemporalAQ)
					ffmpeg.av_dict_set(&videoEncOpts, "temporal-aq", "1", 0);
				if (NvencSpatialAQ || NvencTemporalAQ)
					ffmpeg.av_dict_set(&videoEncOpts, "aq-strength", NvencAQStrength.ToString(), 0);
				
				// Lookahead
				if (NvencLookahead > 0)
					ffmpeg.av_dict_set(&videoEncOpts, "rc-lookahead", NvencLookahead.ToString(), 0);
				
				// GPU设备选择
				ffmpeg.av_dict_set(&videoEncOpts, "gpu", "0", 0);
				
				Logger.Info($"NVENC 配置: preset={preset}, rc={rc}, bf={NvencBFrames}, lookahead={NvencLookahead}");
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
			
			Generator.DebugLog("[FFmpeg] 打开视频编码器...");
			var ret = ffmpeg.avcodec_open2(_videoCtx, videoEnc, &videoEncOpts);
			Generator.DebugLog($"[FFmpeg] 视频编码器结果: {ret}");
			if (ret < 0)
			{
				FfmpegUtils.LogIfAvError(ret, "cannot open video codec");
				throw new InvalidOperationException($"无法打开视频编码器 {encoderName}，错误码: {ret}");
			}

			Generator.DebugLog("[FFmpeg] 配置音频编码器...");
			_audioCtx = ffmpeg.avcodec_alloc_context3(audioEnc);
			Generator.DebugLog("[FFmpeg] 音频上下文已分配");
			_audioCtx->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
			_audioCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
			// AAC 不支持任意采样率！！！！！使用已计算的标准采样率o(*￣▽￣*)ブ
			_audioCtx->sample_rate = _actualAudioSampleRate;
			_audioCtx->time_base.num = 1;
			_audioCtx->time_base.den = _actualAudioSampleRate;
			// 输出声道数使用用户设置
			ffmpeg.av_channel_layout_default(&_audioCtx->ch_layout, Generator.AudioOutputChannelCount);
			_audioCtx->bit_rate = OutputAudioBitRate > 0 ? OutputAudioBitRate : 128_000;
			// AAC 编码器会自动处理 extradata，不需要手动设置
			if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
			{
				_audioCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
			}
			Generator.DebugLog($"[FFmpeg] 音频参数: 解码器{Generator.AudioDecoderSampleRate}Hz {Generator.AudioDecoderChannelCount}ch -> 输出{_actualAudioSampleRate}Hz {Generator.AudioOutputChannelCount}ch, {_audioCtx->bit_rate}bps");
			Generator.DebugLog("[FFmpeg] 打开音频编码器...");
			ret = ffmpeg.avcodec_open2(_audioCtx, audioEnc, null);
			Generator.DebugLog($"[FFmpeg] 音频编码器结果: {ret}");
			if (ret < 0)
			{
				Generator.DebugLog("[FFmpeg] 警告: 音频编码器打开失败，将跳过音频");
				_audioEnabled = false;
			}
			FfmpegUtils.LogIfAvError(ret, "cannot open audio codec");

			// streams
			// =======
			Generator.DebugLog("[FFmpeg] 创建视频流...");
			_videoStream = ffmpeg.avformat_new_stream(_fmtCtx, null);
			if (_videoStream == null) Logger.Error("cannot allocate video output stream");
			_videoStream->index = (int)(_fmtCtx->nb_streams - 1);
			_videoStream->time_base = _videoCtx->time_base;
			_videoStream->r_frame_rate = videoFps;

			ret = ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _videoCtx);
			FfmpegUtils.LogIfAvError(ret, "cannot set video codec params from codec context");

			// 只在音频编码器可用时创建音频流
			if (_audioEnabled)
			{
				Generator.DebugLog("[FFmpeg] 创建音频流...");
				_audioStream = ffmpeg.avformat_new_stream(_fmtCtx, null);
				if (_audioStream == null) Logger.Error("cannot allocate audio output stream");
				_audioStream->index = (int)(_fmtCtx->nb_streams - 1);
				_audioStream->time_base = FfmpegUtils.GetRational(1, _audioCtx->sample_rate);
				
				ret = ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audioCtx);
				FfmpegUtils.LogIfAvError(ret, "cannot set audio codec params from codec context");
				if (_audioStream->codecpar->extradata == null)
				{
					Logger.Error("audio codec did not create extradata buffer");
				}
			}
			else
			{
				Generator.DebugLog("[FFmpeg] 跳过音频流创建（音频编码器不可用）");
			}
			Generator.DebugLog("[FFmpeg] 流创建完成");

			// output file/stream
			// ==================
			Generator.DebugLog($"[FFmpeg] 打开输出文件: {Generator.OutputFilePath}");
			ret = ffmpeg.avio_open(&_fmtCtx->pb, Generator.OutputFilePath ?? "pipe:", Generator.OutputFilePath != null ? ffmpeg.AVIO_FLAG_READ_WRITE : ffmpeg.AVIO_FLAG_WRITE);
			Generator.DebugLog($"[FFmpeg] avio_open 结果: {ret}");
			FfmpegUtils.LogIfAvError(ret, "cannot open stdout");
			Generator.DebugLog("[FFmpeg] 写入文件头...");
			AVDictionary* fmtOpts;
			ret = ffmpeg.avformat_write_header(_fmtCtx, &fmtOpts);
			Generator.DebugLog($"[FFmpeg] 写入头结果: {ret}");
			FfmpegUtils.LogIfAvError(ret, "cannot write header");

			byte* dictBuf = (byte*)ffmpeg.av_malloc(1024);
			ffmpeg.av_dict_get_string(fmtOpts, &dictBuf, (byte)'=', (byte)':');

			// video frames
			// ============
			Generator.DebugLog("[FFmpeg] 分配视频帧缓冲...");
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

			// audio frames (只在音频编码器可用时初始化)
			// ============
			if (_audioEnabled)
			{
				Generator.DebugLog("[FFmpeg] 分配音频帧缓冲...");
				
				// 输入采样率是音频解码器的原始采样率
				_inputAudioSampleRate = Generator.AudioDecoderSampleRate;
				// 输出采样率是用户期望的目标采样率（已经过 AAC 兼容性检查）
				_actualAudioSampleRate = GetNearestSupportedSampleRate(Generator.AudioOutputSampleRate);
				// 当输入和输出采样率不同时需要重采样
				_needResample = (_inputAudioSampleRate != _actualAudioSampleRate);
				Generator.DebugLog($"[FFmpeg] 音频重采样: {_inputAudioSampleRate}Hz (解码器) -> {_actualAudioSampleRate}Hz (输出), 需要重采样: {_needResample}");
				
				// Generator 每帧产生的采样数（基于解码器采样率）
				// 解码器每帧产生: AudioDecoderSampleRate / OutputFps 个采样（每通道）
				int inputSamplesPerChannel = Generator.AudioDecoderSampleRate / Generator.OutputFps;
				Generator.DebugLog($"[FFmpeg] Generator 每帧采样数(每通道): {inputSamplesPerChannel} (基于解码器 {Generator.AudioDecoderSampleRate}Hz)");
				
				// 输入音频帧缓冲区大小需要足够容纳 AAC 帧（通常 1024 采样）
				int audioFrameBufferSize = Math.Max(inputSamplesPerChannel, _audioCtx->frame_size);
				Generator.DebugLog($"[FFmpeg] 音频帧缓冲区大小: {audioFrameBufferSize} (AAC帧: {_audioCtx->frame_size})");
				
				// 输入音频帧（使用解码器的实际声道数）
				int inputChannels = Generator.AudioDecoderChannelCount;
				// 输出声道数（用户设置的目标）
				int outputChannels = Generator.AudioOutputChannelCount;
				_audioAvFrame = ffmpeg.av_frame_alloc();
				_audioAvFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
				ffmpeg.av_channel_layout_default(&_audioAvFrame->ch_layout, inputChannels);
				_audioAvFrame->sample_rate = _inputAudioSampleRate;
				_audioAvFrame->nb_samples = audioFrameBufferSize;
				_audioAvFrame->time_base.num = 1;
				_audioAvFrame->time_base.den = _actualAudioSampleRate;
				Generator.DebugLog($"[FFmpeg] 输入帧配置: {inputChannels}ch, {_inputAudioSampleRate}Hz, {audioFrameBufferSize}samples");

				ret = ffmpeg.av_frame_get_buffer(_audioAvFrame, 0);
				FfmpegUtils.LogIfAvError(ret, "cannot allocate input audio sample buffer");
				
				if (_needResample)
				{
					// 初始化重采样上下文
					Generator.DebugLog("[FFmpeg] 初始化音频重采样上下文...");
					
					// 初始化重采样上下文（在 unsafe 上下文中局部变量已固定）
					// 输入：使用解码器的声道数
					// 输出：使用用户设置的输出声道数，重采样器会自动上混/下混
					SwrContext* swrCtx = null;
					AVChannelLayout inLayout = new AVChannelLayout();
					AVChannelLayout outLayout = new AVChannelLayout();
					ffmpeg.av_channel_layout_default(&inLayout, inputChannels);
					ffmpeg.av_channel_layout_default(&outLayout, outputChannels);
					
					ret = ffmpeg.swr_alloc_set_opts2(
						&swrCtx,
						&outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, _actualAudioSampleRate,
						&inLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, _inputAudioSampleRate,
						0, null);
					_swrCtx = swrCtx;
					FfmpegUtils.LogIfAvError(ret, "cannot set swr options");
					
					ret = ffmpeg.swr_init(_swrCtx);
					FfmpegUtils.LogIfAvError(ret, "cannot init swr context");
					Generator.DebugLog($"[FFmpeg] 重采样上下文初始化完成: {_inputAudioSampleRate}Hz {inputChannels}ch -> {_actualAudioSampleRate}Hz {outputChannels}ch");
					
					// 分配重采样后的输出帧
					_resampledAudioFrame = ffmpeg.av_frame_alloc();
					_resampledAudioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
					ffmpeg.av_channel_layout_default(&_resampledAudioFrame->ch_layout, outputChannels);
					_resampledAudioFrame->sample_rate = _actualAudioSampleRate;
					_resampledAudioFrame->nb_samples = _audioCtx->frame_size;
					_resampledAudioFrame->time_base.num = 1;
					_resampledAudioFrame->time_base.den = _actualAudioSampleRate;
					
					ret = ffmpeg.av_frame_get_buffer(_resampledAudioFrame, 0);
					FfmpegUtils.LogIfAvError(ret, "cannot allocate resampled audio buffer");
				}

				// 检查音频编码器是否支持可变帧大小
				if (_audioCtx->codec != null && (_audioCtx->codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) == 0)
				{
					Logger.Warning("audio codec does not support variable frame size");
				}

				// 音频队列缓冲长度 = 每通道采样数 × 实际声道数
				int audioQueueLength = inputSamplesPerChannel * inputChannels;
				_audioQueue.BufferLength = audioQueueLength;
				Generator.DebugLog($"[FFmpeg] 音频队列长度: {audioQueueLength}, AAC帧大小: {_audioCtx->frame_size}, 输入帧采样数(每通道): {inputSamplesPerChannel}, 输入声道数: {inputChannels}");
				
				int aacFrameSize = _audioCtx->frame_size; // AAC 通常是 1024
				
				// 重采样后的输出累积缓冲区（累积到 AAC 帧大小后再编码）
				// 需要足够大的缓冲区来存储重采样后的数据
				int resampleRatio = _needResample ? (_actualAudioSampleRate / _inputAudioSampleRate + 2) : 1;
				int outputBufferSize = aacFrameSize * resampleRatio * 2;
				float[] outputAccumL = new float[outputBufferSize];
				float[] outputAccumR = new float[outputBufferSize];
				int outputAccumCount = 0;
				
				// 临时缓冲区用于重采样输出
				float[] tempResampleL = new float[inputSamplesPerChannel * resampleRatio + 1024];
				float[] tempResampleR = new float[inputSamplesPerChannel * resampleRatio + 1024];
				
				Generator.DebugLog($"[FFmpeg] 重采样比率: {resampleRatio}, 输出缓冲区大小: {outputBufferSize}");
				
				_audioQueue.OutputCallback = (buf) =>
				{
					// 根据输入声道数计算每通道采样数
					int samplesPerChannel = buf.Length / inputChannels;
					
					if (_needResample && _swrCtx != null)
					{
						// 填充输入帧（支持单声道或双声道输入）
						float* ab0 = (float*)_audioAvFrame->data[0];
						if (inputChannels == 1)
						{
							// 单声道：所有数据在 data[0]
							for (int i = 0; i < samplesPerChannel; i++)
							{
								ab0[i] = buf[i];
							}
						}
						else
						{
							// 双声道：格式 -> planar 格式
							float* ab1 = (float*)_audioAvFrame->data[1];
							for (int i = 0; i < samplesPerChannel; i++)
							{
								ab0[i] = buf[i * 2];
								ab1[i] = buf[i * 2 + 1];
							}
						}
						_audioAvFrame->nb_samples = samplesPerChannel;
						
						// 使用 swr_convert 进行重采样（不是 swr_convert_frame）
						// 计算预期输出采样数
						int maxOutputSamples = (int)((long)samplesPerChannel * _actualAudioSampleRate / _inputAudioSampleRate) + 256;
						
						// 确保重采样输出帧有足够空间
						if (_resampledAudioFrame->nb_samples < maxOutputSamples)
						{
							// 重新分配缓冲区（使用用户设置的输出声道数）
							ffmpeg.av_frame_unref(_resampledAudioFrame);
							_resampledAudioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
							ffmpeg.av_channel_layout_default(&_resampledAudioFrame->ch_layout, outputChannels);
							_resampledAudioFrame->sample_rate = _actualAudioSampleRate;
							_resampledAudioFrame->nb_samples = maxOutputSamples;
							ffmpeg.av_frame_get_buffer(_resampledAudioFrame, 0);
						}
						
						// 执行重采样
						byte** outData = (byte**)&_resampledAudioFrame->data;
						byte** inData = (byte**)&_audioAvFrame->data;
						int convertedSamples = ffmpeg.swr_convert(
							_swrCtx,
							outData,
							maxOutputSamples,
							inData,
							samplesPerChannel);
						
						if (convertedSamples < 0)
						{
							FfmpegUtils.LogIfAvError(convertedSamples, "swr_convert failed");
							return;
						}
						
						// 将重采样后的数据累积到输出缓冲区（支持单声道或双声道输出）
						float* outL = (float*)_resampledAudioFrame->data[0];
						if (outputChannels == 1)
						{
							// 单声道输出
							for (int i = 0; i < convertedSamples && outputAccumCount < outputBufferSize; i++)
							{
								outputAccumL[outputAccumCount] = outL[i];
								outputAccumR[outputAccumCount] = outL[i]; // 复制到 R 以保持一致性
								outputAccumCount++;
							}
						}
						else
						{
							// 双声道输出
							float* outR = (float*)_resampledAudioFrame->data[1];
							for (int i = 0; i < convertedSamples && outputAccumCount < outputBufferSize; i++)
							{
								outputAccumL[outputAccumCount] = outL[i];
								outputAccumR[outputAccumCount] = outR[i];
								outputAccumCount++;
							}
						}
					}
					else
					{
						// 无需重采样，直接累积（处理输入/输出声道不匹配的情况）
						if (inputChannels == 1)
						{
							// 单声道输入
							for (int i = 0; i < samplesPerChannel && outputAccumCount < outputBufferSize; i++)
							{
								outputAccumL[outputAccumCount] = buf[i];
								outputAccumR[outputAccumCount] = buf[i]; // 复制到 R
								outputAccumCount++;
							}
						}
						else
						{
							// 双声道输入
							for (int i = 0; i < samplesPerChannel && outputAccumCount < outputBufferSize; i++)
							{
								outputAccumL[outputAccumCount] = buf[i * 2];
								outputAccumR[outputAccumCount] = buf[i * 2 + 1];
								outputAccumCount++;
							}
						}
					}
					
					// 当累积够 AAC 帧大小时，编码输出
					while (outputAccumCount >= aacFrameSize)
					{
						// 填充编码帧（支持单声道或双声道）
						float* encL = (float*)_resampledAudioFrame->data[0];
						for (int i = 0; i < aacFrameSize; i++)
						{
							encL[i] = outputAccumL[i];
						}
						if (outputChannels >= 2)
						{
							float* encR = (float*)_resampledAudioFrame->data[1];
							for (int i = 0; i < aacFrameSize; i++)
							{
								encR[i] = outputAccumR[i];
							}
						}
						_resampledAudioFrame->nb_samples = aacFrameSize;
						_resampledAudioFrame->pts = _audioSampleCount;
						
						DoEncode(_audioCtx, _audioStream, _resampledAudioFrame, _audioAvPacket);
						_audioSampleCount += aacFrameSize;
						
						// 移除已处理的数据
						Array.Copy(outputAccumL, aacFrameSize, outputAccumL, 0, outputAccumCount - aacFrameSize);
						Array.Copy(outputAccumR, aacFrameSize, outputAccumR, 0, outputAccumCount - aacFrameSize);
						outputAccumCount -= aacFrameSize;
					}
				};
			}
			else
			{
				Generator.DebugLog("[FFmpeg] 跳过音频帧缓冲分配（音频编码器不可用）");
			}

			Logger.Debug($"video original linesize = {_videoAvFramePre->linesize[0]} {_videoAvFramePre->linesize[1]}");
			Logger.Debug($"video target linesize =   {_videoAvFrame->linesize[0]} {_videoAvFrame->linesize[1]} {_videoAvFrame->linesize[2]}");
			// 只在音频可用时输出音频调试信息
			if (_audioEnabled)
			{
				Logger.Debug($"req. audio frame size =   {_audioCtx->frame_size} * {_audioCtx->ch_layout.nb_channels}ch");
				Logger.Debug($"audio ch layout =         {_audioAvFrame->ch_layout.nb_channels} {_audioAvFrame->ch_layout.order} {_audioAvFrame->ch_layout.u.mask}");
				Logger.Debug($"audio linesizes =         {_audioAvFrame->linesize[0]} {_audioAvFrame->linesize[1]} {_audioAvFrame->linesize[2]} {_audioAvFrame->linesize[3]} {_audioAvFrame->linesize[4]} {_audioAvFrame->linesize[5]} {_audioAvFrame->linesize[6]} {_audioAvFrame->linesize[7]}");
			}

			_videoAvPacket = ffmpeg.av_packet_alloc();
			// 只在音频可用时分配音频包
			if (_audioEnabled)
			{
				_audioAvPacket = ffmpeg.av_packet_alloc();
			}

			ffmpeg.av_dump_format(_fmtCtx, 0, Generator.OutputFilePath ?? "pipe:", 1);
			Generator.DebugLog("[FFmpeg] 初始化完成！");
		}
		_init = true;
	}

	private unsafe void DoEncode(AVCodecContext* cCtx, AVStream* stream, AVFrame* frame, AVPacket* packet)
	{
		int ret;

		FfmpegUtils.LogFrameData(frame);

		// 发送帧到编码器
		ret = ffmpeg.avcodec_send_frame(cCtx, frame);
		if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
		{
			FfmpegUtils.LogIfAvError(ret, "cannot send frame to encoder");
			return;
		}

		// 循环接收编码后的 packet
		while (true)
		{
			ret = ffmpeg.avcodec_receive_packet(cCtx, packet);

			if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
			{
				// EAGAIN: 需要更多输入帧；EOF: 编码器已刷新完毕
				break;
			}
			else if (ret < 0)
			{
				FfmpegUtils.LogIfAvError(ret, "cannot encode");
				break;
			}

			// 转换时间戳
			ffmpeg.av_packet_rescale_ts(packet, cCtx->time_base, stream->time_base);
			packet->stream_index = stream->index;
			packet->time_base.num = stream->time_base.num;
			packet->time_base.den = stream->time_base.den;
			FfmpegUtils.LogPacketData(packet);

			// 写入 packet
			ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, packet);
			if (ret < 0)
			{
				FfmpegUtils.LogIfAvError(ret, "cannot write packet");
				if (ret == -32) Generator._exitRequested = true;
			}
			
			// 关键：释放 packet 资源，防止内存泄漏
			ffmpeg.av_packet_unref(packet);
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
			Generator.DebugLog("[FFmpeg] 开始推送第一帧...");
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
		// 视频 PTS 直接使用帧号，time_base 已设为 1/fps
		// 这确保视频帧精确对应每个时间点，与音频独立计算
		_videoAvFrame->pts = _frameNum;
		_videoAvFrame->duration = 1;
		DoEncode(_videoCtx, _videoStream, _videoAvFrame, _videoAvPacket);

		// 只在音频编码器可用时处理音频
		if (_audioEnabled)
		{
			_audioQueue.Push(audioFrame.ToArray());
		}

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
		// 只在音频编码器可用时刷新音频流
		if (_audioEnabled)
		{
			DoEncode(_audioCtx, _audioStream, null, _audioAvPacket);
		}

		Logger.Debug("Freeing FFmpeg resources…");
		ffmpeg.sws_freeContext(_swsCtx);
		
		// 释放音频重采样资源
		if (_swrCtx != null)
		{
			var swrCtx = _swrCtx;
			ffmpeg.swr_free(&swrCtx);
			_swrCtx = null;
		}
		if (_resampledAudioFrame != null)
		{
			var resampledFrame = _resampledAudioFrame;
			ffmpeg.av_frame_free(&resampledFrame);
			_resampledAudioFrame = null;
		}
		
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
