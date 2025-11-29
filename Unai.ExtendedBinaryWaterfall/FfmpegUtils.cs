using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Unai.ExtendedBinaryWaterfall;

public static class FfmpegUtils
{
	private static readonly string[] _ffmpegSearchPathsLinux =
	[
		"/usr/lib",
		"/usr/lib64",
		"/usr/lib32",
		"/lib",
		"/lib64",
		"/lib32"
	];

	private static readonly string[] _ffmpegSearchPathsWindows =
	[
		"C:\\ffmpeg",
		"C:\\ffmpeg\\bin"
	];

	public static string GetFfmpegLibraryPath()
	{
		Logger.Debug("Guessing FFmpeg library path…");
		IEnumerable<string> ret;

		if (Environment.OSVersion.Platform == PlatformID.Win32NT)
		{
			// 搜索预定义路径
			ret = _ffmpegSearchPathsWindows
				.Where(Directory.Exists)
				.Where(x => Directory.GetFiles(x, "*avcodec-*.dll").Length > 0);

			if (ret.Any())
			{
				Logger.Debug($"Found FFmpeg in predefined path: {ret.First()}");
				return ret.First();
			}

			// 搜索 WinGet 安装目录
			Logger.Debug("Searching WinGet packages folder…");
			var wingetPath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Microsoft", "WinGet", "Packages");
			
			if (Directory.Exists(wingetPath))
			{
				try
				{
					var ffmpegDirs = Directory.GetDirectories(wingetPath, "*FFmpeg*", SearchOption.TopDirectoryOnly);
					foreach (var ffmpegDir in ffmpegDirs)
					{
						// 递归查找包含 avcodec DLL 的目录
						var dllDirs = Directory.GetFiles(ffmpegDir, "*avcodec*.dll", SearchOption.AllDirectories);
						if (dllDirs.Length > 0)
						{
							var dllDir = Path.GetDirectoryName(dllDirs[0]);
							Logger.Debug($"Found FFmpeg in WinGet: {dllDir}");
							return dllDir;
						}
					}
				}
				catch (Exception ex)
				{
					Logger.Debug($"Error searching WinGet folder: {ex.Message}");
				}
			}

			// 搜索 PATH 环境变量
			Logger.Debug("Search via predefined paths failed. Trying PATH environment variable…");
			var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
			ret = pathVar
				.Split(';')
				.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
				.Where(p => {
					try { return Directory.GetFiles(p, "avcodec*.dll").Length > 0; }
					catch { return false; }
				});

			if (ret.Any())
			{
				Logger.Debug($"Found FFmpeg in PATH: {ret.First()}");
				return ret.First();
			}
		}
		else
		{
			ret = _ffmpegSearchPathsLinux
				.Where(Directory.Exists)
				.Where(x => File.Exists($"{x}/libavcodec.so"));
			
			if (ret.Any())
			{
				return ret.First();
			}
		}

		Logger.Error("Cannot determine folder path containing FFmpeg libraries.");
		if (Environment.OSVersion.Platform == PlatformID.Win32NT)
		{
			Logger.Info("Please enter the following command to install FFmpeg libraries:");
			Logger.Info("	winget install \"FFmpeg (Shared)\"");
			Logger.Info("Once installed, restart the application.");
		}
		return null;
	}

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
