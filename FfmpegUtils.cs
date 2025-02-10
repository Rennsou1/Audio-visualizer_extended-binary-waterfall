using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Unai.ExtendedBinaryWaterfall;

public static class FfmpegUtils
{
	public unsafe static void LogIfAvError(int errorCode, string message)
	{
		if (errorCode < 0)
		{
			byte* errbuf = (byte*)ffmpeg.av_malloc(1024);
			ffmpeg.av_make_error_string(errbuf, 1024, errorCode);
			Logger.Error($"FFmpeg error {errorCode}: {message}: {Marshal.PtrToStringUTF8((nint)errbuf)}");
			ffmpeg.av_free(errbuf);
		}
	}

	public static AVRational GetRational(int num, int den)
	{
		AVRational ret;
		ret.num = num;
		ret.den = den;
		return ret;
	}

	public unsafe static void LogFrameData(AVFrame* frame)
	{
		if (frame != null)
		{
			Logger.Trace($"frm: pts={frame->pts} dur={frame->duration} tb={frame->time_base.num}/{frame->time_base.den}");
		}
	}

	public unsafe static void LogPacketData(AVPacket* packet)
	{
		Logger.Trace($"pkt: str={packet->stream_index} pts={packet->pts} dts={packet->dts} dur={packet->duration} tb={packet->time_base.num}/{packet->time_base.den}");
	}
}
