using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Unai.ExtendedBinaryWaterfall.Parsers.Wad;

[Parser("wad", "Doom Engine's Asset Archive (WAD)", [ ".wad" ])]
public class WadParser : IParser
{
	public Stream InputStream { get; set; }
	public Stream AuxiliaryInputStream { get; set; }

	public IEnumerable<SubFile> GetSubFiles()
	{
		using var br = new BinaryReader(InputStream, Encoding.ASCII, true);

		var wadMagic = br.ReadBytes(4); // IWAD / PWAD
		var wadLumpCount = br.ReadUInt32();
		var wadDirectoryOff = br.ReadUInt32();

		yield return new("WAD Header", 0, 8) { IconString = "🔶" };
		yield return new("WAD Directory", wadDirectoryOff, br.BaseStream.Length - wadDirectoryOff) { IconString = "🔶" };

		br.BaseStream.Position = wadDirectoryOff;

		for (int i = 0; i < wadLumpCount; i++)
		{
			var wadLumpOff = br.ReadUInt32();
			var wadLumpSize = br.ReadUInt32();
			var wadName = br.ReadString(8).TrimEnd('\0');

			if (wadLumpOff != 0)
			{
				yield return new(wadName, wadLumpOff, wadLumpSize);
			}
		}
	}
}