using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Unai.ExtendedBinaryWaterfall;

public static class FileListingParsers
{
	public static IEnumerable<SubFile> ParseCustomCsv(Stream data)
	{
		using var sr = new StreamReader(data);
		var csvValues = sr.ReadToEnd()
			.Split('\n')
			.Select(line => line.Split(','));

		foreach (var row in csvValues.Skip(1))
		{
			yield return new(row[3], long.Parse(row[0]), long.Parse(row[1]));
		}
	}

	public static IEnumerable<SubFile> ParseWimDir(Stream wimDirResult)
	{
		using var sr = new StreamReader(wimDirResult);
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

	class ModuleEntry(string fileName, ulong baseAddress, ulong size, ulong endAddress, uint timestamp)
	{
		public string FileName = fileName;
		public ulong BaseAddress = baseAddress;
		public ulong Size = size;
		public ulong EndAddress = endAddress;
		public uint Timestamp = timestamp;
	}

	class MemoryEntry(ulong vaStart, ulong rva, ulong size)
	{
		public ulong VAStart = vaStart;
		public ulong RVA = rva;
		public ulong Size = size;
	}

	public static IEnumerable<SubFile> ParseMinidumpPythonOutput(Stream fileStream)
	{
		List<ModuleEntry> modules = [];
		List<MemoryEntry> memoryRanges = [];
		char parseMode = ' ';
		bool doParse = false;

		using var sr = new StreamReader(fileStream, Encoding.ASCII, leaveOpen: true);
		foreach (var line in sr.ReadToEnd().Split('\n'))
		{
			if (line.StartsWith("== "))
			{
				doParse = false;
				if (line == "== ModuleList ==")
				{
					parseMode = 'o';
				}
				else if (line == "== UnloadedModuleList ==")
				{
					parseMode = 'u';
				}
				else if (line == "== MinidumpMemory64List ==")
				{
					parseMode = '6';
				}
				else
				{
					parseMode = ' ';
				}
				continue;
			}

			if (!doParse && line.StartsWith("----"))
			{
				doParse = true;
				continue;
			}

			if (doParse && line.Contains(" | "))
			{
				var fields = line.Split(" | ").Select(s => s.Trim()).ToArray();
				switch (parseMode)
				{
					case 'o':
						modules.Add(new(
							fields[0],
							Utils.ParseHex(fields[1]),
							Utils.ParseHex(fields[2]),
							Utils.ParseHex(fields[3]),
							(uint)Utils.ParseHex(fields[4])
							));
						break;

					case 'u':
						modules.Add(new(
							fields[0],
							Utils.ParseHex(fields[1]),
							Utils.ParseHex(fields[2]),
							Utils.ParseHex(fields[3]),
							0
							));
						break;

					case '6':
						memoryRanges.Add(new(
							Utils.ParseHex(fields[0]),
							Utils.ParseHex(fields[1]),
							Utils.ParseHex(fields[2])
						));
						break;
				}
			}
		}

		foreach (var memoryRange in memoryRanges)
		{
			var ret = new SubFile("Unknown Memory", (long)memoryRange.RVA, (long)memoryRange.Size)
			{
				IconString = "❓",
				Description = $"Base Address: 0x{memoryRange.VAStart:X16}"
			};
			
			var module = modules.FirstOrDefault(m => Utils.Intersects(m.BaseAddress, m.EndAddress, memoryRange.VAStart, memoryRange.VAStart + memoryRange.Size));
			if (module != null)
			{
				ret.Path = module.FileName.Replace('\\', '/');
				ret.IconString = null;
			}
			yield return ret;
		}
	}

	public static IEnumerable<SubFile> ParseElf(Stream fileStream)
	{
		throw new NotImplementedException();
	}

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
		Debug.WriteLine($"traversing iso dir /{parentPath}");
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

	public static IEnumerable<SubFile> ParseIso(Stream fileStream)
	{
		yield return new("System Area", 0, 0x8000) { IconString = "🔶" };

		using BinaryReader br = new(fileStream, Encoding.ASCII, true);

		int pathTableOfs = 0;
		int pathTableSize = 0;
		IsoDirectoryEntry rootDir = null;

		for (int i = 0; i < 16; i++)
		{
			br.BaseStream.Position = 0x8000 + (2048 * i);
			var volDesOfs = br.BaseStream.Position;

			var volDesType = br.ReadByte();
			var volDesId = Encoding.ASCII.GetString(br.ReadBytes(5));
			var volDesVersion = br.ReadByte();

			if (volDesType == 0xff) break;

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

			// yield return new(pteDirName, pteLba * 2048, 2048) { IsDirectory = true };
			pathTableEntries.Add(new() { Name = pteDirName, Lba = pteLba, ParentIndex = pteParentRecIdx });
		}

		yield return new("/", rootDir.DataLba * 2048, rootDir.DataLength) { IsDirectory = true };

		br.BaseStream.Position = rootDir.DataLba * 2048;
		
		foreach (var dir in TraverseIsoDirectoryEntry(br, rootDir))
		{
			yield return dir;
		}

		// foreach (var pte in pathTableEntries)
		// {
		// 	br.BaseStream.Position = pte.Lba * 2048;
		// 	for (int i = 0; i < 8; i++)
		// 	{
		// 		var drLen = br.ReadByte();
		// 		var 
		// 	}
		// }

		// CDReader cdr = new(fileStream, true);

		// if (cdr.HasBootImage) yield return new("Boot Code", cdr.BootImageStart, 2048);

		// var target = cdr.Root.FileSystem;

		// int c = 100;

		// foreach (var f in target.GetType().GetFields())
		// {
		// 	yield return new($"f {f.FieldType.Name} {f}", c * 2048, 2048);
		// 	c++;
		// }
		// foreach (var p in target.GetType().GetProperties())
		// {
		// 	yield return new($"p {p.PropertyType.Name} {p}", c * 2048, 2048);
		// 	c++;
		// }

		// return null;
	}
}
