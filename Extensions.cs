using System;
using System.IO;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace Unai.ExtendedBinaryWaterfall;

public static class Extensions
{
	public static void DrawProgressBar(this IImageProcessingContext ictx, float percent, int x1, int x2, float y)
	{
		percent = Math.Clamp(percent, 0, 1);

		ictx.DrawLine(new(), new SolidBrush(Color.FromRgb(32, 32, 32)), 8,
				new PointF(x1, y),
				new PointF(x2, y)
			).DrawLine(new(), new SolidBrush(Color.Silver), 8,
				new PointF(x1, y),
				new PointF(x1 + (x2 - x1) * percent, y)
			);
	}

	public static string ReadString(this BinaryReader br, int length, Encoding textEncoding = null)
	{
		textEncoding ??= Encoding.ASCII;
		return textEncoding.GetString(br.ReadBytes(length));
	}
}
