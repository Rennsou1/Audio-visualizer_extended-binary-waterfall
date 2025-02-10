using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace Unai.ExtendedBinaryWaterfall;

class Program
{
	static Stream _inputStream = null;
	static Stream _inputFileListStream = null;
	static ExportHandler _exporter = new FfmpegExportHandler();

	static List<SubFile> _subfiles = [];

	static Image<Rgba32> _frameContent = null;
	static Image<Rgba32> _viewportFramebuf = null;
	static byte[] _audioBuffer = null;

	public static int OutputVideoWidth { get; set; } = 1920;
	public static int OutputVideoHeight { get; set; } = 1080;
	public static int OutputFps { get; set; } = 60;
	public static int WaterfallScaledWidth { get; set; } = 768;
	public static int WaterfallScaledHeight { get; set; } = 768;
	public static int WaterfallWidth { get; set; } = 256;
	public static int WaterfallHeight { get; set; } = 256;
	public static int WaterfallFrameLength => WaterfallWidth * WaterfallHeight * 4;

	static FontCollection _fontCollection;
	static FontFamily _fontFamily, _emojiFontFamily;
	static Font _font16, _font24, _font32, _font48;

	static string _title = null;
	static string _author = null;
	static string _inputFileListFormat = null;
	static string _outputType = null;

	static Stopwatch _timer = new();

	static void Main(string[] args)
	{
		// Make decimals use "." instead of other characters.
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

		if (!ParseCommandLineArguments(args))
		{
			Logger.Fail("Invalid command line arguments. Exiting…");
			return;
		}

		Logger.Info("Parsing subfiles…");
		Logger.Debug($"Target file format: '{_inputFileListFormat}'.");

		switch (_inputFileListFormat)
		{
			case "wim":
				_subfiles = FileListingParsers.ParseWimDir(_inputFileListStream).ToList();
				_subfiles = [.. _subfiles
					.Where(sf => sf.Length > 0)
					.GroupBy(sf => sf.StartOffset)
					.Select(sfg => sfg.FirstOrDefault())
					.OrderBy(sf => sf.StartOffset)];
				_subfiles =
				[
					.. _subfiles,
					new("WIM File Table", _subfiles.OrderBy(sf => sf.EndOffset).FirstOrDefault().EndOffset, _inputStream.Length) { IconString = "🔶" },
				];
				break;

			case "minidump_py":
				_subfiles = FileListingParsers.ParseMinidumpPythonOutput(_inputFileListStream).ToList();
				_subfiles = [.. _subfiles.MixLastOcurrences((sf, lsf) => sf.Path == lsf.Path, (lsf, sf) => { lsf.EndOffset = sf.EndOffset; })];
				_subfiles =
				[
					new("Header", 0, _subfiles.OrderBy(sf => sf.StartOffset).FirstOrDefault().StartOffset) { IconString = "🔶" },
					.. _subfiles,
				];
				break;

			case "elf":
				_subfiles = FileListingParsers.ParseElf(_inputStream).ToList();
				break;

			case "iso":
				_subfiles = FileListingParsers.ParseIso(_inputStream).ToList();
				break;

			// case "gamemaker":
			// 	GameMakerData gm = new(_inputStream);
			// 	gm.ParseDataWin();
			// 	_subfiles = gm.GetSubFiles().ToList();
			// 	break;
		}

		using var targetFileReader = new BinaryReader(_inputStream);

		_subfiles = [.. _subfiles
			.OrderBy(sf => sf.StartOffset)
			.Select(sf => Utils.ParseSubfile(_inputStream, sf))];

		Logger.Debug($"Total number of subfiles: {_subfiles.Count}");

		Logger.Info("Initializing export handler…");

		switch (_outputType)
		{
			case "sdl":
				_exporter = new SdlExportHandler();
				break;

			case "null":
				return;

			case null:
				break;

			default:
				throw new Exception("Unknown output type.");
		}

		int bytesPerFrame = WaterfallWidth * 4;
		// int bytesPerLine = WaterfallWidth * 4;
		int videoFrameX1 = OutputVideoWidth / 4 - WaterfallScaledWidth / 2;
		int videoFrameX2 = OutputVideoWidth / 4 + WaterfallScaledWidth / 2;
		int videoFrameY1 = OutputVideoHeight / 2 - WaterfallScaledHeight / 2;
		int videoFrameY2 = OutputVideoHeight / 2 + WaterfallScaledHeight / 2;
		int audioSampleRate = bytesPerFrame * OutputFps / 2;
		int audioOutputBytesPerFrame = (48000 * 2) / OutputFps;

		Logger.Debug($"Waterfall duration will be {TimeSpan.FromSeconds(_inputStream.Length / (bytesPerFrame * OutputFps))}.");

		_frameContent = new(OutputVideoWidth, OutputVideoHeight);
		_audioBuffer = Enumerable.Repeat((byte)128, audioOutputBytesPerFrame).ToArray();

		_fontCollection = new();
		_fontCollection.AddSystemFonts();
		_fontFamily = _fontCollection.Get("unifont");
		_font48 = _fontFamily.CreateFont(48f, FontStyle.Regular);
		_font32 = _fontFamily.CreateFont(32f, FontStyle.Regular);
		_font24 = _fontFamily.CreateFont(24f, FontStyle.Regular);
		_font16 = _fontFamily.CreateFont(16f, FontStyle.Regular);
		_emojiFontFamily = _fontCollection.Get("unifont upper");

		_timer.Start();

		// 1. Intro

		GenerateIntro();

		// 2. Main Video

		GenerateMainVideo(targetFileReader, bytesPerFrame, videoFrameX1, videoFrameY1, audioOutputBytesPerFrame);
	}

	private static void GenerateIntro()
	{
		Logger.Info("Generating introduction…");

		var totalFrames = (5 * OutputFps); // 60FPS = 300

		for (long frameNumber = 0; frameNumber < totalFrames; frameNumber++)
		{
			_frameContent.Mutate(ctx => ctx.Clear(new Rgba32(16, 16, 16, 255)));

			_frameContent.Mutate(av => av
				.DrawText(new RichTextOptions(_font48)
				{
					Origin = new Vector2(OutputVideoWidth / 2, OutputVideoHeight / 2),
					HorizontalAlignment = HorizontalAlignment.Center,
					TextAlignment = TextAlignment.Center,
				}, "DISCLAIMER\n\nThis video contains\nhigh speed flashing lights\nand loud noises", Color.White)
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(OutputVideoWidth / 2, OutputVideoHeight - 128),
					HorizontalAlignment = HorizontalAlignment.Center,
				}, $"Starting in {(totalFrames - frameNumber) / (float)OutputFps:N1} seconds…", Color.White)
				.DrawProgressBar(frameNumber / (float)totalFrames, (int)(OutputVideoWidth * 0.3), (int)(OutputVideoWidth * 0.7), OutputVideoHeight - 64));

			_exporter.PushNewFrame(_frameContent, _audioBuffer, _timer.Elapsed.TotalSeconds);
			_timer.Restart();
		}
	}

	private static void GenerateMainVideo(BinaryReader targetFileReader, int bytesPerFrame, int videoFrameX1, int videoFrameY1, int audioOutputBytesPerFrame)
	{
		Logger.Info("Generating binary waterfall…");

		string avSettingsString = $"{bytesPerFrame * OutputFps / 2} Hz, PCM unsigned 8-bit, stereo\nRGBA (32bpp), {WaterfallWidth} px/line";
		string readSpeedString = $"{(bytesPerFrame * OutputFps) / 1024} KiB/s";

		float subfileWindowIndex = 0f;
		long currentOffset = 0;
		int playHeadRelPos = 0;

		while (currentOffset < _inputStream.Length)
		{
			// Get video buffer.

			playHeadRelPos = 0;
			var frameStartByteOffset = currentOffset - (WaterfallFrameLength / 2);
			if (frameStartByteOffset < 0)
			{
				playHeadRelPos = (int)-(frameStartByteOffset / (WaterfallScaledWidth / 2));
				frameStartByteOffset = 0;
			}
			else if (frameStartByteOffset + WaterfallFrameLength >= _inputStream.Length)
			{
				frameStartByteOffset = _inputStream.Length - WaterfallFrameLength;
			}
			var frameEndByteOffset = frameStartByteOffset + WaterfallFrameLength;

			_inputStream.Position = frameStartByteOffset;
			var currentVideoBuffer = targetFileReader.ReadBytes(WaterfallFrameLength);

			// Get audio buffer.

			var audioFrameStartByteOffset = currentOffset - (bytesPerFrame / 2);
			if (audioFrameStartByteOffset < 0)
			{
				audioFrameStartByteOffset = 0;
			}
			else if (audioFrameStartByteOffset + bytesPerFrame >= _inputStream.Length)
			{
				audioFrameStartByteOffset = _inputStream.Length - WaterfallFrameLength;
			}
			var audioFrameEndByteOffset = audioFrameStartByteOffset + bytesPerFrame;

			_inputStream.Position = audioFrameStartByteOffset;
			var currentAudioBuffer = targetFileReader.ReadBytes(bytesPerFrame);

			// Get video data.

			_viewportFramebuf = Image.LoadPixelData<Rgba32>(currentVideoBuffer, WaterfallWidth, WaterfallHeight);
			_viewportFramebuf.ProcessPixelRows(pa =>
			{
				for (int y = 0; y < pa.Height; y++)
				{
					var row = pa.GetRowSpan(y);
					for (int x = 0; x < row.Length; x++)
					{
						row[x].A = 255;
					}
				}
			});
			_viewportFramebuf.Mutate(ctx => ctx.Flip(FlipMode.Vertical).Resize(WaterfallScaledWidth, WaterfallScaledHeight, new NearestNeighborResampler()));

			// Get audio data.

			_audioBuffer = currentAudioBuffer
				.Select(x => (float)x)
				.ToList()
				.NearestNeighborResample(audioOutputBytesPerFrame)
				.Select(x => (byte)x)
				.Select(x => x >= 128 ? (byte)(128 - x) : (byte)(128 + x)) // signed to unsigned
				.ToArray();

			// Compute registers.

			var subfilesInFrame = _subfiles
				.Select((sf, i) => new { key = i, value = sf })
				.Where(kvp => kvp.value.Intersects(currentOffset - (bytesPerFrame / 2), currentOffset + (bytesPerFrame / 2)))
				.ToList();
			var mainSubfile = subfilesInFrame.LastOrDefault();

			if (mainSubfile != null)
			{
				subfileWindowIndex = .2f * subfileWindowIndex + .8f * mainSubfile.key;
			}

			// 1. Clear frame

			_frameContent.Mutate(ctx => ctx.Clear(new Rgba32(16, 16, 16, 255)));

			// 2. Draw subfile listing

			int subfileX1 = OutputVideoWidth / 2;
			int subfileX2 = OutputVideoWidth - 32;

			int firstSubfileIndex = (int)(subfileWindowIndex - 7);
			int lastSubfileIndex = (int)Math.Ceiling(subfileWindowIndex + 7);

			float subfileH = 48;
			float subfileY = (OutputVideoHeight / 2) - (subfileWindowIndex - firstSubfileIndex) * subfileH;

			for (int sfi = firstSubfileIndex; sfi <= lastSubfileIndex; sfi++)
			{
				int i = sfi - (mainSubfile?.key ?? 0);

				if (sfi < 0 || sfi >= _subfiles.Count)
				{
					subfileY += subfileH;
					continue;
				}

				var subfile = _subfiles[sfi];

				bool isMainSubfile = sfi == (mainSubfile?.key ?? -1);

				_frameContent.Mutate(ictx => ictx
					.DrawText(new RichTextOptions(_font32)
					{
						Origin = new Vector2(subfileX1, subfileY),
						VerticalAlignment = VerticalAlignment.Center,
						FallbackFontFamilies = [_emojiFontFamily],
					}, $"{(isMainSubfile ? "▶" : " ")} {Utils.GetFileTypeEmoji(subfile)} {Utils.TruncateString(subfile.FileName, 40)}", Color.White)
					.DrawText(new RichTextOptions(_font32)
					{
						Origin = new Vector2(subfileX2, subfileY),
						HorizontalAlignment = HorizontalAlignment.Right,
						VerticalAlignment = VerticalAlignment.Center,
					}, Utils.ToByteSizeString(subfile.Length), Color.DimGray)
				);

				if (isMainSubfile)
				{
					float percentOfSubfile = (currentOffset - subfile.StartOffset) / (float)subfile.Length;

					_frameContent.Mutate(ictx => ictx
						.DrawText(new(_font16)
						{
							Origin = new PointF(subfileX1 + 48, subfileY + 20),
							HorizontalAlignment = HorizontalAlignment.Center,
							VerticalAlignment = VerticalAlignment.Center,
						}, $"{(int)Math.Clamp(percentOfSubfile * 100, 0, 100)} %", Color.White)
						.DrawProgressBar(percentOfSubfile, subfileX1 + 80, subfileX2, subfileY + 20)
					);
				}

				subfileY += subfileH;
			}

			// 3. Draw binary waterfall viewport

			_frameContent.Mutate(ctx => ctx
				.DrawImage(_viewportFramebuf, new Point(videoFrameX1, videoFrameY1), 1f)
				.DrawText(new RichTextOptions(_font32)
				{
					Origin = new Vector2(32, (OutputVideoHeight / 2) + playHeadRelPos),
					VerticalAlignment = VerticalAlignment.Center,
				}, "▶", Color.White)
			);

			// 4. Draw top-bottom gradients

			float shadowY1 = (OutputVideoHeight / 2) - subfileH * 8.5f;
			float shadowY2 = (OutputVideoHeight / 2) + subfileH * 6.5f;

			_frameContent.Mutate(ctx => ctx
				.Fill(
					new LinearGradientBrush(
						new PointF(0, shadowY1),
						new PointF(0, shadowY1 + subfileH * 2),
						GradientRepetitionMode.None,
						new(0.5f, Color.FromRgba(16, 16, 16, 255)),
						new(1, Color.FromRgba(16, 16, 16, 0))
					),
					new RectangleF(0, shadowY1, OutputVideoWidth, subfileH * 2))
				.Fill(
					new LinearGradientBrush(
						new PointF(0, shadowY2),
						new PointF(0, shadowY2 + subfileH * 2),
						GradientRepetitionMode.None,
						new(0, Color.FromRgba(16, 16, 16, 0)),
						new(0.5f, Color.FromRgba(16, 16, 16, 255))
					),
					new RectangleF(0, shadowY2, OutputVideoWidth, subfileH * 2))
			);

			_frameContent.Mutate(ctx => ctx
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(subfileX1 + 40, 160),
					VerticalAlignment = VerticalAlignment.Center,
				}, Utils.TruncateString(mainSubfile?.value?.FileDirectory ?? string.Empty, 72), Color.DimGray)
			);

			// 5. Draw Status and General Info

			_frameContent.Mutate(ctx =>
			{
				ctx
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(32, 32),
				}, "A/V SETTINGS", Color.DimGray)
				.DrawText(new(_font32)
				{
					Origin = new Vector2(32, 32 + 24),
				}, avSettingsString, Color.White)
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(OutputVideoWidth - 32, 32),
					HorizontalAlignment = HorizontalAlignment.Right,
				}, "ABS. OFFSET", Color.DimGray)
				.DrawText(new(_font32)
				{
					Origin = new Vector2(OutputVideoWidth - 32, 32 + 24),
					HorizontalAlignment = HorizontalAlignment.Right,
					TextAlignment = TextAlignment.End,
				}, $"{currentOffset / 1048576f:N2} MiB\n0x{currentOffset:X8}", Color.White)
				.DrawText(new RichTextOptions(_font24)
				{
					Origin = new Vector2(OutputVideoWidth - 256, 32),
					HorizontalAlignment = HorizontalAlignment.Right,
				}, "BITRATE", Color.DimGray)
				.DrawText(new(_font32)
				{
					Origin = new Vector2(OutputVideoWidth - 256, 32 + 24),
					HorizontalAlignment = HorizontalAlignment.Right,
				}, readSpeedString, Color.White);

				if (_author != null)
				{
					ctx.DrawText(new(_font32)
					{
						Origin = new Vector2(OutputVideoWidth / 2, 32 + 24),
						VerticalAlignment = VerticalAlignment.Center,
						HorizontalAlignment = HorizontalAlignment.Center,
					}, _author, Color.White);
				}

				if (_title != null)
				{
					ctx.DrawText(new RichTextOptions(_font24)
					{
						Origin = new Vector2(32, OutputVideoHeight - 64 - (_title.Contains('\n') ? 32 : 0)),
						VerticalAlignment = VerticalAlignment.Bottom,
					}, "TARGET", Color.DimGray)
					.DrawText(new(_font32)
					{
						Origin = new Vector2(32, OutputVideoHeight - 32),
						VerticalAlignment = VerticalAlignment.Bottom,
					}, _title, Color.White);
				}

				if (mainSubfile?.value?.Icon != null)
				{
					ctx.DrawImage(mainSubfile.value.Icon, new Point(OutputVideoWidth / 2, OutputVideoHeight - 128 - 32), 1f);
				}

				if (mainSubfile?.value?.Description != null)
				{
					ctx.DrawText(new(_font32)
					{
						Origin = new Vector2(OutputVideoWidth / 2 + 128 + 32, OutputVideoHeight - 32),
						VerticalAlignment = VerticalAlignment.Bottom,
					}, mainSubfile.value.Description, Color.White);
				}
			});

			_exporter.PushNewFrame(_frameContent, _audioBuffer, _timer.Elapsed.TotalSeconds);
			_timer.Restart();

			currentOffset += bytesPerFrame;
		}

		_exporter.Finish();
	}

	private static bool ParseCommandLineArguments(IEnumerable<string> args = null)
	{
		Logger.Info("Parsing command line arguments…");

		foreach (var arg in args ?? Environment.GetCommandLineArgs()[1..])
		{
			if (!arg.StartsWith('-'))
			{
				if (_inputStream != null)
				{
					Logger.Error("Cannot specify more than two input files.");
					return false;
				}
				_inputStream = File.OpenRead(arg);
				_title ??= arg;
				continue;
			}

			var argKvp = arg.Split('=');
			switch (argKvp[0])
			{
				case "--title":
					_title = argKvp[1].Replace("\\n", "\n");
					break;

				case "--author":
					_author = argKvp[1];
					break;

				case "--format":
					_inputFileListFormat = argKvp[1];
					break;

				case "--file-list":
					_inputFileListStream = File.OpenRead(argKvp[1]);
					break;

				case "--output-type":
					_outputType = argKvp[1];
					break;
			}
		}

		return true;
	}
}
