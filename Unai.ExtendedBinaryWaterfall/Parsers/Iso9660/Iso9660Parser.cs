using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Unai.ExtendedBinaryWaterfall.Parsers.Iso9660;

class IsoPathTableEntry
{
	public string Name;
	public int Lba;
	public int ParentIndex;
}

class IsoDirectoryEntry
{
	public string Name;
	public int DataLba;
	public int DataLength;
	public byte Flags;

	public bool IsDirectory => (Flags & 0b10) != 0;
}

[Parser("iso9660", "ISO 9660 Disc File System", [ ".iso" ])]
public class Iso9660Parser : IParser
{
	public Stream InputStream { get; set; }
	public Stream AuxiliaryInputStream { get; set; }

	private static IsoDirectoryEntry ParseIsoDirectoryEntry(BinaryReader br)
	{
		var dentLoc = br.BaseStream.Position;
		// Console.Error.WriteLine($"{dentLoc:X8}");

		var dentLen = br.ReadByte();
		if (dentLen == 0) return null;
		var dentExtAttrRecLen = br.ReadByte();
		var dentLba = br.ReadInt32(); br.BaseStream.Position += 4;
		var dentDataLen = br.ReadInt32(); br.BaseStream.Position += 4;
		var dentDateTime = br.ReadBytes(7);
		var dentFlags = br.ReadByte();
		var dentInterUnitSize = br.ReadByte();
		var dentInterGapSize = br.ReadByte();
		var dentVolSeqNum = br.ReadInt16(); br.BaseStream.Position += 2;
		var dentNameLen = br.ReadByte();
		var dentName = Encoding.ASCII.GetString(br.ReadBytes(dentNameLen));
		// Console.Error.WriteLine($"{dentNameLen} '{dentName}'");
		if (dentNameLen % 2 == 0) br.BaseStream.Position++;

		return new() { Name = dentName, DataLba = dentLba, DataLength = dentDataLen, Flags = dentFlags };
	}

	private static IEnumerable<SubFile> TraverseIsoDirectoryEntry(BinaryReader br, IsoDirectoryEntry dir, string parentPath = "", int recursiveCount = 0)
	{
		Logger.Debug($"Traversing ISO directory /{parentPath}");
		if (recursiveCount > 16)
		{
			yield break;
		}
		br.BaseStream.Position = dir.DataLba * 2048;
		while (br.BaseStream.Position < dir.DataLba * 2048 + dir.DataLength)
		{
			var dent = ParseIsoDirectoryEntry(br);
			if (dent == null) continue; // we're probably in a zero-filled sector limit gap and we need to seek further.

			if (dent.Name != "\x00" && dent.Name != "\x01")
			{
				yield return new(parentPath + "/" + dent.Name, dent.DataLba * 2048, dent.DataLength) { IsDirectory = dent.IsDirectory };
				if (dent.IsDirectory)
				{
					var dentEndOfs = br.BaseStream.Position;
					foreach (var sf in TraverseIsoDirectoryEntry(br, dent, parentPath + "/" + dent.Name, recursiveCount + 1))
					{
						yield return sf;
					}
					br.BaseStream.Position = dentEndOfs;
				}
			}
		}
	}

	public IEnumerable<SubFile> GetSubFiles()
	{
		yield return new("System Area", 0, 0x8000) { IconString = "🔶" };

		using BinaryReader br = new(InputStream, Encoding.ASCII, true);

		int pathTableOfs = 0;
		int pathTableSize = 0;
		IsoDirectoryEntry rootDir = null;

		for (int i = 0; i < 16; i++)
		{
			Logger.Debug($"Reading volume descriptor {i}…");
			br.BaseStream.Position = 0x8000 + (2048 * i);
			var volDesOfs = br.BaseStream.Position;

			var volDesType = br.ReadByte();
			var volDesId = Encoding.ASCII.GetString(br.ReadBytes(5));
			var volDesVersion = br.ReadByte();

			switch (volDesType)
			{
				case 1:
					br.BaseStream.Position = volDesOfs + 8;
					var pvdSystemId = Encoding.ASCII.GetString(br.ReadBytes(32));
					var pvdVolId = Encoding.ASCII.GetString(br.ReadBytes(32));
					br.BaseStream.Position += 8;
					var pvdVolNumLogicalBlocks = br.ReadInt32(); br.BaseStream.Position += 4 + 32;
					var pvdVolSeqSize = br.ReadInt16(); br.BaseStream.Position += 2;
					var pvdVolSeqIdx = br.ReadInt16(); br.BaseStream.Position += 2;
					br.BaseStream.Position = volDesOfs + 128;
					var pvdLogicalBlockSize = br.ReadInt16(); br.BaseStream.Position += 2;
					var pvdPathTableSize = br.ReadInt32(); br.BaseStream.Position += 4;
					var pvdPathTableLeLba = br.ReadInt32();
					var pvdPathTableOptLeLba = br.ReadInt32();
					br.BaseStream.Position = volDesOfs + 156;
					rootDir = ParseIsoDirectoryEntry(br);

					pathTableOfs = pvdPathTableLeLba * 2048;
					pathTableSize = pvdPathTableSize;
					break;
			}

			string volDesDisplayString = volDesType switch
			{
				0 => "Boot Record",
				1 => "Primary Volume Descriptor",
				2 => "Secondary Volume Descriptor",
				_ => $"Volume Descriptor, Type {volDesType:X2}"
			};

			yield return new(volDesDisplayString, volDesOfs, 2048) { IconString = "🔶" };

			// volume type 0xff signals end of volume descriptor table.
			if (volDesType == 0xff) break;
		}

		yield return new("Path Table", pathTableOfs, pathTableSize) { IconString = "🔶" };

		var pathTableEntries = new List<IsoPathTableEntry>();

		br.BaseStream.Position = pathTableOfs;

		while (br.BaseStream.Position < pathTableOfs + pathTableSize)
		{
			var pteDirNameLen = br.ReadByte();
			var pteExtAttrRecLen = br.ReadByte();
			var pteLba = br.ReadInt32();
			var pteParentRecIdx = br.ReadInt16();
			var pteDirName = Encoding.ASCII.GetString(br.ReadBytes(pteDirNameLen));
			if (pteDirNameLen % 2 == 1) br.BaseStream.Position++;

			pathTableEntries.Add(new() { Name = pteDirName, Lba = pteLba, ParentIndex = pteParentRecIdx });
		}

		yield return new("/", rootDir.DataLba * 2048, rootDir.DataLength) { IsDirectory = true };

		br.BaseStream.Position = rootDir.DataLba * 2048;
		
		foreach (var dir in TraverseIsoDirectoryEntry(br, rootDir))
		{
			yield return dir;
		}
	}
}
