using Gtk;
using static Gtk.Builder;

namespace Unai.ExtendedBinaryWaterfall.Gui.GtkSharp;

public class RenderDialog : Dialog
{
	[Object]
	internal Label _uiStatusLabel;
	[Object]
	internal ProgressBar _uiStatusProgBar;

	public RenderDialog() : this(new Builder("RenderDialog.glade"))
	{
		
	}

	private RenderDialog(Builder builder) : base(builder.GetRawOwnedObject("RenderDialog"))
	{
		builder.Autoconnect(this);
	}
}