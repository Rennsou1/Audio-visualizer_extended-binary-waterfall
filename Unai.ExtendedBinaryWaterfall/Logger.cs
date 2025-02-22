using System;
using System.Diagnostics;

namespace Unai.ExtendedBinaryWaterfall;

public static class Logger
{
	public enum LogLevel
	{
		Fail, Error, Warning, Info, Debug, Trace
	}

	public static bool UseColor { get; set; } = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOCOLOR"));

	private static void Print(string message, LogLevel logLevel, StackFrame sf)
	{
		var source = sf?.GetMethod()?.DeclaringType?.Name ?? "?";
		var logLevelAnsiColor = logLevel switch
		{
			LogLevel.Fail => "\x1b[31m",
			LogLevel.Error => "\x1b[91m",
			LogLevel.Warning => "\x1b[93m",
			LogLevel.Debug => "\x1b[92m",
			LogLevel.Trace => "\x1b[32m",
			_ => "\x1b[0m",
		};
		Console.Error.WriteLine(UseColor ? $"\x1b[90m{source} {logLevelAnsiColor}{message}\x1b[0m" : $"{source} {message}");
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
