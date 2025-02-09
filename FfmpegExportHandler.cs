using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Unai.ExtendedBinaryWaterfall;

public class FfmpegExportHandler : ExportHandler
{
	private bool _init = false;
	private bool _quit = false;

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

	private byte[] _audioQueue = null;
	private long _audioQueueOfs = 0;

	private int _frameNum = 0;

	public void InitializeFfmpeg()
	{
		unsafe
		{
			if (Environment.OSVersion.Platform != PlatformID.Win32NT) ffmpeg.RootPath = "/usr/lib";
			
			// ffmpeg.av_log_set_level(ffmpeg.AV_LOG_DEBUG);
			av_log_set_callback_callback logCb = (p0, level, format, v1) =>
			{
				if (level > ffmpeg.av_log_get_level()) return;
				var messageBufferLen = 1024;
				var messageBuffer = stackalloc byte[messageBufferLen];
				var printPrefix = 1;
				ffmpeg.av_log_format_line(p0, level, format, v1, messageBuffer, messageBufferLen, &printPrefix);
				var message = Marshal.PtrToStringAnsi((nint)messageBuffer);
				Console.Error.Write(message);
			};
			ffmpeg.av_log_set_callback(logCb);

			// format
			// ======

			AVFormatContext* fmtCtx = null;
			ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "matroska", "/dev/stdout");
			if (fmtCtx == null) Console.Error.WriteLine("cannot allocate AVFormatContext");
			_fmtCtx = fmtCtx;
			if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
			{
				Debug.WriteLine("format requested global stream headers");
			}

			// encoders
			// ========

			AVRational videoFps; videoFps.num = 60; videoFps.den = 1;

			var videoEnc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
			var audioEnc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);

			_videoCtx = ffmpeg.avcodec_alloc_context3(videoEnc);
			_videoCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
			_videoCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
			_videoCtx->width = 1920;
			_videoCtx->height = 1080;
			_videoCtx->time_base.num = 1;
			_videoCtx->time_base.den = videoFps.num;
			_videoCtx->framerate.num = videoFps.num;
			_videoCtx->framerate.den = videoFps.den;
			_videoCtx->bit_rate = 6_000_000;
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
			AVDictionary* videoEncOpts;
			var ret = ffmpeg.avcodec_open2(_videoCtx, videoEnc, &videoEncOpts);
			FfmpegUtils.LogIfAvError(ret, "cannot open video codec");

			_audioCtx = ffmpeg.avcodec_alloc_context3(audioEnc);
			_audioCtx->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
			_audioCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
			_audioCtx->sample_rate = 48000;
			_audioCtx->time_base.num = 1;
			_audioCtx->time_base.den = 48000;
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
			if (_videoStream == null) Console.Error.WriteLine("cannot allocate video output stream");
			_videoStream->index = (int)(_fmtCtx->nb_streams - 1);
			_videoStream->time_base = _videoCtx->time_base;
			_videoStream->r_frame_rate = videoFps;

			ret = ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _videoCtx);
			FfmpegUtils.LogIfAvError(ret, "cannot set video codec params from codec context");

			_audioStream = ffmpeg.avformat_new_stream(_fmtCtx, null);
			if (_videoStream == null) Console.Error.WriteLine("cannot allocate audio output stream");
			_audioStream->index = (int)(_fmtCtx->nb_streams - 1);
			_audioStream->time_base = FfmpegUtils.GetRational(1, 48000);
			
			ret = ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audioCtx);
			FfmpegUtils.LogIfAvError(ret, "cannot set audio codec params from codec context");
			if (_audioStream->codecpar->extradata == null)
			{
				Console.Error.WriteLine("audio codec did not create extradata buffer");
			}

			// output file/stream
			// ==================

			ret = ffmpeg.avio_open(&_fmtCtx->pb, "pipe:", ffmpeg.AVIO_FLAG_WRITE);
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
			_videoAvFrame->width = 1920;
			_videoAvFrame->height = 1080;
			_videoAvFrame->time_base = _videoStream->time_base;

			ret = ffmpeg.av_frame_get_buffer(_videoAvFrame, 0);
			FfmpegUtils.LogIfAvError(ret, "cannot allocate video pixel buffer");

			_videoAvFramePre = ffmpeg.av_frame_alloc();
			_videoAvFramePre->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
			_videoAvFramePre->width = 1920;
			_videoAvFramePre->height = 1080;
			_videoAvFramePre->time_base = _videoStream->time_base;

			ret = ffmpeg.av_frame_get_buffer(_videoAvFramePre, 0);
			FfmpegUtils.LogIfAvError(ret, "cannot allocate video pixel buffer");

			// audio frames
			// ============

			_audioAvFrame = ffmpeg.av_frame_alloc();
			_audioAvFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
			ffmpeg.av_channel_layout_copy(&_audioAvFrame->ch_layout, &_audioCtx->ch_layout);
			_audioAvFrame->sample_rate = 48000;
			_audioAvFrame->nb_samples = _audioCtx->frame_size;
			_audioAvFrame->ch_layout.nb_channels = 2;
			_audioAvFrame->ch_layout.u.mask = 3;
			_audioAvFrame->time_base = _audioStream->time_base;

			if ((_audioCtx->codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) == 0)
			{
				Console.Error.WriteLine("audio codec does not support variable frame size");
			}

			ret = ffmpeg.av_frame_get_buffer(_audioAvFrame, 0);
			FfmpegUtils.LogIfAvError(ret, "cannot allocate audio sample buffer");
			_audioQueue = new byte[_audioAvFrame->nb_samples * _audioAvFrame->ch_layout.nb_channels];

			// Console.Error.WriteLine($"original linesize = {_videoAvFramePre->linesize[0]} {_videoAvFramePre->linesize[1]}");
			// Console.Error.WriteLine($"target linesize =   {_videoAvFrame->linesize[0]} {_videoAvFrame->linesize[1]} {_videoAvFrame->linesize[2]}");
			// Console.Error.WriteLine($"req. frame size =   {_audioCtx->frame_size} * {_audioCtx->ch_layout.nb_channels}ch");
			// Console.Error.WriteLine($"ch layout =         {_audioAvFrame->ch_layout.nb_channels} {_audioAvFrame->ch_layout.order} {_audioAvFrame->ch_layout.u.mask}");
			// Console.Error.WriteLine($"audio linesizes =   {_audioAvFrame->linesize[0]} {_audioAvFrame->linesize[1]} {_audioAvFrame->linesize[2]} {_audioAvFrame->linesize[3]} {_audioAvFrame->linesize[4]} {_audioAvFrame->linesize[5]} {_audioAvFrame->linesize[6]} {_audioAvFrame->linesize[7]}");

			_videoAvPacket = ffmpeg.av_packet_alloc();
			_audioAvPacket = ffmpeg.av_packet_alloc();

			ffmpeg.av_dump_format(_fmtCtx, 0, "pipe:", 1);
		}
		_init = true;
	}

	private unsafe void DoEncode(AVCodecContext* cCtx, AVStream* stream, AVFrame* frame, AVPacket* packet)
	{
		int ret;

		// if (frame != null)
		// {
		// 	Console.Error.WriteLine($"frm: str={stream->index} pts={frame->pts} dts=n/a dur={frame->duration} tb={frame->time_base.num}/{frame->time_base.den}");
		// }

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
				FfmpegUtils.LogIfAvError(ret, "cannot encode");
				break;
			}

			ffmpeg.av_packet_rescale_ts(packet, cCtx->time_base, stream->time_base);
			packet->stream_index = stream->index;
			packet->time_base.num = stream->time_base.num;
			packet->time_base.den = stream->time_base.den;
			// FfmpegUtils.LogPacketData(packet);

			ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, packet);
			FfmpegUtils.LogIfAvError(ret, "cannot write packet");
			if (ret == -32) _quit = true;
		}
	}

	public override void PushNewFrame(Image videoFrame, byte[] audioFrame, double delta)
	{
		PushNewFrame((Image<Rgba32>)videoFrame, audioFrame, delta);
	}

	public unsafe void PushNewFrame(Image<Rgba32> videoFrame, byte[] audioFrame, double delta)
	{
		if (!_init)
		{
			InitializeFfmpeg();
		}

		if (_quit)
		{
			return;
		}

		var ret = ffmpeg.av_frame_make_writable(_videoAvFrame);
		FfmpegUtils.LogIfAvError(ret, "cannot make video pixel data writable");
		ret = ffmpeg.av_frame_make_writable(_audioAvFrame);
		FfmpegUtils.LogIfAvError(ret, "cannot make audio sample buffer writable");

		_audioAvFrame->time_base.num = _audioCtx->time_base.num;
		_audioAvFrame->time_base.den = _audioCtx->time_base.den;
		_audioAvFrame->pts = (long)(_audioAvFrame->sample_rate * (_frameNum / (float)Program.OutputFps));
		_audioAvFrame->duration = 48000 / 1024;

		if (_swsCtx == null)
		{
			_swsCtx = ffmpeg.sws_getContext(videoFrame.Width, videoFrame.Height, (AVPixelFormat)_videoAvFramePre->format, videoFrame.Width, videoFrame.Height, (AVPixelFormat)_videoAvFrame->format, ffmpeg.SWS_BILINEAR, null, null, null);
			if (_swsCtx == null)
			{
				Console.Error.WriteLine("cannot initialize sws context");
			}
		}

		if (_swsCtx != null)
		{
			var pixelData = new byte[videoFrame.Width * videoFrame.Height * 4];
			videoFrame.CopyPixelDataTo(pixelData);

			Marshal.Copy(pixelData, 0, (nint)_videoAvFramePre->data[0], pixelData.Length);
			ffmpeg.sws_scale(_swsCtx, _videoAvFramePre->data, _videoAvFramePre->linesize, 0, _videoAvFramePre->height, _videoAvFrame->data, _videoAvFrame->linesize);
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

		// Console.Error.WriteLine($"\n{(ulong)_videoAvFrame->data[0]:X16} {(ulong)_videoAvFrame->data[1]:X16} {(ulong)_videoAvFrame->data[2]:X16} {_videoAvFrame->width}x{_videoAvFrame->height} {_videoAvFrame->format:X}");

		_videoAvFrame->time_base.num = _videoCtx->time_base.num;
		_videoAvFrame->time_base.den = _videoCtx->time_base.den;
		_videoAvFrame->pts = _frameNum;
		_videoAvFrame->duration = 1;
		DoEncode(_videoCtx, _videoStream, _videoAvFrame, _videoAvPacket);

		var newAudioBufferOfs = _audioQueueOfs + audioFrame.Length;
		if (newAudioBufferOfs >= _audioQueue.Length)
		{
			var firstHalfSize = _audioQueue.Length - _audioQueueOfs;
			var secondHalfSize = Math.Abs(_audioQueue.Length - newAudioBufferOfs);
			Array.Copy(audioFrame, 0, _audioQueue, _audioQueueOfs, firstHalfSize);
			
			_audioQueueOfs = 0;

			float* ab0 = (float*)_audioAvFrame->data[0];
			float* ab1 = (float*)_audioAvFrame->data[1];
			for (int i = 0; i < _audioAvFrame->linesize[0] / sizeof(float); i++)
			{
				ab0[i] = (_audioQueue[i * 2] - 128) / 128f;
				ab1[i] = (_audioQueue[i * 2 + 1] - 128) / 128f;
			}
			DoEncode(_audioCtx, _audioStream, _audioAvFrame, _audioAvPacket);
			
			Array.Copy(audioFrame, firstHalfSize, _audioQueue, 0, secondHalfSize);
			_audioQueueOfs = secondHalfSize;
		}
		else
		{
			Array.Copy(audioFrame, 0, _audioQueue, _audioQueueOfs, audioFrame.Length);
			_audioQueueOfs += audioFrame.Length;
		}
		Debug.WriteLine($"audio buf status: filled {_audioQueueOfs}/{_audioQueue.Length} {_audioQueue.Length - _audioQueueOfs} bytes left");

		if (_frameNum % 10 == 0)
		{
			Console.Error.Write($"frame {_frameNum}, ts {_frameNum / 60}, framegen speed {(int)(1/delta)} fps\x1b[K\x1b[G");
		}

		_frameNum++;
	}

	public override unsafe void Finish()
	{
		DoEncode(_videoCtx, _videoStream, null, _videoAvPacket);
		DoEncode(_audioCtx, _audioStream, null, _audioAvPacket);

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