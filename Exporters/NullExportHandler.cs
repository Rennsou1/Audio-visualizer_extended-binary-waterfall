using SixLabors.ImageSharp;

namespace Unai.ExtendedBinaryWaterfall.Exporters;

public class NullExportHandler : ExportHandler
{
	public override void Finish()
	{
		
	}

	public override void PushNewFrame(Image videoFrame, byte[] audioFrame, double delta = 0.04)
	{
		
	}
}
