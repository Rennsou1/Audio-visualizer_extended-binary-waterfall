using SixLabors.ImageSharp;

namespace Unai.ExtendedBinaryWaterfall;

public class SubFile(string path, long startOffset, long length)
{
	public string Path { get; set; } = path;
	public long StartOffset { get; set; } = startOffset;
	public long Length { get; set; } = length;
	public bool IsDirectory { get; set; } = false;
	public long EndOffset { get => StartOffset + Length; set => Length = value - StartOffset; }
	public string Description { get; set; } = null;
	public string IconString { get; set; } = null;
	public Image Icon { get; set; } = null;
	// 可选的音频元数据字段：用于在右上文件列表中显示 Album/Album Artist/Track/Artist/Genre 信息
	public string AlbumTitle { get; set; } = null;              // 专辑名
	public string AlbumArtistName { get; set; } = null;        // 专辑作者/专辑艺术家名
	public int? DiscNumber { get; set; } = null;               // 第几碟
	public int? TrackNumber { get; set; } = null;              // 第几首
	public string TrackTitle { get; set; } = null;             // 曲名
	public string ArtistName { get; set; } = null;             // 艺术家/作曲家
	public string Genre { get; set; } = null;                  // 曲目风格

	public string FileName => System.IO.Path.GetFileName(Path);
	public string FileDirectory => System.IO.Path.GetDirectoryName(Path);
	public string Extension => System.IO.Path.GetExtension(Path);

	public bool Intersects(long start, long end) => end > StartOffset && start <= EndOffset;

	// 根据整个输入文件构造一个“虚拟子文件”，用于在没有解析到真实子文件时显示歌曲列表
	public static SubFile FromWholeFile(string path, long fileLength)
	{
		// 虚拟子文件从 0 偏移开始，长度为整个文件大小
		return new SubFile(path, 0, fileLength);
	}
}
