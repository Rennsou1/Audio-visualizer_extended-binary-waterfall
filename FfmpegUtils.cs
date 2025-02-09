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
			Console.Error.WriteLine($"\x1b[91mFFmpeg error: {message}: {Marshal.PtrToStringUTF8((nint)errbuf)} ({errorCode})\x1b[0m");
			ffmpeg.av_free(errbuf);
		}
	}

	public static void LogCall(string s)
	{
		StackFrame sf = new(1);
		Console.Error.WriteLine($" <--- {sf.GetMethod().Name} {s}");
	}

	public static AVRational GetRational(int num, int den)
	{
		AVRational ret;
		ret.num = num;
		ret.den = den;
		return ret;
	}

	public unsafe static void LogPacketData(AVPacket* packet)
	{
		Console.Error.WriteLine($"pkt: str={packet->stream_index} pts={packet->pts} dts={packet->dts} dur={packet->duration} tb={packet->time_base.num}/{packet->time_base.den}");
	}
}
