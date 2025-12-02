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
	
	// 日志文件路径 EXE 同级目录
	private static readonly string LogFilePath = Path.Combine(
		Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, 
		"performance.log");
	private static readonly object _logLock = new();
	// 是否启用文件日志
	public static bool EnableFileLog { get; set; } = true;

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
		var logLine = $"{source} {message}";
		Console.Error.WriteLine(UseColor ? $"\x1b[90m{source} {logLevelAnsiColor}{message}\x1b[0m" : logLine);
		
		// 性能相关日志
		if (EnableFileLog && (message.Contains("[性能]") || message.Contains("[渲染性能]")))
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
