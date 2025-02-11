using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Unai.ExtendedBinaryWaterfall.Parsers;

[Parser("wim", "Windows Image (WIM)", [ ".wim" ])]
public class WindowsImageParser : IParser
{
	public Stream InputStream { get => throw new System.NotImplementedException(); set => throw new System.NotImplementedException(); }
	public Stream AuxiliaryInputStream { get => throw new System.NotImplementedException(); set => throw new System.NotImplementedException(); }

	public IEnumerable<SubFile> GetSubFiles()
	{
		var ret = ParseWimDir();
		ret = [.. ret
			.Where(sf => sf.Length > 0)
			.GroupBy(sf => sf.StartOffset)
			.Select(sfg => sfg.FirstOrDefault())
			.OrderBy(sf => sf.StartOffset)];
		return
		[
			.. ret,
			new("WIM File Table", ret.OrderBy(sf => sf.EndOffset).FirstOrDefault().EndOffset, InputStream.Length) { IconString = "🔶" },
		];
	}

	private IEnumerable<SubFile> ParseWimDir()
	{
		using var sr = new StreamReader(AuxiliaryInputStream);
		string line;

		bool firstLine = true;
		string filePath = null;
		long fileSize = 0;
		long fileOffset = 0;
		int fileAttrFlags = 0;

		while ((line = sr.ReadLine()) != null)
		{
			if (line.StartsWith("--------"))
			{
				if (!firstLine)
				{
					yield return new(filePath, fileOffset, fileSize) { IsDirectory = (fileAttrFlags & 0x10) == 0x10 };
				}
				firstLine = false;
			}
			string[] kvp = line.Split(" = ", 2);
			if (line.StartsWith("Full Path"))
			{
				filePath = kvp[1][1..^1];
			}
			else if (line.StartsWith("Uncompressed size"))
			{
				fileSize = long.Parse(kvp[1].Split(' ')[0]);
			}
			else if (line.StartsWith("Offset in WIM"))
			{
				fileOffset = long.Parse(kvp[1].Split(' ')[0]);
			}
			else if (line.StartsWith("Attributes"))
			{
				fileAttrFlags = int.Parse(kvp[1][2..], System.Globalization.NumberStyles.HexNumber);
			}
		}
		
		yield return new(filePath, fileOffset, fileSize) { IsDirectory = (fileAttrFlags & 0x10) == 0x10 };
	}
}