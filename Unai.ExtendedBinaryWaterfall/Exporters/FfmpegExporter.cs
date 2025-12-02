using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using Lennox.LibYuvSharp;
using Microsoft.Extensions.ObjectPool;
using SkiaSharp;

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

// 编码质量预设（速度 vs 质量权衡）
public enum EncodingQualityPreset
{
	Speed,    // 速度优先
	Balanced, // 平衡
	Quality   // 质量
}

// 异步编码帧数据（实现 IResettable 以支持 ObjectPool 自动重置）
public class PendingFrame : IResettable
{
	public byte[] PixelData;        // 像素数据副本
	public int Width;
	public int Height;
	public float[] AudioSamples;    // 音频采样副本
	public int AudioSampleCount;
	public int AudioChannels;
	public long VideoPts;           // 视频 PTS
	public long AudioPts;           // 音频 PTS
	
	// IResettable 实现：归还到池时自动重置
	public bool TryReset()
	{
		PixelData = null;
		AudioSamples = null;
		AudioSampleCount = 0;
		return true;
	}
}

[Exporter("ffmpeg", "FFmpeg Stream", "Use FFmpeg libraries to encode audio and video data and output it in Matroska format.")]
public class FfmpegExporter : IExporter
{
	private bool _init = false;
	// 标记音频编码是否可用
	private bool _audioEnabled = true;
	// 实际使用的音频采样率（AAC 标准采样率）
	private int _actualAudioSampleRate = 48000;
	
	// 异步编码管道（使用 Channel 替代 BlockingCollection）
	private Channel<PendingFrame> _frameChannel;
	private Task _encoderTask;
	private CancellationTokenSource _encoderCts;
	private const int MaxQueueSize = 384;  // 增大缓冲区以改善推送延迟
	
	// 背压策略：DropOldest=丢弃旧帧保持实时性，Wait=等待确保所有帧
	public BoundedChannelFullMode ChannelBackpressureMode { get; set; } = BoundedChannelFullMode.Wait;
	
	// 对象池（替代 ConcurrentBag，减少锁竞争）
	private ObjectPool<PendingFrame> _framePool;
	private ConcurrentQueue<byte[]> _pixelBufferPool = new();
	private ConcurrentQueue<float[]> _audioBufferPool = new();
	
	// 预分配对象池
	private const int PreallocPoolSize = 256;  // 对象池最大保留数量

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
	
	// 性能诊断计时器
	private readonly System.Diagnostics.Stopwatch _perfTimer = new();
	// 使用滑动窗口统计（避免长时间运行后累加器精度问题）
	private const int PerfWindowSize = 100;
	private double _windowSwsTime = 0;
	private double _windowEncodeTime = 0;
	private double _windowAudioTime = 0;
	private int _windowSampleCount = 0;
	private int _totalFrameCount = 0;

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

	// NVENC 编码配置
	// P1=最快低质量, P3=快速平衡, P4=平衡, P6=质量优先, P7=最慢最高质量
	public string NvencPreset { get; set; } = "p1";       // P1 最快速度
	public string NvencTune { get; set; } = "ll";         // ll=低延迟, hq=高质量
	public string NvencRateControl { get; set; } = "vbr"; // vbr=可变比特率, cbr=恒定比特率
	public int NvencBFrames { get; set; } = 0;            // B帧数（0=禁用, 1-3=启用B帧提高压缩率）
	public bool NvencTemporalAQ { get; set; } = false;    // 时域AQ（改善场景切换质量）
	public bool NvencSpatialAQ { get; set; } = false;     // 空域AQ（改善复杂区域质量）
	public int NvencAQStrength { get; set; } = 0;         // AQ强度=0-15
	public int NvencLookahead { get; set; } = 0;          // Lookahead=0-32（10-16推荐用于质量优先）
	public bool NvencZeroLatency { get; set; } = true;    // 零延迟模式（禁用可改善质量）
	
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
	
	// 编码质量预设（用于快速切换速度/质量配置）
	public EncodingQualityPreset QualityPreset { get; set; } = EncodingQualityPreset.Speed;

	#endregion

	// 根据质量预设应用相应的 NVENC 配置
	public void ApplyQualityPreset(EncodingQualityPreset preset)
	{
		QualityPreset = preset;
		switch (preset)
		{
			case EncodingQualityPreset.Speed:
				// 速度优先：最快编码，最小延迟
				NvencPreset = "p1";           // 最快预设
				NvencTune = "ll";             // 低延迟调优
				NvencBFrames = 0;             // 禁用B帧（显著提高速度）
				NvencTemporalAQ = false;      // 禁用时域AQ
				NvencSpatialAQ = false;       // 禁用空域AQ
				NvencLookahead = 0;           // 禁用前瞻
				NvencZeroLatency = true;      // 零延迟模式
				ChannelBackpressureMode = BoundedChannelFullMode.Wait;  // 确保所有帧
				Logger.Info("已应用速度预设: p1, bf=0, zerolatency=on");
				break;
				
			case EncodingQualityPreset.Balanced:
				// 平衡模式：速度与质量的折中
				NvencPreset = "p3";           // 快速预设（比 p4 快，比 p1 质量更好）
				NvencTune = "hq";             // 高质量调优
				NvencBFrames = 1;             // 1个B帧（平衡压缩率和延迟）
				NvencTemporalAQ = true;       // 启用时域AQ
				NvencSpatialAQ = false;       // 禁用空域AQ
				NvencLookahead = 10;          // 10帧前瞻（NVIDIA 推荐）
				NvencZeroLatency = false;     // 允许适度延迟
				ChannelBackpressureMode = BoundedChannelFullMode.Wait;
				Logger.Info("已应用平衡预设: p3, bf=1, lookahead=10");
				break;
				
			case EncodingQualityPreset.Quality:
				// 质量优先：最佳压缩率和图像质量
				NvencPreset = "p6";           // 慢速预设（高质量）
				NvencTune = "hq";             // 高质量调优
				NvencBFrames = 3;             // 3个B帧（最佳压缩率）
				NvencTemporalAQ = true;       // 启用时域AQ
				NvencSpatialAQ = true;        // 启用空域AQ
				NvencAQStrength = 8;          // AQ强度
				NvencLookahead = 16;          // 16帧前瞻（最佳质量）
				NvencZeroLatency = false;     // 允许延迟
				ChannelBackpressureMode = BoundedChannelFullMode.Wait;
				Logger.Info("已应用质量预设: p6, bf=3, lookahead=16, spatial-aq=on");
				break;
		}
	}

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

	// 检测并列出所有可用的硬件编码器
	private unsafe void DetectAvailableEncoders()
	{
		string[] hwEncoders = { "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf" };
		var available = new System.Collections.Generic.List<string>();
		var unavailable = new System.Collections.Generic.List<string>();
		
		foreach (var name in hwEncoders)
		{
			var enc = ffmpeg.avcodec_find_encoder_by_name(name);
			if (enc != null)
				available.Add(name);
			else
				unavailable.Add(name);
		}
		
		Logger.Info($"可用硬件编码器: {(available.Count > 0 ? string.Join(", ", available) : "无")}");
		if (unavailable.Count > 0 && available.Count == 0)
		{
			Logger.Warning($"未检测到硬件编码器，将使用软件编码 (libx264)");
			Logger.Warning($"提示: 确保已安装支持 NVENC/QSV/AMF 的 FFmpeg 版本");
		}
	}
	
	// 尝试查找可用的硬件编码器，返回编码器名称
	private unsafe AVCodec* FindVideoEncoder()
	{
		// 首先检测所有可用编码器
		DetectAvailableEncoders();
		
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
				// 判断是否为硬件编码器
				bool isHwEncoder = encoderName.Contains("nvenc") || 
				                   encoderName.Contains("qsv") || 
				                   encoderName.Contains("amf");
				string hwType = isHwEncoder ? "GPU 硬件" : "CPU 软件";
				Logger.Info($"✓ 选择视频编码器: {encoderName} ({hwType}编码)");
				return encoder;
			}
			else
			{
				Logger.Debug($"  编码器 {encoderName} 不可用");
			}
		}

		// 回退到默认 H.264 编码器
		encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
		if (encoder != null)
		{
			Logger.Warning("⚠ 使用默认 H.264 软件编码器 (速度较慢)");
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
				ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, formatName, Generator.OutputFilePath ?? "/dev/stdout");
				if (fmtCtx == null)
				{
					Console.Error.WriteLine("cannot allocate AVFormatContext");
				}
				_fmtCtx = fmtCtx;
			}
			if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
			{
				Logger.Debug("Format requested global stream headers.");
			}

			// encoders
			// ========

			AVRational videoFps; videoFps.num = Generator.OutputFps; videoFps.den = 1;
			// 先计算实际使用的音频采样率（AAC 支持的标准采样率）
			_actualAudioSampleRate = GetNearestSupportedSampleRate(Generator.AudioOutputSampleRate);
			// 视频 time_base = 1/fps，PTS 直接使用帧号
			int videoTimeBaseDen = Generator.OutputFps;

			// 使用硬件加速编码器（如果可用）
			var videoEnc = FindVideoEncoder();
			if (videoEnc == null)
			{
				throw new InvalidOperationException("找不到可用的视频编码器！");
			}
			string vEncName = Marshal.PtrToStringAnsi((nint)videoEnc->name);
			
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
			// 设置关键帧间隔（GOP大小），默认每 2 秒一个关键帧以支持视频定位
			int gopSize = KeyframeInterval > 0 ? KeyframeInterval : Generator.OutputFps * 2;
			_videoCtx->gop_size = gopSize;
			_videoCtx->max_b_frames = NvencBFrames;
			Logger.Info($"视频 GOP 大小: {gopSize} 帧 (约 {gopSize / Generator.OutputFps} 秒)");
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
				// 预设名称（使用 FFmpeg 支持的标准名称）
				// p1=最快低质量, p3=快速平衡, p4=平衡, p6=质量优先, p7=最慢最高质量
				string preset = NvencPreset switch
				{
					"p1" => "p1",        // 最快预设
					"p2" => "p2",
					"p3" => "p3",
					"p4" => "p4",
					"p5" => "p5",
					"p6" => "p6",
					"p7" => "p7",
					_ => "p1"            // 默认使用最快预设
				};
				ffmpeg.av_dict_set(&videoEncOpts, "preset", preset, 0);
				
				// 调优模式（ll=低延迟, hq=高质量）
				string tune = NvencTune switch
				{
					"ll" => "ll",        // 低延迟
					"hq" => "hq",        // 高质量
					"lossless" => "lossless",  // 无损
					_ => "ll"
				};
				ffmpeg.av_dict_set(&videoEncOpts, "tune", tune, 0);
				
				// 码率控制
				string rc = NvencRateControl switch
				{
					"vbr" => "vbr",
					"cbr" => "cbr",
					"vbr_hq" => "vbr",
					"cbr_hq" => "cbr",
					_ => "vbr"
				};
				ffmpeg.av_dict_set(&videoEncOpts, "rc", rc, 0);
				
				// B帧配置（0=禁用提高速度，1-3=启用提高压缩率）
				ffmpeg.av_dict_set(&videoEncOpts, "bf", NvencBFrames.ToString(), 0);
				
				// B帧参考模式（0=禁用双向参考帧提高速度，middle=启用提高质量）
				if (NvencBFrames > 0)
					ffmpeg.av_dict_set(&videoEncOpts, "b_ref_mode", "middle", 0);
				else
					ffmpeg.av_dict_set(&videoEncOpts, "b_ref_mode", "0", 0);
				
				// 零延迟模式（禁用可改善压缩率和质量）
				if (NvencZeroLatency)
				{
					ffmpeg.av_dict_set(&videoEncOpts, "delay", "0", 0);
					ffmpeg.av_dict_set(&videoEncOpts, "zerolatency", "1", 0);
				}
				
				// Lookahead 前瞻分析（0=禁用，10-16=推荐用于质量优先）
				if (NvencLookahead > 0)
					ffmpeg.av_dict_set(&videoEncOpts, "rc-lookahead", NvencLookahead.ToString(), 0);
				
				// 自适应量化（改善复杂区域和场景切换的质量）
				if (NvencSpatialAQ)
				{
					ffmpeg.av_dict_set(&videoEncOpts, "spatial-aq", "1", 0);
					if (NvencAQStrength > 0)
						ffmpeg.av_dict_set(&videoEncOpts, "aq-strength", NvencAQStrength.ToString(), 0);
				}
				if (NvencTemporalAQ)
					ffmpeg.av_dict_set(&videoEncOpts, "temporal-aq", "1", 0);
				
				// GPU设备选择
				ffmpeg.av_dict_set(&videoEncOpts, "gpu", "0", 0);
				
				// 编码器表面缓冲区数量（平衡并行度和延迟）
				// 32 足够 60fps 编码，减少内存占用和延迟
				ffmpeg.av_dict_set(&videoEncOpts, "surfaces", "32", 0);
				
				// 强制关键帧以支持视频定位
				ffmpeg.av_dict_set(&videoEncOpts, "forced-idr", "1", 0);
				
				// 禁用场景切换检测（减少延迟波动）
				ffmpeg.av_dict_set(&videoEncOpts, "no-scenecut", "1", 0);
				
				// 严格 GOP（确保固定关键帧间隔）
				ffmpeg.av_dict_set(&videoEncOpts, "strict_gop", "1", 0);
				
				// 2pass 多码率编码分配（提高码率利用效率）
				ffmpeg.av_dict_set(&videoEncOpts, "multipass", "0", 0);
				
				// 禁用加权预测（减少编码复杂度）
				ffmpeg.av_dict_set(&videoEncOpts, "weighted_pred", "0", 0);
				
				Logger.Info($"NVENC 配置: preset={preset}, tune={tune}, rc={rc}, bf={NvencBFrames}, lookahead={NvencLookahead}, zerolatency={NvencZeroLatency}, surfaces=32");
			}
			else if (encoderName == "h264_qsv" || encoderName == "hevc_qsv")
			{
				// Intel QSV 选项
				ffmpeg.av_dict_set(&videoEncOpts, "preset", "veryfast", 0);
				ffmpeg.av_dict_set(&videoEncOpts, "low_power", "1", 0);  // 低功耗模式（更快）
				ffmpeg.av_dict_set(&videoEncOpts, "look_ahead", "0", 0); // 禁用前瞻
				Logger.Info($"QSV 选项已配置: preset=veryfast, low_power=1");
			}
			else if (encoderName == "h264_amf" || encoderName == "hevc_amf")
			{
				// AMD AMF 选项
				ffmpeg.av_dict_set(&videoEncOpts, "quality", "speed", 0);
				ffmpeg.av_dict_set(&videoEncOpts, "usage", "lowlatency", 0);  // 低延迟模式
				ffmpeg.av_dict_set(&videoEncOpts, "preanalysis", "0", 0);     // 禁用预分析
				Logger.Info($"AMF 选项已配置: quality=speed, usage=lowlatency");
			}
			
			var ret = ffmpeg.avcodec_open2(_videoCtx, videoEnc, &videoEncOpts);
			if (ret < 0)
			{
				FfmpegUtils.LogIfAvError(ret, "cannot open video codec");
				throw new InvalidOperationException($"无法打开视频编码器 {encoderName}，错误码: {ret}");
			}

			_audioCtx = ffmpeg.avcodec_alloc_context3(audioEnc);
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
			ret = ffmpeg.avcodec_open2(_audioCtx, audioEnc, null);
			if (ret < 0)
			{
				_audioEnabled = false;
			}
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

			// 只在音频编码器可用时创建音频流
			if (_audioEnabled)
			{
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

			// audio frames (只在音频编码器可用时初始化)
			// ============
			if (_audioEnabled)
			{
				
				// 输入采样率是音频解码器的原始采样率
				_inputAudioSampleRate = Generator.AudioDecoderSampleRate;
				// 输出采样率是用户期望的目标采样率（已经过 AAC 兼容性检查）
				_actualAudioSampleRate = GetNearestSupportedSampleRate(Generator.AudioOutputSampleRate);
				// 当输入和输出采样率不同时需要重采样
				_needResample = (_inputAudioSampleRate != _actualAudioSampleRate);
				
				// Generator 每帧产生的采样数（基于解码器采样率）
				// 解码器每帧产生: AudioDecoderSampleRate / OutputFps 个采样（每通道）
				int inputSamplesPerChannel = Generator.AudioDecoderSampleRate / Generator.OutputFps;
				
				// 输入音频帧缓冲区大小需要足够容纳 AAC 帧（通常 1024 采样）
				int audioFrameBufferSize = Math.Max(inputSamplesPerChannel, _audioCtx->frame_size);
				
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

				ret = ffmpeg.av_frame_get_buffer(_audioAvFrame, 0);
				FfmpegUtils.LogIfAvError(ret, "cannot allocate input audio sample buffer");
				
				// 分配用于编码的音频帧
				_resampledAudioFrame = ffmpeg.av_frame_alloc();
				_resampledAudioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
				ffmpeg.av_channel_layout_default(&_resampledAudioFrame->ch_layout, outputChannels);
				_resampledAudioFrame->sample_rate = _actualAudioSampleRate;
				_resampledAudioFrame->nb_samples = _audioCtx->frame_size;
				_resampledAudioFrame->time_base.num = 1;
				_resampledAudioFrame->time_base.den = _actualAudioSampleRate;
				
				ret = ffmpeg.av_frame_get_buffer(_resampledAudioFrame, 0);
				FfmpegUtils.LogIfAvError(ret, "cannot allocate audio encode buffer");
				
				if (_needResample)
				{
					// 初始化重采样上下文（在 unsafe 上下文中局部变量已固定）
					// 输入：使用解码器的声道数
					// 输出：使用用户设置的输出声道数，重采样器会自动upmix/downmix
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
				}

				// 检查音频编码器是否支持可变帧大小
				if (_audioCtx->codec != null && (_audioCtx->codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) == 0)
				{
					Logger.Warning("audio codec does not support variable frame size");
				}

				// 音频队列缓冲长度 = 每通道采样数 × 实际声道数
				int audioQueueLength = inputSamplesPerChannel * inputChannels;
				_audioQueue.BufferLength = audioQueueLength;
				
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

	// 接受 SKBitmap 作为视频帧
	public unsafe void PushNewFrame(SKBitmap videoFrame, AudioBuffer audioFrame, double delta)
	{
		if (!_init)
		{
			InitializeFfmpeg();
			StartAsyncEncoder();
		}
		
		// 获取帧数据对象（从 ObjectPool）
		var pendingFrame = _framePool.Get();
		
		// 获取或创建像素缓冲区（从队列池）
		int pixelSize = videoFrame.Width * videoFrame.Height * 4;
		if (!_pixelBufferPool.TryDequeue(out var pixelBuffer) || pixelBuffer.Length < pixelSize)
		{
			pixelBuffer = new byte[pixelSize];
		}
		
		// 从 SKBitmap 复制像素数据
		ReadOnlySpan<byte> pixels = videoFrame.GetPixelSpan();
		pixels.CopyTo(pixelBuffer);
		pendingFrame.PixelData = pixelBuffer;
		pendingFrame.Width = videoFrame.Width;
		pendingFrame.Height = videoFrame.Height;
		
		// 复制音频数据（使用 CopyTo 避免 ToArray 分配）
		if (_audioEnabled && audioFrame != null)
		{
			int audioLen = audioFrame.TotalSampleCount;
			if (!_audioBufferPool.TryDequeue(out var audioBuffer) || audioBuffer.Length < audioLen)
			{
				audioBuffer = new float[audioLen];
			}
			audioFrame.CopyTo(audioBuffer);
			pendingFrame.AudioSamples = audioBuffer;
			pendingFrame.AudioSampleCount = audioLen;
			pendingFrame.AudioChannels = audioFrame.ChannelCount;
		}
		else
		{
			pendingFrame.AudioSamples = null;
		}
		
		// 设置 PTS
		pendingFrame.VideoPts = _frameNum;
		
		// 将帧加入编码 Channel（非阻塞）
		// 使用 ValueTask 避免分配，且不阻塞渲染线程
		var writeTask = _frameChannel.Writer.WriteAsync(pendingFrame);
		if (!writeTask.IsCompletedSuccessfully)
		{
			// 如果不能立即写入，使用异步等待但不阻塞主线程
			// 通过 SpinWait 短暂等待，避免线程切换开销
			var spinner = new SpinWait();
			while (!writeTask.IsCompleted)
			{
				spinner.SpinOnce();
				if (spinner.NextSpinWillYield)
				{
					// 已经自旋足够多次，让出 CPU
					Thread.Sleep(0);
				}
			}
		}

		if (_frameNum % 50 == 0)
		{
			Logger.Trace($"frame {_frameNum}, ts {_frameNum / Generator.OutputFps}, speed {(int)(1/delta)} fps\x1b[K\x1b[G");
		}

		_frameNum++;
	}
	
	// 启动异步编码线程
	private void StartAsyncEncoder()
	{
		// 初始化 ObjectPool
		var poolPolicy = new DefaultPooledObjectPolicy<PendingFrame>();
		_framePool = new DefaultObjectPool<PendingFrame>(poolPolicy, PreallocPoolSize);
		
		// 使用 Channel（比 BlockingCollection 快 2-3 倍）
		// 背压策略：Wait=等待确保所有帧, DropOldest=丢弃旧帧保持实时性
		_frameChannel = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(MaxQueueSize)
		{
			FullMode = ChannelBackpressureMode,
			SingleReader = true,
			SingleWriter = true
		});
		_encoderCts = new CancellationTokenSource();
		
		// 预分配缓冲区池（减少 GC）
		// 注意：_framePool 是 ObjectPool，会自动管理对象创建
		int pixelSize = Generator.OutputVideoWidth * Generator.OutputVideoHeight * 4;
		int audioSize = Generator.AudioDecoderSampleRate / Generator.OutputFps * Generator.AudioDecoderChannelCount * 2;
		for (int i = 0; i < PreallocPoolSize; i++)
		{
			_pixelBufferPool.Enqueue(new byte[pixelSize]);
			_audioBufferPool.Enqueue(new float[audioSize]);
		}
		
		// 强制进行一次完整 GC，清理启动时的临时对象
		GC.Collect(2, GCCollectionMode.Forced, true, true);
		GC.WaitForPendingFinalizers();
		
		// 高优先级线程运行编码器
		// 使用 Task.Run + Unwrap 确保 async Task 被正确等待
		_encoderTask = Task.Factory.StartNew(
			() => AsyncEncoderLoop(), 
			_encoderCts.Token, 
			TaskCreationOptions.LongRunning, 
			TaskScheduler.Default
		).Unwrap();
		
		Logger.Info($"编码器已启动，Channel 缓冲: {MaxQueueSize} 帧，预分配池: {PreallocPoolSize} 对象");
	}
	
	// 异步编码循环（在后台线程运行）
	private async Task AsyncEncoderLoop()
	{
		// 设置线程优先级为高
		Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
		
		try
		{
			await foreach (var frame in _frameChannel.Reader.ReadAllAsync(_encoderCts.Token))
			{
				try
				{
					EncodeFrameSync(frame);
				}
				catch (Exception encodeEx)
				{
					Logger.Error($"编码帧失败: {encodeEx.Message}");
				}
				finally
				{
					ReturnToPool(frame);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// 正常取消，Channel 关闭时会触发
			Logger.Debug("编码器循环正常结束");
		}
		catch (ChannelClosedException)
		{
			// Channel 已关闭，正常结束
			Logger.Debug("编码 Channel 已关闭");
		}
		catch (Exception ex)
		{
			Logger.Error($"异步编码器错误: {ex.Message}\n{ex.StackTrace}");
		}
	}
	
	// 同步编码单帧
	private unsafe void EncodeFrameSync(PendingFrame frame)
	{
		var ret = ffmpeg.av_frame_make_writable(_videoAvFrame);
		FfmpegUtils.LogIfAvError(ret, "cannot make video pixel data writable");
		ret = ffmpeg.av_frame_make_writable(_audioAvFrame);
		FfmpegUtils.LogIfAvError(ret, "cannot make audio sample buffer writable");

		// 计时：色彩空间转换（使用 libyuv，比 sws_scale 快 4-5 倍）
		_perfTimer.Restart();
		{
			int srcStride = frame.Width * 4;
			int yStride = _videoAvFrame->linesize[0];
			int uStride = _videoAvFrame->linesize[1];
			int vStride = _videoAvFrame->linesize[2];
			
			fixed (byte* srcData = frame.PixelData)
			{
				// SkiaSharp BGRA8888 在内存中是 B-G-R-A
				// libyuv 的 ARGBToI420 期望 little-endian ARGB（即内存中 B-G-R-A）
				LibYuv.ARGBToI420(
					srcData, srcStride,
					_videoAvFrame->data[0], yStride,
					_videoAvFrame->data[1], uStride,
					_videoAvFrame->data[2], vStride,
					frame.Width, frame.Height);
			}
		}
		double swsTime = _perfTimer.Elapsed.TotalMilliseconds;
		_windowSwsTime += swsTime;

		_videoAvFrame->time_base.num = _videoCtx->time_base.num;
		_videoAvFrame->time_base.den = _videoCtx->time_base.den;
		_videoAvFrame->pts = frame.VideoPts;
		_videoAvFrame->duration = 1;
		
		// 计时：视频编码
		_perfTimer.Restart();
		DoEncode(_videoCtx, _videoStream, _videoAvFrame, _videoAvPacket);
		double encodeTime = _perfTimer.Elapsed.TotalMilliseconds;
		_windowEncodeTime += encodeTime;

		// 计时：音频处理
		_perfTimer.Restart();
		if (_audioEnabled && frame.AudioSamples != null && frame.AudioSampleCount > 0)
		{
			// 直接使用 ReadOnlySpan 避免内存分配（AudioSamples 已经是从对象池获取的缓冲区）
			// 使用 AsSpan 创建视图，避免 .ToArray() 导致的内存分配
			ReadOnlySpan<float> audioSpan = frame.AudioSamples.AsSpan(0, frame.AudioSampleCount);
			_audioQueue.Push(audioSpan);
		}
		double audioTime = _perfTimer.Elapsed.TotalMilliseconds;
		_windowAudioTime += audioTime;
		
		// 滑动窗口性能统计（每 100 帧重置，避免累加器精度问题）
		_windowSampleCount++;
		_totalFrameCount++;
		if (_windowSampleCount >= PerfWindowSize)
		{
			double avgSws = _windowSwsTime / _windowSampleCount;
			double avgEncode = _windowEncodeTime / _windowSampleCount;
			double avgAudio = _windowAudioTime / _windowSampleCount;
			Logger.Info($"[性能] sws_scale: {avgSws:F2}ms, 编码: {avgEncode:F2}ms, 音频: {avgAudio:F2}ms (avg/frame, window={_totalFrameCount})");
			
			// 重置滑动窗口
			_windowSwsTime = 0;
			_windowEncodeTime = 0;
			_windowAudioTime = 0;
			_windowSampleCount = 0;
		}
	}
	
	// 将帧数据对象归还到对象池
	private void ReturnToPool(PendingFrame frame)
	{
		// 像素缓冲区归还到队列池
		if (frame.PixelData != null)
		{
			_pixelBufferPool.Enqueue(frame.PixelData);
			frame.PixelData = null;
		}
		// 音频缓冲区归还到队列池
		if (frame.AudioSamples != null)
		{
			_audioBufferPool.Enqueue(frame.AudioSamples);
			frame.AudioSamples = null;
		}
		// 帧对象归还到 ObjectPool（会自动调用 TryReset）
		_framePool.Return(frame);
	}

	public unsafe void Finish()
	{
		try
		{
			FinishInternal();
		}
		catch (Exception ex)
		{
			Logger.Error($"Finish 方法异常: {ex.Message}\n{ex.StackTrace}");
		}
	}
	
	// 内部完成方法（包含实际清理逻辑）
	private unsafe void FinishInternal()
	{
		Logger.Debug("等待异步编码器完成...");
		
		// 停止接受新帧并等待 Channel 清空
		if (_frameChannel != null)
		{
			try
			{
				_frameChannel.Writer.Complete();
			}
			catch { }
			
			try
			{
				// 等待编码器线程完成所有待处理帧
				_encoderTask?.Wait(TimeSpan.FromMinutes(5));
			}
			catch (AggregateException ex)
			{
				Logger.Error($"编码器线程异常: {ex.InnerException?.Message}");
			}
			catch (Exception ex)
			{
				Logger.Error($"等待编码器异常: {ex.Message}");
			}
			finally
			{
				try { _encoderCts?.Cancel(); } catch { }
				try { _encoderCts?.Dispose(); } catch { }
			}
		}
		
		Logger.Debug("Flushing streams…");
		// 刷新视频编码器（发送 null 帧触发编码器输出所有缓冲的帧）
		if (_videoCtx != null && _videoStream != null && _videoAvPacket != null)
		{
			try
			{
				DoEncode(_videoCtx, _videoStream, null, _videoAvPacket);
			}
			catch (Exception ex)
			{
				Logger.Error($"视频流刷新失败: {ex.Message}");
			}
		}
		// 只在音频编码器可用时刷新音频流
		if (_audioEnabled && _audioCtx != null && _audioStream != null && _audioAvPacket != null)
		{
			try
			{
				DoEncode(_audioCtx, _audioStream, null, _audioAvPacket);
			}
			catch (Exception ex)
			{
				Logger.Error($"音频流刷新失败: {ex.Message}");
			}
		}

		Logger.Debug("Freeing FFmpeg resources…");
		
		// 重要：必须先写入文件尾（在释放编码器之前）
		if (_fmtCtx != null)
		{
			try
			{
				ffmpeg.av_write_trailer(_fmtCtx);
			}
			catch (Exception ex)
			{
				Logger.Error($"av_write_trailer 失败: {ex.Message}");
			}
		}
		
		// 释放视频帧和包（添加 try-catch 防止原生崩溃）
		try
		{
			if (_videoAvFrame != null)
			{
				var frame = _videoAvFrame;
				ffmpeg.av_frame_free(&frame);
				_videoAvFrame = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 videoAvFrame 失败: {ex.Message}"); }
		
		try
		{
			if (_videoAvFramePre != null)
			{
				var frame = _videoAvFramePre;
				ffmpeg.av_frame_free(&frame);
				_videoAvFramePre = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 videoAvFramePre 失败: {ex.Message}"); }
		
		try
		{
			if (_videoAvPacket != null)
			{
				var packet = _videoAvPacket;
				ffmpeg.av_packet_free(&packet);
				_videoAvPacket = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 videoAvPacket 失败: {ex.Message}"); }
		
		// 释放音频帧和包
		try
		{
			if (_audioAvFrame != null)
			{
				var frame = _audioAvFrame;
				ffmpeg.av_frame_free(&frame);
				_audioAvFrame = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 audioAvFrame 失败: {ex.Message}"); }
		
		try
		{
			if (_audioAvPacket != null)
			{
				var packet = _audioAvPacket;
				ffmpeg.av_packet_free(&packet);
				_audioAvPacket = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 audioAvPacket 失败: {ex.Message}"); }
		
		// 释放色彩转换上下文
		try
		{
			if (_swsCtx != null)
			{
				ffmpeg.sws_freeContext(_swsCtx);
				_swsCtx = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 swsCtx 失败: {ex.Message}"); }
		
		// 释放音频重采样资源
		try
		{
			if (_swrCtx != null)
			{
				var swrCtx = _swrCtx;
				ffmpeg.swr_free(&swrCtx);
				_swrCtx = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 swrCtx 失败: {ex.Message}"); }
		
		try
		{
			if (_resampledAudioFrame != null)
			{
				var resampledFrame = _resampledAudioFrame;
				ffmpeg.av_frame_free(&resampledFrame);
				_resampledAudioFrame = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 resampledAudioFrame 失败: {ex.Message}"); }
		
		// 释放视频编码器上下文
		try
		{
			if (_videoCtx != null)
			{
				var videoCtx = _videoCtx;
				ffmpeg.avcodec_free_context(&videoCtx);
				_videoCtx = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 videoCtx 失败: {ex.Message}"); }
		
		// 释放音频编码器上下文
		try
		{
			if (_audioCtx != null)
			{
				var audioCtx = _audioCtx;
				ffmpeg.avcodec_free_context(&audioCtx);
				_audioCtx = null;
			}
		}
		catch (Exception ex) { Logger.Error($"释放 audioCtx 失败: {ex.Message}"); }

		// 关闭输出文件并释放格式上下文
		if (_fmtCtx != null)
		{
			try
			{
				if ((_fmtCtx->flags & ffmpeg.AVFMT_NOFILE) == 0 && _fmtCtx->pb != null)
				{
					ffmpeg.avio_closep(&_fmtCtx->pb);
				}
			}
			catch (Exception ex)
			{
				Logger.Error($"avio_closep 失败: {ex.Message}");
			}

			try
			{
				ffmpeg.avformat_free_context(_fmtCtx);
			}
			catch (Exception ex)
			{
				Logger.Error($"avformat_free_context 失败: {ex.Message}");
			}
			_fmtCtx = null;
		}
		
		Logger.Info("FFmpeg 资源释放完成");
	}
}
