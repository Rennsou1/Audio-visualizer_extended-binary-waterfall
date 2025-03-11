using System;
using System.Linq;
using System.Reflection;
using Unai.ExtendedBinaryWaterfall.Parsers;
using Unai.ExtendedBinaryWaterfall.Parsers.Custom;
using static Gtk.Builder;
using System.Threading.Tasks;
using Gtk;
using Unai.ExtendedBinaryWaterfall.Exporters;

namespace Unai.ExtendedBinaryWaterfall.Gui.GtkSharp;

public class MainWindow : Window
{
	#pragma warning disable CS0649

	[Object] private FileChooserButton _uiInputFileChooser;
	[Object] private FileChooserButton _uiAuxInputFileChooser;
	[Object] private FileFilter _uiSaveFileDialogFilter;
	[Object] private ComboBox _uiParserComboBox;
	[Object] private ListStore _uiParserListStore;
	[Object] private ToolButton _uiPreviewBtn;

	private FileChooserDialog _uiSaveFileDialog = null;

	private readonly string _nullParserId = typeof(CustomParser).GetCustomAttribute<ParserAttribute>().Id;
	private string _inputFilePath = null;

	private static Generator _generator = null;
	private static Task _genTask = null;
	// private static RenderDialog _genStatusDialog = null;
	private static bool _genTaskFinished = false;
	private static float _genProgress = 0f;

	public MainWindow() : this(new Builder("MainWindow.glade"))
	{
		
	}

	private MainWindow(Builder builder) : base(builder.GetRawOwnedObject("MainWindow"))
	{
		builder.Autoconnect(this);

		foreach (var parser in Utils.GetTypesWithAttribute<ParserAttribute>())
		{
			_uiParserListStore.AppendValues(parser.Key.Id, parser.Key.Name);
		}

		var cellRen = new CellRendererText();
		_uiParserComboBox.PackStart(cellRen, true);
		_uiParserComboBox.AddAttribute(cellRen, "text", 1);

		_uiSaveFileDialogFilter.Name = "Matroska Video File (*.mkv)";
	}

	private void UpdateBasedOnInputFile(object sender, EventArgs e)
	{
		_inputFilePath = _uiInputFileChooser.Filename;

		// TODO: Use Utils common method for this.
		var inputFileExtension = System.IO.Path.GetExtension(_inputFilePath);
		bool formatDetected = false;
		foreach (var parser in Utils.GetTypesWithAttribute<ParserAttribute>())
		{
			if (parser.Key.FileExtensions.Contains(inputFileExtension))
			{
				_uiParserComboBox.SetActiveId(parser.Key.Id);
				formatDetected = true;
				break;
			}
		}

		if (!formatDetected)
		{
			var msgBox = new MessageDialog(
				this,
				DialogFlags.Modal,
				MessageType.Warning,
				ButtonsType.Ok,
				false,
				"Cannot detect file format.\n\nYou can manually specify a parser if you know the actual file format.\n\nAlternatively, you can use a custom subfile definition CSV file (specified at the auxiliary file chooser).");
			msgBox.Run();
			msgBox.Destroy();
			_uiParserComboBox.SetActiveId(_nullParserId);
		}

		UpdateBasedOnParser(sender, e);
	}

	private void UpdateBasedOnParser(object sender, EventArgs e)
	{
		_uiAuxInputFileChooser.Sensitive = _uiParserComboBox.ActiveId == _nullParserId;
	}

	private void OnWaterfallPreviewClick(object sender, EventArgs e)
	{
		PrepareGenerator();
		
		Application.Invoke((_, _) =>
		{
			_generator.Initialize();
			_generator.Generate();
			// This should not be necessary, but here we are…
			((SdlExporter)_generator.Exporter).Finish();
		});
	}

	private void OnWaterfallRenderClick(object sender, EventArgs e)
	{
		_uiSaveFileDialog = new FileChooserDialog("Output File", this, FileChooserAction.Save, "Save", ResponseType.Accept)
		{
			DoOverwriteConfirmation = true,
			CurrentName = $"{System.IO.Path.GetFileNameWithoutExtension(_inputFilePath)}.mkv"
		};
		_uiSaveFileDialog.AddFilter(_uiSaveFileDialogFilter);

		int result = _uiSaveFileDialog.Run();
		string outputFilePath = _uiSaveFileDialog.Filename;
		_uiSaveFileDialog.Destroy();
		
		// Console.Error.WriteLine(result);
		if (result != (int)ResponseType.Accept)
		{
			return;
		}

		PrepareGenerator("ffmpeg");
		_generator.AdditionalCliArguments["-o"] = outputFilePath;
		
		_genTaskFinished = false;
		_generator.OnFinish += () => Program._renderDialog.Hide();
		_generator.OnProgress += (p) => _genProgress = p;
		_genTask = Task.Run(() =>
		{
			_generator.Initialize();
			_generator.Generate();
			_genTaskFinished = true;
		});

		Program._renderDialog = new();
		Program._renderDialog.Show();

		GLib.Idle.Add(new(() => 
		{
			Program._renderDialog._uiStatusProgBar.Fraction = _genProgress;
			Program._renderDialog._uiStatusProgBar.Text = $"{_genProgress * 100:N2} %";
			return !_genTaskFinished;
		}));
	}

	private void OnAboutBoxClick(object sender, EventArgs e)
	{
		var aboutBox = new AboutDialog()
		{
			Title = $"About {BuildInfo.ApplicationName}",
			ProgramName = BuildInfo.ApplicationName,
			Website = "https://github.com/unai-d/extended-binary-waterfall",
			WebsiteLabel = "GitHub Repository",
			Authors = [ "Unai Domínguez" ],
			Version = BuildInfo.SemVer,
			Modal = true,
		};
		aboutBox.Show();
	}

	private void PrepareGenerator(string exporterId = "sdl")
	{
		_generator = new()
		{
			ExporterId = exporterId,
			InputFilePath = _inputFilePath,
			InputFileFormatId = _uiParserComboBox.ActiveId
		};
	}
}
