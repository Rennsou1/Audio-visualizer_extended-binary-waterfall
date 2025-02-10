using SixLabors.ImageSharp;

namespace Unai.ExtendedBinaryWaterfall.Exporters;

public abstract class ExportHandler
{
	public abstract void PushNewFrame(Image videoFrame, byte[] audioFrame, double delta = 0.04);
	public abstract void Finish();
}
