using SkiaSharp;

namespace Unai.ExtendedBinaryWaterfall.Exporters;

public interface IExporter
{
	public Generator Generator { get; set; }
	// 推送新帧到导出器（videoFrame 使用 SKBitmap）
	public abstract void PushNewFrame(SKBitmap videoFrame, AudioBuffer audioFrame, double delta = 0.04);
	public abstract void Finish();
}
