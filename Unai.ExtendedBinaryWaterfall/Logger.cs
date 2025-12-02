using System;
using System.Diagnostics;
using System.IO;

namespace Unai.ExtendedBinaryWaterfall;

public static class Logger
{
	public enum LogLevel
	{
		Fail, Error, Warning, Info, Debug, Trace
	}

	public static bool UseColor { get; set; } = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOCOLOR"));
	
	// 日志文件夹路径：EXE 同级目录下的 logs 文件夹
	private static readonly string LogFolder = Path.Combine(
		Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, 
		"logs");
	// 日志文件路径（按日期命名）
	private static readonly string LogFilePath;
	private static readonly object _logLock = new();
	// 是否启用文件日志
	public static bool EnableFileLog { get; set; } = true;
	// 是否记录所有日志到文件（默认开启）
	public static bool LogAllToFile { get; set; } = true;
	
	// 静态构造函数：初始化日志文件夹和文件
	static Logger()
	{
		try
		{
			// 确保日志文件夹存在
			if (!Directory.Exists(LogFolder))
			{
				Directory.CreateDirectory(LogFolder);
			}
			// 按日期命名日志文件
			LogFilePath = Path.Combine(LogFolder, $"app_{DateTime.Now:yyyyMMdd_HHmmss}.log");
		}
		catch
		{
			// 如果创建失败，使用临时目录
			LogFilePath = Path.Combine(Path.GetTempPath(), $"AudioVisualizer_{DateTime.Now:yyyyMMdd_HHmmss}.log");
		}
	}

	private static void Print(string message, LogLevel logLevel, StackFrame sf)
	{
		var callingMethod = sf?.GetMethod();
		var source = callingMethod != null ? $"{callingMethod.DeclaringType?.Name} {callingMethod.Name}" : "?";
		var logLevelAnsiColor = logLevel switch
		{
			LogLevel.Fail => "\x1b[31m",
			LogLevel.Error => "\x1b[91m",
			LogLevel.Warning => "\x1b[93m",
			LogLevel.Debug => "\x1b[92m",
			LogLevel.Trace => "\x1b[32m",
			_ => "\x1b[0m",
		};
		var levelName = logLevel.ToString().ToUpper();
		var logLine = $"[{levelName}] {source} {message}";
		Console.Error.WriteLine(UseColor ? $"\x1b[90m{source} {logLevelAnsiColor}{message}\x1b[0m" : logLine);
		
		// 写入日志文件
		if (EnableFileLog && LogAllToFile)
		{
			lock (_logLock)
			{
				try
				{
					File.AppendAllText(LogFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {logLine}\n");
				}
				catch { }
			}
		}
	}

	public static void Fail(string message)
	{
		Print(message, LogLevel.Fail, new StackFrame(1));
	}

	public static void Error(string message)
	{
		Print(message, LogLevel.Error, new StackFrame(1));
	}

	public static void Warning(string message)
	{
		Print(message, LogLevel.Warning, new StackFrame(1));
	}

	public static void Info(string message)
	{
		Print(message, LogLevel.Info, new StackFrame(1));
	}

	[Conditional("DEBUG")]
	public static void Debug(string message)
	{
		Print(message, LogLevel.Debug, new StackFrame(1));
	}

	[Conditional("TRACE")]
	public static void Trace(string message)
	{
		Print(message, LogLevel.Trace, new StackFrame(1));
	}
}
