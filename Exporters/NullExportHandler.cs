using SixLabors.ImageSharp;

namespace Unai.ExtendedBinaryWaterfall.Exporters;

[Exporter("null", "Null/Dummy Output", "Do nothing with the generated video. Useful for debugging purposes.")]
public class NullExportHandler : ExportHandler
{
	public override void Finish()
	{
		
	}

	public override void PushNewFrame(Image videoFrame, byte[] audioFrame, double delta = 0.04)
	{
		
	}
}
