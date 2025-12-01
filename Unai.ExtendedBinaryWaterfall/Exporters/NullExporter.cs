using SkiaSharp;

namespace Unai.ExtendedBinaryWaterfall.Exporters;

[Exporter("null", "Null/Dummy Output", "Do nothing with the generated video. Useful for debugging purposes.")]
public class NullExporter : IExporter
{
	public Generator Generator { get; set; }

	public void Finish()
	{
		
	}

	// 空实现，不做任何处理
	public void PushNewFrame(SKBitmap videoFrame, AudioBuffer audioFrame, double delta = 0.04)
	{
		
	}
}
