using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Unai.ExtendedBinaryWaterfall.Parsers;

namespace Unai.ExtendedBinaryWaterfall;

class Program
{
	static bool _helpMode = false;

	static Generator _generator = new();

	static void Main(string[] args)
	{
		// Make decimals use "." instead of other characters.
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

		string asmVer = "unknown";
		try
		{
			// FIXME: this is horrible, change it!
			asmVer = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
		}
		catch (Exception ex)
		{
			Logger.Error($"Cannot determine program version: {ex.Message}");
		}
		Logger.Info($"Extended Binary Waterfall {asmVer}");

		if (!ParseCommandLineArguments(args))
		{
			Logger.Fail("Invalid command line arguments. Exiting…");
			return;
		}

		if (_helpMode)
		{
			PrintHelp();
			return;
		}

		_generator.Generate();
	}

	private static bool ParseCommandLineArguments(IEnumerable<string> args = null)
	{
		Logger.Info("Parsing command line arguments…");

		foreach (var arg in args ?? Environment.GetCommandLineArgs()[1..])
		{
			if (!arg.StartsWith('-'))
			{
				if (_generator.InputFilePath != null)
				{
					Logger.Error("Cannot specify more than two input files.");
					return false;
				}
				_generator.InputFilePath = arg;
				// _inputStream = File.OpenRead(arg);
				_generator.Title ??= Path.GetFileName(arg);
				continue;
			}

			var argKvp = arg.Split('=');
			switch (argKvp[0])
			{
				case "--help":
					_helpMode = true;
					break;

				case "--title":
					_generator.Title = argKvp[1].Replace("\\n", "\n");
					break;

				case "--author":
					_generator.Author = argKvp[1];
					break;

				case "--format":
					_generator.InputFileFormatId = argKvp[1];
					break;

				case "--file-list":
				case "--aux-file":
					_generator.InputAuxiliaryFilePath = argKvp[1];
					break;

				case "--output-type":
				case "--exporter":
					_generator.ExporterId = argKvp[1];
					break;
			}
		}

		return true;
	}

	private static void PrintHelp()
	{
		StringBuilder helpStrBld = new();
		helpStrBld.AppendLine($"Usage: {Environment.GetCommandLineArgs()[0]} <file_input> [options]");
		helpStrBld.AppendLine("Options:");
		helpStrBld.AppendLine($"	--title=…       Set the target file's title");
		helpStrBld.AppendLine($"	--author=…      Set the author name of the generated binary waterfall");
		helpStrBld.AppendLine($"	--format=…      Set the target file's format (autodetected from extension if unset)");
		helpStrBld.AppendLine($"	--file-list=…   Set the file list text file path (some parsers require it)");
		helpStrBld.AppendLine($"	--output-type=… Set the output type/exporter (SDL window by default)");
		helpStrBld.AppendLine();

		helpStrBld.AppendLine("Available parsers/input formats:");
		foreach (var parser in Utils.GetTypesWithAttribute<ParserAttribute>())
		{
			var parserAttr = parser.GetCustomAttribute<ParserAttribute>();
			helpStrBld.AppendLine($"	{parserAttr.Id.PadRight(16)} {parserAttr.Name}");
		}
		helpStrBld.AppendLine();

		helpStrBld.AppendLine("Available exporters:");
		foreach (var exporter in Utils.GetTypesWithAttribute<ExporterAttribute>())
		{
			var exporterAttr = exporter.GetCustomAttribute<ExporterAttribute>();
			helpStrBld.AppendLine($"	{exporterAttr.Id.PadRight(16)} {exporterAttr.Name} – {exporterAttr.Description}");
		}

		Console.Error.WriteLine(helpStrBld);
	}
}
