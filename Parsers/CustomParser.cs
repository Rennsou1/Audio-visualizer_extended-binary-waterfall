using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Unai.ExtendedBinaryWaterfall.Parsers;

[Parser("custom", "Unknown Format, Custom File Listing", null)]
public class CustomParser : IParser
{
	public Stream InputStream { get; set; }
	public Stream AuxiliaryInputStream { get; set; }

	public IEnumerable<SubFile> GetSubFiles()
	{
		using var sr = new StreamReader(AuxiliaryInputStream);
		var csvValues = sr.ReadToEnd()
			.Split('\n')
			.Select(line => line.Split(','));

		foreach (var row in csvValues.Skip(1))
		{
			yield return new(row[3], long.Parse(row[0]), long.Parse(row[1]));
		}
	}
}
