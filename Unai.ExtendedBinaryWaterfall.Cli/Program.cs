using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unai.ExtendedBinaryWaterfall.Parsers;

namespace Unai.ExtendedBinaryWaterfall.Cli;

class Program
{
	static bool _helpMode = false;

	static readonly Generator _generator = new();

	static void Main(string[] args)
	{
		// Make decimals use "." instead of other characters.
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

		Logger.Info($"{BuildInfo.ApplicationName} {BuildInfo.SemVer ?? "unknown"}");

		if (args.Length < 1)
		{
			Logger.Error("At least one argument must be specified.");
			PrintHelp();
			return;
		}

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

		try
		{
			_generator.Initialize();
			_generator.Generate();
		}
		catch (Exception ex)
		{
			Logger.Fail($"Unhandled exception while generating binary waterfall: {ex}");
		}
	}

	private static bool ParseCommandLineArguments(IEnumerable<string> args = null)
	{
		Logger.Info("Parsing command line arguments…");

		foreach (var arg in args ?? Environment.GetCommandLineArgs()[1..])
		{
			Logger.Debug($"Parsing command line argument: `{arg}`");

			if (!arg.StartsWith('-'))
			{
				if (_generator.InputFilePath != null)
				{
					Logger.Error("Cannot specify more than two input files.");
					return false;
				}
				_generator.InputFilePath = arg;
				continue;
			}

			var argKvp = arg.Split('=');

			switch (argKvp[0])
			{
				case "--help":
				case "-h":
				case "-?":
					_helpMode = true;
					break;

				default:
					var targetParam = Utils.GetPropertiesWithAttribute<CliParameterAttribute>()
						.Where(p => argKvp[0].Length == 2 ? p.GetCustomAttribute<CliParameterAttribute>().ShortParameterName == argKvp[0][1] : p.GetCustomAttribute<CliParameterAttribute>().LongParameterName == argKvp[0][2..]).FirstOrDefault();

					if (targetParam == null)
					{
						Logger.Error($"Unknown argument: `{argKvp[0]}`.");
						return false;
					}

					// Can't do a `switch` statement here. :(
					object targetObject = null;
					if (targetParam.DeclaringType == typeof(Generator))
					{
						targetObject = _generator;
					}
					else
					{
						Logger.Error($"Cannot set property `{targetParam.Name}` because the instance of its declaring type is unknown.");
						return false;
					}

					if (targetParam.PropertyType == typeof(string))
					{
						targetParam.SetValue(targetObject, argKvp[1]);
					}
					else if (targetParam.PropertyType == typeof(int))
					{
						targetParam.SetValue(targetObject, int.Parse(argKvp[1]));
					}
					else if (targetParam.PropertyType.IsEnum)
					{
						var ok = Enum.TryParse(targetParam.PropertyType, argKvp[1], true, out var pval);
						if (!ok)
						{
							Logger.Error($"Cannot parse value '{argKvp[1]}' to enumeration '{targetParam.PropertyType.Name}'.");
							Logger.Info("Valid values:");
							foreach (var enumVal in Enum.GetValues(targetParam.PropertyType))
							{
								Logger.Info($"	{enumVal}");
							}
							return false;
						}
						targetParam.SetValue(targetObject, pval);
					}
					else if (targetParam.PropertyType == typeof(bool))
					{
						targetParam.SetValue(targetObject, bool.Parse(argKvp[1]));
					}
					else
					{
						Logger.Error($"Cannot convert string representation of value of property `{targetParam.Name}` because it is not implemented yet.");
					}
					break;
			}
		}

		return true;
	}

	private static void PrintHelp()
	{
		StringBuilder helpStrBld = new();
		helpStrBld.AppendLine("Usage:");
		helpStrBld.AppendLine($"	{Path.GetFileName(Environment.GetCommandLineArgs()[0])} <file_input> [options]");
		helpStrBld.AppendLine();
		helpStrBld.AppendLine("Options:");
		helpStrBld.AppendLine($"	-h, -?, --help\n		Print this help text and exit");

		foreach (var cliParam in Utils.GetPropertiesWithAttribute<CliParameterAttribute>())
		{
			var cliParamAttr = cliParam.GetCustomAttribute<CliParameterAttribute>();
			helpStrBld.Append('\t');
			if (cliParamAttr.ShortParameterName.HasValue)
			{
				helpStrBld.Append($"-{cliParamAttr.ShortParameterName}, ");
			}
			helpStrBld.Append($"--{cliParamAttr.LongParameterName}=<{cliParam.PropertyType.Name}> ".PadRight(cliParamAttr.ShortParameterName.HasValue ? 28 : 32));
			helpStrBld.AppendLine(cliParamAttr.Name);
			if (cliParamAttr.Description != null)
			{
				helpStrBld.AppendLine($"		{cliParamAttr.Description}");
			}
		}
		helpStrBld.AppendLine();

		helpStrBld.AppendLine("Available parsers/input formats:");
		foreach (var parserKvp in Utils.GetTypesWithAttribute<ParserAttribute>())
		{
			var parserAttr = parserKvp.Key;
			helpStrBld.AppendLine($"	{parserAttr.Id.PadRight(16)} {parserAttr.Name}");
		}
		helpStrBld.AppendLine();

		helpStrBld.AppendLine("Available exporters:");
		foreach (var exporterKvp in Utils.GetTypesWithAttribute<ExporterAttribute>())
		{
			var exporterAttr = exporterKvp.Key;
			helpStrBld.AppendLine($"	{exporterAttr.Id.PadRight(16)} {exporterAttr.Name} – {exporterAttr.Description}");
		}

		Console.Error.WriteLine(helpStrBld);
	}
}
