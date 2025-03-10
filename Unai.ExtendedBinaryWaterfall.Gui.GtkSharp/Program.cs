using System;
using Gtk;

namespace Unai.ExtendedBinaryWaterfall.Gui.GtkSharp;

static class Program
{
	internal static Application _gtkApp = null;
	internal static Window _mainWin = null;
	
	[STAThread]
	static void Main(string[] args)
	{
		GLib.ExceptionManager.UnhandledException += ShowUnhandledExceptionMessageBox;
		GLib.Global.ApplicationName = BuildInfo.ApplicationName;

		Logger.Debug($"Initializing GTK…");

		Application.Init();

		_gtkApp = new Application("io.github.unai-d.extended-binary-waterfall", GLib.ApplicationFlags.None);

		_gtkApp.Startup += (_, _) =>
		{
			_mainWin = new MainWindow();
			_gtkApp.AddWindow(_mainWin);

			_mainWin.ShowAll();
		};

		_gtkApp.Activated += (_, _) =>
		{
			_gtkApp.Windows[0].Present();
		};

		((GLib.Application)_gtkApp).Run();
	}

	private static void ShowUnhandledExceptionMessageBox(GLib.UnhandledExceptionArgs args)
	{
		ShowUnhandledExceptionMessageBox((Exception)args.ExceptionObject);
	}

	internal static void ShowUnhandledExceptionMessageBox(Exception ex)
	{
		var errorMsgBox = new MessageDialog(null, 0, MessageType.Error, ButtonsType.Ok, false, $"Unhandled exception: {ex.Message}\n\n{ex.StackTrace}");
		
		errorMsgBox.Run();
		errorMsgBox.Destroy();
	}
}
