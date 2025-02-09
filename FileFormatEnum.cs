namespace Unai.ExtendedBinaryWaterfall;

public enum FileFormat
{
	Unknown = 0,
	PortableExecutable,
	ElfExecutable,
	WindowsMinidump,
	IsoDiscImage,
	WindowsImage, // WIM
}