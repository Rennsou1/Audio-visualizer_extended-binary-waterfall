using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Unai.ExtendedBinaryWaterfall;

public static class Utils
{
	public static string GetFileTypeEmoji(SubFile subFile)
	{
		if (subFile.IconString != null) return subFile.IconString;
		if (subFile.IsDirectory) return "🗀";
		return GetFileTypeEmojiFromExtension(subFile.Extension);
	}

	public static string GetFileTypeEmojiFromExtension(string extension)
	{
		if (extension == null) return null;

		return extension.ToLower() switch
		{
			".png" => "🖼",
			".jpg" => "🖼",
			".bmp" => "🖼",
			".ico" => "🖼",
			".tif" => "🖼",
			".sys" => "⚙️",
			".dll" => "⚙️",
			".cpl" => "⚙️",
			".msc" => "⚙️",
			".ax" => "⚙️",
			".txt" => "🖹",
			".ini" => "🖹",
			".inf" => "🖹",
			".htm" => "🖹",
			".xml" => "🖹",
			".sql" => "🖹",
			".log" => "🖹",
			".wav" => "🎵",
			".mp3" => "🎵",
			".wma" => "🎵",
			".mid" => "🎵",
			".avi" => "🎞️",
			".wmv" => "🎞️",
			".mp4" => "🎞️",
			".mpg" => "🎞️",
			".ufont" => "🗛",
			".ttf" => "🗛",
			".ttc" => "🗛",
			".fon" => "🗛",
			".chm" => "🕮",
			".bat" => "🗔",
			".exe" => "🗔",
			".com" => "🗔",
			".scr" => "🗔",
			".cur" => "🖰",
			".ani" => "🖰",
			_ => "🗋",
		};
	}

	public static string ToByteSizeString(long value)
	{
		if (value < 1024)
		{
			return $"{value:N0} B";
		}

		float fvalue = value / 1024f;

		string[] suffixes = [ "KiB", "MiB", "GiB" ];

		for (int i = 0; i < suffixes.Length; i++)
		{
			if (fvalue < 1024)
			{
				return $"{fvalue:N1} {suffixes[i]}";
			}
			fvalue /= 1024f;
		}

		return $"Infinity";
	}

	public static string TruncateString(string input, int maxLength)
	{
		if (input == null) return null;
		return input.Length > maxLength ? input[..(maxLength - 1)] + "…" : input;
	}

	public static ulong ParseHex(string input)
	{
		return ulong.Parse(input[2..], System.Globalization.NumberStyles.HexNumber);
	}

	public static bool Intersects(ulong aStart, ulong aEnd, ulong bStart, ulong bEnd)
	{
		return Math.Max(aStart, bStart) <= Math.Min(aEnd, bEnd);
	}

	public static IEnumerable<T> SkipLastRepetition<T>(this IEnumerable<T> input, Func<T, T, bool> criteria) where T : class
	{
		T last = null;
		foreach (var item in input)
		{
			if (last == null) 
			{
				yield return item;
				last = item;
			}
			else if (criteria(item, last))
			{
				yield return item;
				last = item;
			}
		}
	}

	public static IEnumerable<T> MixLastOcurrences<T>(this IEnumerable<T> input, Func<T, T, bool> criteria, Action<T, T> mixFunc) where T : class
	{
		bool first = true;
		T output = null;
		foreach (var item in input)
		{
			if (first)
			{
				output = item;
				first = false;
			}
			else if (criteria(item, output))
			{
				mixFunc(output, item);
			}
			else
			{
				yield return output;
				output = item;
			}
		}
		yield return output;
	}

	internal static SubFile ParsePe(Stream target, SubFile sf)
	{
		if (sf.Extension == ".exe" || sf.Extension == ".dll" || sf.Extension == ".sys" || sf.Extension == ".scr")
		{
			Console.Error.WriteLine($"parsing pe: {sf.Path}");
			try
			{
				target.Position = sf.StartOffset;
				byte[] peFileBuf = new byte[sf.Length];
				target.ReadExactly(peFileBuf);
				var peFile = new PeNet.PeFile(peFileBuf);

				if (peFile.Resources != null)
				{
					foreach (var stringEntry in peFile.Resources.VsVersionInfo.StringFileInfo.StringTable)
					{
						sf.Description = $"{stringEntry.OriginalFilename}\n{stringEntry.ProductName}\n{stringEntry.FileDescription}\n{stringEntry.ProductVersion}";
					}
					if (peFile.Resources.GroupIconDirectories != null)
					{
						foreach (var giDir in peFile.Resources.GroupIconDirectories)
						{
							var bestIconGi = giDir.DirectoryEntries.OrderByDescending(gi => gi.WBitCount).FirstOrDefault();
							var bestIcon = bestIconGi.AssociatedIcons(peFile).FirstOrDefault();
							if (bestIcon != null)
							{
								sf.Icon = Image.Load(bestIcon.AsIco());
							}
						}
					}
				}

				if (sf.Icon != null)
				{
					var firstIcon = peFile.Icons().FirstOrDefault();
					if (firstIcon != null)
					{
						sf.Icon = Image.Load(firstIcon);
					}
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex.Message);
			}
		}
		else if (sf.Extension == ".png" || sf.Extension == ".jpg" || sf.Extension == ".tif" || sf.Extension == ".gif")
		{
			try
			{
				target.Position = sf.StartOffset;
				byte[] imageBuf = new byte[sf.Length];
				target.Read(imageBuf, 0, imageBuf.Length);
				sf.Icon = Image.Load(imageBuf);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex.Message);
			}
		}
					
		sf.Icon?.Mutate(ctx => ctx.Resize(128, 128));

		return sf;
	}

	internal static IEnumerable<float> NearestNeighborResample(this IList<float> input, int newSampleCount = 48000)
	{
		for (int i = 0; i < newSampleCount; i++)
		{
			double ratio = i / (float)newSampleCount;
			int srcIndex = (int)(ratio * input.Count);
			yield return input[srcIndex];
		}
	}

	public static void PrintHex(BinaryReader br, int count = 4)
	{
		var ofs = br.BaseStream.Position;

		var buf = br.ReadBytes(count);
		var bufHex = string.Join(' ', buf.Select(x => x.ToString("X2")));
		var bufAscii = string.Join("", buf.Select(x => char.IsBetween((char)x, ' ', '\x7f') ? (char)x : '.'));
		Console.Error.WriteLine($"[{br.BaseStream.Position:X12}] {bufHex} {bufAscii}");

		br.BaseStream.Position = ofs;
	}
}
