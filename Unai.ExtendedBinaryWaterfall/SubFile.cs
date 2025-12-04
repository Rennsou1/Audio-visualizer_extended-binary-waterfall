using SkiaSharp;

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
	public SKBitmap Icon { get; set; } = null;
	// 可选的音频元数据字段：用于在右上文件列表中显示 Album/Album Artist/Track/Artist/Genre 信息
	public string AlbumTitle { get; set; } = null;              // 专辑名
	public string AlbumArtistName { get; set; } = null;        // 专辑作者/专辑艺术家名
	public int? DiscNumber { get; set; } = null;               // 第几碟
	public int? TrackNumber { get; set; } = null;              // 第几首
	public string TrackTitle { get; set; } = null;             // 曲名
	public string ArtistName { get; set; } = null;             // 艺术家
	public string ComposerName { get; set; } = null;           // 作曲家（这个优先显示而不是ArtistName~）
	public string Genre { get; set; } = null;                  // 曲风
	public float[] WaveformPeaks { get; set; } = null;        // 预计算的波形峰值数组（用于底部进度条显示）
	public float AudioPeak { get; set; } = 1.0f;              // 歌曲的最大振幅峰值（用于波形和频谱归一化）

	// 音频时间信息（用于进度条同步，单位：秒）
	public double AudioStartTime { get; set; } = 0;           // 该子文件音频在总时间线中的开始时间
	public double AudioDuration { get; set; } = 0;            // 该子文件的音频时长
	
	// 实际文件字节偏移（用于瀑布可视化读取）
	public long ActualByteOffset { get; set; } = 0;           // 该子文件在实际二进制流中的起始偏移
	public long ActualByteLength { get; set; } = 0;           // 该子文件的实际字节大小
	
	// 音频格式信息（用于 A/V SETTINGS 显示源文件格式）
	public int AudioSampleRate { get; set; } = 0;             // 音频采样率 (Hz)
	public int AudioChannels { get; set; } = 0;               // 音频声道数
	public int AudioBitDepth { get; set; } = 0;               // 音频位深 (bits)
	public int AudioBitrate { get; set; } = 0;                // 音频比特率 (bps)
	
	// MIDI 相关属性
	public bool IsMidi { get; set; } = false;                 // 是否为 MIDI 文件
	public MidiMetadata MidiMetadata { get; set; } = null;    // MIDI 元数据
	public string SoundFontName { get; set; } = null;         // 使用的 SoundFont 名称

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
