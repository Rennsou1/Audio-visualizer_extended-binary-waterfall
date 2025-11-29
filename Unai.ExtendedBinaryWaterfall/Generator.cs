using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CSCore;
using CSCore.Codecs;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Unai.ExtendedBinaryWaterfall.Exporters;
using Unai.ExtendedBinaryWaterfall.Parsers;

namespace Unai.ExtendedBinaryWaterfall;

public class Generator
{
    #region Main Fields

    public FileStream InputFileStream { get; set; }
    public Stream InputAuxiliaryFileStream { get; set; }
    public IParser Parser { get; set; }
    public IExporter Exporter { get; set; }
    private readonly Stopwatch _timer = new();
    private List<SubFile> _subfiles = [];

    public Dictionary<string, string> AdditionalCliArguments { get; } = [];

    #endregion

    #region Events

    public event Action OnFinish;
    public event Action<float> OnProgress;

    #endregion

    #region Generator Registers

    private Image<Rgba32> _frameContent = null;
    private Image<Rgba32> _viewportFramebuf = null;
    private AudioBuffer _inputAudioBuffer = null;
    private AudioBuffer _outputAudioBuffer = null;
    // CSCore 音频解码相关字段：使用 CodecFactory 自动根据扩展名选择解码器
    private IWaveSource _audioWaveSource = null;
    private ISampleSource _audioSampleSource = null;
    // 用于暂存每帧交织的 float PCM 数据
    private float[] _audioSampleBuffer = null;
    private int _videoFrameX1, _videoFrameX2, _videoFrameY1, _videoFrameY2;
    internal bool _exitRequested = false;

    #endregion

    #region ImageSharp-specific

    private FontCollection _fontCollection;
    private FontFamily _fontFamily, _emojiFontFamily;
    private Font _font16, _font24, _font32, _font48;
    private readonly DrawingOptions _drawOpts = new()
    {
        GraphicsOptions = new()
        {
            Antialias = true,
            AntialiasSubpixelDepth = 0, // coalesced value
        }
    };

    #endregion

    #region General Parameters

    public string InputFilePath { get; set; } = null;
    [CliParameter("Input File Listing File Path", "file-listing", "Set the file path that contains a text-based file listing if the input file format cannot be parsed entirely by this program")]
    public string InputAuxiliaryFilePath { get; set; } = null;
    [CliParameter("Output File Path", "output", 'o', "Set the output video file path")]
    public string OutputFilePath { get; set; } = null;
    [CliParameter("Title", "title", 't', "Set the title that will be shown during the binary waterfall describing the target file")]
    public string Title { get; set; } = null;
    [CliParameter("Author", "author", 'a', "Set the author of the generated binary waterfall")]
    public string Author { get; set; } = null;
    [CliParameter("Input File Parser", "parser", 'p', "Force a specific parser for the input file")]
    public string InputFileFormatId { get; set; } = null;
    [CliParameter("Exporter", "exporter", 'e', "Set the exporter to be used to export the generated binary waterfall")]
    public string ExporterId { get; set; } = null;
    [CliParameter("Input Bytes per Second", "input-bps", "Set the amount of bytes that will be read per audio/video second")]
    public int InputBytesPerSecond { get; set; } = 48000 * 2;
    [CliParameter("Font Name", "font", "Set the font name to render the on-screen text")]
    public string FontName { get; set; } = null;
    [CliParameter("Font Antialiasing", "font-antialiasing")]
    public bool FontAntialiasing { get => _drawOpts.GraphicsOptions.Antialias; set => _drawOpts.GraphicsOptions.Antialias = value; }

    #endregion

    public int InputBytesPerFrame => InputBytesPerSecond / OutputFps;

    #region Video Parameters

    [CliParameter("Output Video Width", "output-width")]
    public int OutputVideoWidth { get; set; } = 1920;
    [CliParameter("Output Video Height", "output-height")]
    public int OutputVideoHeight { get; set; } = 1080;
    [CliParameter("Output Framerate", "output-fps")]
    public int OutputFps { get; set; } = 60;
    public int WaterfallScaledWidth { get; set; } = 768;
    public int WaterfallScaledHeight { get; set; } = 768;
    [CliParameter("Input Video Width", "input-width")]
    public int WaterfallWidth { get; set; } = 256;
    [CliParameter("Input Video Height", "input-height")]
    public int WaterfallHeight { get; set; } = 256;
    public int WaterfallFrameLength => WaterfallWidth * WaterfallHeight * 4;

    #endregion

    #region Audio Parameters

    [CliParameter("Input Sample Format", "sample-format")]
    public AudioSampleFormat AudioInputSampleFormat { get; set; } = AudioSampleFormat.Unsigned8;
    [CliParameter("Input Audio Channel Count", "channel-count")]
    public int AudioInputChannelCount { get; set; } = 2;
    public int AudioInputSamplesPerFrame => InputBytesPerFrame / AudioInputSampleFormat.GetByteSize();
    public int AudioInputSamplesPerFramePerChannel => AudioInputSamplesPerFrame / AudioInputChannelCount;
    public int AudioInputSampleRate => (InputBytesPerSecond / AudioInputSampleFormat.GetByteSize()) / AudioInputChannelCount;
    public int AudioInputBytesPerFrame => InputBytesPerFrame;

    [CliParameter("Output Sample Format", "output-sample-format")]
    public AudioSampleFormat AudioOutputSampleFormat { get; set; } = AudioSampleFormat.Float32;
    [CliParameter("Output Audio Channel Count", "output-channel-count")]
    public int AudioOutputChannelCount { get; set; } = 2;
    [CliParameter("Output Sample Rate", "output-sample-rate")]
    public int AudioOutputSampleRate { get; set; } = 48000;
    public int AudioOutputSamplesPerFramePerChannel => AudioOutputSampleRate / OutputFps;
    public int AudioOutputSamplesPerFrame => AudioOutputSamplesPerFramePerChannel * AudioOutputChannelCount;
    public int AudioOutputBytesPerFrame => AudioOutputSampleFormat.GetByteSize() * AudioOutputSamplesPerFrame;

    #endregion

    #region Debug Flags

    public bool LogAllSubfiles { get; set; } = false;

    #endregion

    #region Initialization Methods

    public void Initialize()
    {
        if (InputFileStream == null)
        {
            Logger.Info("Opening files…");
            Logger.Debug($"Opening file '{InputFilePath}'…");
            InputFileStream = File.OpenRead(InputFilePath);
        }

        if (InputAuxiliaryFileStream == null)
        {
            if (InputAuxiliaryFilePath != null)
            {
                Logger.Debug($"Opening file '{InputAuxiliaryFilePath}'…");
                InputAuxiliaryFileStream = File.OpenRead(InputAuxiliaryFilePath);
                Logger.Debug($"  Done ({InputAuxiliaryFileStream.Length / 1024} KiB).");
            }
        }

        InitializeParser();

        ParseSubfiles();

        InitializeExporter();

        InitializeFonts();

        Logger.Info("Preparing audio/video generation…");

        UpdateValues();

        // 使用 CSCore 初始化真实音频解码器，并让输出采样率/声道数跟随解码器参数
        InitializeAudioDecoder();

        if (_drawOpts.GraphicsOptions.Antialias)
        {
            if (_drawOpts.GraphicsOptions.AntialiasSubpixelDepth < 0)
            {
                _drawOpts.GraphicsOptions.AntialiasSubpixelDepth = 1;
            }
        }

        LogGeneratorStatus();
    }

    /// <summary>
    /// 使用 CSCore 初始化音频解码器：根据输入文件自动选择合适的解码器，
    /// 并让 AudioOutputSampleRate / AudioOutputChannelCount 直接跟随解码器参数。
    /// 如果初始化失败，则回退到原先基于字节流的伪音频配置。
    /// </summary>
    private void InitializeAudioDecoder()
    {
        try
        {
            Logger.Info("Initializing audio decoder (CSCore)…");

            // 使用 CodecFactory 根据文件扩展名自动选择解码器
            _audioWaveSource = CodecFactory.Instance.GetCodec(InputFilePath);
            var wf = _audioWaveSource.WaveFormat;

            // 直接跟随解码器的采样率和声道数
            AudioOutputSampleRate = wf.SampleRate;
            AudioOutputChannelCount = wf.Channels;

            // 转为浮点样本源，便于后续直接读取 float PCM
            _audioSampleSource = _audioWaveSource.ToSampleSource();

            // 准备输出音频缓冲区（每帧 SamplesPerFramePerChannel × 通道数）
            _outputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
            _audioSampleBuffer = new float[AudioOutputSamplesPerFrame];

            // 输入缓冲区在新的流程中不再使用真实含义，这里仅保持大小一致以避免空引用
            _inputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
        }
        catch (Exception ex)
        {
            // 出错时记录日志并回退到旧的“伪音频”逻辑，以保证程序仍可运行
            Logger.Error($"Failed to initialize audio decoder with CSCore. Falling back to fake audio. {ex.Message}");
            _audioSampleSource = null;
            _audioWaveSource?.Dispose();
            _audioWaveSource = null;

            _inputAudioBuffer = new(AudioInputSamplesPerFramePerChannel, AudioInputChannelCount);
            _outputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
        }
    }

    [Conditional("DEBUG")]
    private void LogGeneratorStatus()
    {
        Logger.Debug($"Selected parser: {Parser?.GetType().GetCustomAttribute<ParserAttribute>()?.Name ?? "<null>"}");
        Logger.Debug($"Selected exporter: {Exporter?.GetType().GetCustomAttribute<ExporterAttribute>()?.Name ?? "<null>"}");
        Logger.Debug($"Selected font: {_fontFamily.Name ?? "<null>"}");
        Logger.Debug($"Read speed: {InputBytesPerFrame} bytes/frame ({InputBytesPerSecond} bytes/second)");
        Logger.Debug($"Waterfall duration will be around {TimeSpan.FromSeconds(InputFileStream.Length / InputBytesPerSecond)}.");
        Logger.Debug($"Video input:  {WaterfallWidth}×{WaterfallHeight}");
        Logger.Debug($"Audio input:  {AudioInputBytesPerFrame}bpf {AudioInputSamplesPerFrame}spf → {AudioInputSampleRate}Hz {AudioInputChannelCount}ch {8 * AudioInputSampleFormat.GetByteSize()}-bit");
        Logger.Debug($"Audio output: {AudioOutputBytesPerFrame}bpf {AudioOutputSamplesPerFrame}spf → {AudioOutputSampleRate}Hz {AudioOutputChannelCount}ch {8 * AudioOutputSampleFormat.GetByteSize()}-bit");
    }

    private void InitializeFonts()
    {
        Logger.Info("Loading fonts…");
        Logger.Debug($"Requested font: '{FontName}'.");

        if (_fontCollection == null)
        {
            _fontCollection = new();
            _fontCollection.AddSystemFonts();
        }

        if (FontName != null)
        {
            // Try getting the font by the font name specified by the user
            if (!_fontCollection.TryGet(FontName, out _fontFamily))
            {
                Logger.Error($"Cannot find font '{FontName}'.");
            }
        }

        if (_fontFamily.Name == null)
        {
            // 默认字体 unifont
            if (_fontCollection.TryGet("unifont", out _fontFamily))
            {
                // unifont upper
                _fontCollection.TryGet("unifont upper", out _emojiFontFamily);
            }

            else if (Environment.OSVersion.Platform == PlatformID.Win32NT && _fontCollection.TryGet("Segoe UI Symbol", out _fontFamily))
            {
            // 没有以上字体后使用 Segoe UI Symbol
            }
            else
            {
                // 最后回退 Consolas / Source Code Pro
                _fontFamily = _fontCollection.Get(Environment.OSVersion.Platform == PlatformID.Win32NT ? "Consolas" : "Source Code Pro");
            }
        }

        _font48 = _fontFamily.CreateFont(48f, FontStyle.Regular);
        _font32 = _fontFamily.CreateFont(32f, FontStyle.Regular);
        _font24 = _fontFamily.CreateFont(24f, FontStyle.Regular);
        _font16 = _fontFamily.CreateFont(16f, FontStyle.Regular);
    }

    private void InitializeExporter()
    {
        Logger.Info("Setting up exporter…");
        Logger.Debug($"Requested exporter: '{ExporterId}'.");

        if (ExporterId != null)
        {
            var availableExporters = Utils.GetTypesWithAttribute<ExporterAttribute>();
            foreach (var exporterKvp in availableExporters)
            {
                var exporterAttr = exporterKvp.Key;
                if (exporterAttr.Id != ExporterId)
                {
                    continue;
                }
                Exporter = (IExporter)Activator.CreateInstance(exporterKvp.Value);
            }
            if (Exporter == null)
            {
                Logger.Fail($"Unknown exporter ID: '{ExporterId}'.");
                return;
            }
        }
        else
        {
            Logger.Debug("No exporter requested. Using SDL…");
            Exporter = new SdlExporter();
        }

        Exporter.Generator = this;

        if (AdditionalCliArguments.Count > 0)
        {
            Logger.Info($"Setting exporter properties from command line arguments…");
            foreach (var argKvp in AdditionalCliArguments)
            {
                var targetProp = Utils.GetPropertyFromCliArgument(argKvp.Key);

                if (targetProp.DeclaringType.GetInterfaces().Contains(typeof(IExporter)))
                {
                    CliParameterAttribute.SetPropertyFromCliArgument(targetProp, Exporter, argKvp.Value);
                }
                else
                {
                    Logger.Error($"Unrecognized CLI argument name: '{argKvp.Key}'.");
                }
            }
        }
    }

    // 解析子文件列表，如果解析不到则构造一个“覆盖整文件”的虚拟子文件
    private void ParseSubfiles()
    {
        IEnumerable<SubFile> subFiles = null;

        if (Parser != null)
        {
            Logger.Info("Parsing subfiles…");

            Parser.InputStream = InputFileStream;
            Parser.AuxiliaryInputStream = InputAuxiliaryFileStream;

            subFiles = Parser.GetSubFiles();

            _subfiles =
            [
                .. subFiles
                .OrderBy(sf => sf.StartOffset)
                .Select(sf => Utils.ParseSubfile(InputFileStream, sf))
            ];
        }

        Logger.Debug($"Total number of subfiles: {_subfiles.Count}");

        // 如果没有解析到任何子文件，则构造一个覆盖整个输入文件的虚拟子文件，
        // 这样右上区域始终可以显示至少一条“歌曲/文件”记录
        if (_subfiles.Count == 0 && InputFileStream != null && !string.IsNullOrEmpty(InputFilePath))
        {
            _subfiles =
            [
                SubFile.FromWholeFile(InputFilePath, InputFileStream.Length)
            ];
            Logger.Debug("No subfiles parsed; created a virtual subfile covering the whole input file.");
        }

        if (LogAllSubfiles)
        {
            foreach (var sf in _subfiles)
            {
                Logger.Debug($"\t{sf.IconString ?? "–"} '{sf.Path}' {sf.StartOffset:X8}–{sf.EndOffset:X8}");
            }
        }
    }

    // 初始化解析器：优先用用户指定的 parser ID，否则根据扩展名自动猜测
    private void InitializeParser()
    {
        Logger.Info("Setting up parser…");
        var availableParsers = Utils.GetTypesWithAttribute<ParserAttribute>();

        if (InputFileFormatId != null)
        {
            Logger.Debug($"Requested parser: '{InputFileFormatId}'.");
            foreach (var parserKvp in availableParsers)
            {
                var parserAttr = parserKvp.Key;
                if (parserAttr.Id != InputFileFormatId)
                {
                    continue;
                }
                Parser = (IParser)Activator.CreateInstance(parserKvp.Value);
            }
            if (Parser == null)
            {
                Logger.Warning($"Unknown parser ID: '{InputFileFormatId}'. Skipping subfile listing.");
            }
        }
        else
        {
            Logger.Info("Guessing input format from file extension…");
            var inputFileExt = Path.GetExtension(InputFilePath).ToLower();

            foreach (var parserKvp in availableParsers)
            {
                var parserAttr = parserKvp.Key;
                if (parserAttr.FileExtensions.Contains(inputFileExt))
                {
                    Logger.Debug($"Parser '{parserAttr.Id}' recognizes '{inputFileExt}' as a valid file extension.");
                    Parser = (IParser)Activator.CreateInstance(parserKvp.Value);
                    break;
                }
            }
            if (Parser == null)
            {
                Logger.Warning("Unknown input format. Skipping subfile listing.");
            }
        }
    }

    #endregion

    // 根据当前输出分辨率更新瀑布图缩放和在画面中的位置（瀑布固定在画面左侧）
    internal void UpdateValues()
    {
        if (_frameContent == null || _frameContent.Width != OutputVideoWidth || _frameContent.Height != OutputVideoHeight)
        {
            _frameContent = new(OutputVideoWidth, OutputVideoHeight);
            var pixelCount = OutputVideoWidth * OutputVideoHeight;

            // 按像素数量近似缩放瀑布图大小，保持大致视觉比例
            WaterfallScaledWidth = (int)(WaterfallWidth * (pixelCount / 691200f));
            WaterfallScaledHeight = (int)(WaterfallHeight * (pixelCount / 691200f));

            // 始终将瀑布图放在画面左侧 1/4 左右的位置，右侧留出大面积给列表和音频可视化
            _videoFrameX1 = OutputVideoWidth / 4 - WaterfallScaledWidth / 2;
            _videoFrameX2 = _videoFrameX1 + WaterfallScaledWidth;
            _videoFrameY1 = OutputVideoHeight / 2 - WaterfallScaledHeight / 2;
            _videoFrameY2 = _videoFrameY1 + WaterfallScaledHeight;
        }
    }

    // 总生成流程：Intro → 主视频
    public void Generate()
    {
        _timer.Start();

        // 1. Intro
        GenerateIntro();
        if (_exitRequested)
        {
            OnFinish?.Invoke();
            return;
        }

        // 2. Main Video
        GenerateMainVideo();

        OnFinish?.Invoke();
    }

    // 生成开头的免责声明画面
    private void GenerateIntro()
    {
        Logger.Info("Generating introduction…");

        var totalFrames = 5 * OutputFps; // 例如 60 FPS 时为 300 帧

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

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();

            OnProgress?.Invoke(frameNumber / (float)totalFrames);

            if (_exitRequested)
            {
                break;
            }
        }
    }

    // 生成主视频：左侧瀑布 + 右上列表 + 右下波形&频谱
    private void GenerateMainVideo()
    {
        Logger.Info("Generating binary waterfall…");

        // A/V 参数字符串
        string avSettingsString =
            $"{AudioInputSampleRate} Hz, PCM {(AudioInputSampleFormat.IsSigned() ? "signed" : "unsigned")} {8 * AudioInputSampleFormat.GetByteSize()}-bit, {(AudioInputChannelCount == 2 ? "stereo" : "mono")}\n" +
            $"RGBA (32bpp), {WaterfallWidth} px/line";
        string readSpeedString = $"{InputBytesPerSecond / 1024} KiB/s";

        // 子文件窗口索引（控制右侧列表滚动）
        float subfileWindowIndex = 0f;
        long currentOffset = 0;
        int playHeadRelPos = 0;

        using var targetFileReader = new BinaryReader(InputFileStream);

        while (currentOffset < InputFileStream.Length)
        {
            // 1. 计算当前帧对应的字节范围（视频）
            playHeadRelPos = 0;
            var frameStartByteOffset = currentOffset.Align(WaterfallWidth * 4) - (WaterfallFrameLength / 2);
            if (frameStartByteOffset < 0)
            {
                playHeadRelPos = (int)-(frameStartByteOffset / (WaterfallWidth * 4));
                frameStartByteOffset = 0;
            }
            else if (frameStartByteOffset + WaterfallFrameLength >= InputFileStream.Length)
            {
                playHeadRelPos = (int)((InputFileStream.Length - (frameStartByteOffset + WaterfallFrameLength)) / (WaterfallWidth * 4));
                frameStartByteOffset = InputFileStream.Length - WaterfallFrameLength;
            }
            var frameEndByteOffset = frameStartByteOffset + WaterfallFrameLength;

            InputFileStream.Position = frameStartByteOffset;
            var currentVideoBuffer = targetFileReader.ReadBytes(WaterfallFrameLength);

            // 2. 处理当前帧对应的音频缓冲
            if (_audioSampleSource != null)
            {
                // 使用 CSCore 解码的真实 PCM：直接按帧读取 float 样本
                int requiredSamples = AudioOutputSamplesPerFrame; // 每帧总样本数（含所有声道）
                if (_audioSampleBuffer == null || _audioSampleBuffer.Length != requiredSamples)
                {
                    _audioSampleBuffer = new float[requiredSamples];
                }

                int read = _audioSampleSource.Read(_audioSampleBuffer, 0, requiredSamples);
                if (read < requiredSamples)
                {
                    // 到达文件尾时，剩余样本清零，避免残留噪声
                    Array.Clear(_audioSampleBuffer, read, requiredSamples - read);
                }

                _outputAudioBuffer.LoadFromInterleavedFloats(_audioSampleBuffer, AudioOutputChannelCount);
            }
            else
            {
                // 回退：仍然从原始输入文件中按字节读取伪 PCM，并通过旧流程生成音频缓冲
                var audioFrameStartByteOffset = currentOffset.Align(AudioInputSampleFormat.GetByteSize()) - (InputBytesPerFrame / 2);
                if (audioFrameStartByteOffset < 0)
                {
                    audioFrameStartByteOffset = 0;
                }
                else if (audioFrameStartByteOffset + InputBytesPerFrame >= InputFileStream.Length)
                {
                    audioFrameStartByteOffset = InputFileStream.Length - InputBytesPerFrame;
                }
                var audioFrameEndByteOffset = audioFrameStartByteOffset + InputBytesPerFrame;

                InputFileStream.Position = audioFrameStartByteOffset;
                var currentAudioBuffer = targetFileReader.ReadBytes(InputBytesPerFrame);

                _inputAudioBuffer.LoadFromByteArray(currentAudioBuffer, AudioInputSampleFormat);
                _outputAudioBuffer = new AudioBuffer(_inputAudioBuffer)
                    .Resample(AudioOutputSamplesPerFramePerChannel)
                    .RemixChannels(AudioOutputChannelCount);
            }

            // 3. 加载视频帧到 ImageSharp 图像
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
            _viewportFramebuf.Mutate(ctx => ctx.Flip(FlipMode.Vertical)
                                               .Resize(WaterfallScaledWidth, WaterfallScaledHeight, new NearestNeighborResampler()));

            // 4. 计算当前帧对应的子文件窗口位置
            var subfilesInFrame = _subfiles
                .Select((sf, i) => new { key = i, value = sf })
                .Where(kvp => kvp.value.Intersects(currentOffset - (InputBytesPerFrame / 2), currentOffset + (InputBytesPerFrame / 2)))
                .ToList();
            var currentSubfile = subfilesInFrame.LastOrDefault();

            if (currentSubfile != null)
            {
                subfileWindowIndex = .2f * subfileWindowIndex + .8f * currentSubfile.key;
            }

            // 6. 实际绘制一帧
            _frameContent.Mutate(ctx =>
            {
                // 6.1 清屏
                ctx.Clear(new Rgba32(16, 16, 16, 255));

                // 6.2 右侧整体面板：上部列表 + 下部音频可视化
                int rightPanelX1 = _videoFrameX2 + 64;       // 瀑布右侧留出间距
                int rightPanelX2 = OutputVideoWidth - 32;    // 靠右留边
                int subfileX1 = rightPanelX1;
                int subfileX2 = rightPanelX2;
                int audioVisX1 = rightPanelX1;
                int audioVisX2 = rightPanelX2;

                float subfileH = 48f; // 每一行子文件条目的高度

                // 顶部/底部淡出渐变仍然以画面中线为基准，用于营造整体氛围
                float shadowY1 = (OutputVideoHeight / 2) - subfileH * 8.5f;
                float shadowY2 = (OutputVideoHeight / 2) + subfileH * 6.5f;

                // 右侧面板整体垂直范围：放在两个淡出区域之间，避免列表/波形/频谱被淡出遮挡
                float rightPanelTop = shadowY1 + subfileH * 2f + 16f;
                float rightPanelBottom = shadowY2 - 16f;
                float rightPanelHeight = rightPanelBottom - rightPanelTop;
                float listHeight = rightPanelHeight * 0.20f; // 顶部 20% 用于列表
                float listTop = rightPanelTop;
                float listBottom = listTop + listHeight;

                int firstSubfileIndex = (int)(subfileWindowIndex - 7);
                int lastSubfileIndex = (int)Math.Ceiling(subfileWindowIndex + 7);

                float subfileY = listTop + subfileH / 2 - (subfileWindowIndex - firstSubfileIndex) * subfileH;

                // 绘制右上“歌曲/子文件列表”
                for (int sfi = firstSubfileIndex; sfi <= lastSubfileIndex; sfi++)
                {
                    if (sfi < 0 || sfi >= _subfiles.Count)
                    {
                        subfileY += subfileH;
                        continue;
                    }

                    var subfile = _subfiles[sfi];
                    bool isMainSubfile = sfi == (currentSubfile?.key ?? -1);

                    ctx.DrawText(_drawOpts, new RichTextOptions(_font32)
                    {
                        Origin = new Vector2(subfileX1, subfileY),
                        VerticalAlignment = VerticalAlignment.Center,
                    }, isMainSubfile ? "▶" : " ", new SolidBrush(Color.White), null)
                    .DrawTextAndCache(_drawOpts, new RichTextOptions(_font32)
                    {
                        Origin = new Vector2(subfileX1 + 32, subfileY),
                        VerticalAlignment = VerticalAlignment.Center,
                        FallbackFontFamilies = _emojiFontFamily.Name != null ? [_emojiFontFamily] : null,
                    }, $"{Utils.GetFileTypeEmoji(subfile)} {Utils.TruncateString(subfile.FileName, 40)}", new SolidBrush(Color.White), null)
                    .DrawTextAndCache(_drawOpts, new RichTextOptions(_font32)
                    {
                        Origin = new Vector2(subfileX2, subfileY),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    }, Utils.ToByteSizeString(subfile.Length), new SolidBrush(Color.DimGray), null);

                    if (isMainSubfile)
                    {
                        float percentOfSubfile = (currentOffset - subfile.StartOffset) / (float)subfile.Length;

                        ctx.DrawText(_drawOpts, new RichTextOptions(_font16)
                        {
                            Origin = new PointF(subfileX1 + 48, subfileY + 20),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        }, $"{(int)Math.Clamp(percentOfSubfile * 100, 0, 100)} %", new SolidBrush(Color.White), null)
                        .DrawProgressBar(percentOfSubfile, subfileX1 + 80, subfileX2, subfileY + 20);
                    }

                    subfileY += subfileH;
                }

                // 6.3 左侧瀑布视图
                ctx.DrawImage(_viewportFramebuf, new Point(_videoFrameX1, _videoFrameY1), 1f)
                .DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(32, (OutputVideoHeight / 2) + (playHeadRelPos * (WaterfallScaledHeight / WaterfallHeight))),
                    VerticalAlignment = VerticalAlignment.Center,
                }, "▶", Color.White);

                // 6.4 右下音频可视化（波形 + 64 柱频谱）
                float audioVisTop = listBottom + 16f;
                float audioVisBottom = rightPanelBottom;
                var audioVisRegion = new RectangleF(audioVisX1, audioVisTop, audioVisX2 - audioVisX1, audioVisBottom - audioVisTop);
                DrawAudioVisualizer(ctx, audioVisRegion, _outputAudioBuffer);

                // 6.5 顶部/底部渐变遮罩列表（使用前面计算好的 shadowY1 / shadowY2）

                ctx.Fill(
                    new LinearGradientBrush(
                        new PointF(0, shadowY1),
                        new PointF(0, shadowY1 + subfileH * 2),
                        GradientRepetitionMode.None,
                        new(0.5f, Color.FromRgba(16, 16, 16, 255)),
                        new(1, Color.FromRgba(16, 16, 16, 0))
                    ),
                    new RectangleF(0, shadowY1, OutputVideoWidth, subfileH * 2)
                )
                .Fill(
                    new LinearGradientBrush(
                        new PointF(0, shadowY2),
                        new PointF(0, shadowY2 + subfileH * 2),
                        GradientRepetitionMode.None,
                        new(0, Color.FromRgba(16, 16, 16, 0)),
                        new(0.5f, Color.FromRgba(16, 16, 16, 255))
                    ),
                    new RectangleF(0, shadowY2, OutputVideoWidth, subfileH * 2)
                )
                .DrawTextAndCache(new RichTextOptions(_font24)
                {
                    // 将目录文字放在右侧面板顶部附近，避免被顶部淡出渐变盖住
                    Origin = new Vector2(subfileX1 + 40, rightPanelTop - 24f),
                    VerticalAlignment = VerticalAlignment.Center,
                }, Utils.TruncateString(currentSubfile?.value?.FileDirectory ?? string.Empty, 72), Color.DimGray);

                // 6.6 状态信息 / 标题 / 作者等
                ctx.DrawTextAndCache(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(32, 32),
                }, "A/V SETTINGS", Color.DimGray)
                .DrawText(new(_font32)
                {
                    Origin = new Vector2(32, 32 + 24),
                }, avSettingsString, Color.White)
                .DrawTextAndCache(new RichTextOptions(_font24)
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
                .DrawTextAndCache(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(OutputVideoWidth - 256, 32),
                    HorizontalAlignment = HorizontalAlignment.Right,
                }, "BITRATE", Color.DimGray)
                .DrawText(new(_font32)
                {
                    Origin = new Vector2(OutputVideoWidth - 256, 32 + 24),
                    HorizontalAlignment = HorizontalAlignment.Right,
                }, readSpeedString, Color.White);

                if (Author != null)
                {
                    ctx.DrawTextAndCache(new(_font32)
                    {
                        Origin = new Vector2(OutputVideoWidth / 2, 32 + 24),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    }, Author, Color.White);
                }

                if (Title != null)
                {
                    ctx.DrawTextAndCache(new RichTextOptions(_font24)
                    {
                        Origin = new Vector2(32, OutputVideoHeight - 64 - (Title.Contains('\n') ? 32 : 0)),
                        VerticalAlignment = VerticalAlignment.Bottom,
                    }, "TARGET", Color.DimGray)
                    .DrawTextAndCache(new(_font32)
                    {
                        Origin = new Vector2(32, OutputVideoHeight - 32),
                        VerticalAlignment = VerticalAlignment.Bottom,
                    }, Title, Color.White);
                }

                if (currentSubfile?.value?.Icon != null)
                {
                    ctx.DrawImage(currentSubfile.value.Icon, new Point(OutputVideoWidth / 2, OutputVideoHeight - 128 - 32), 1f);
                }

                if (currentSubfile?.value?.Description != null)
                {
                    ctx.DrawText(new(_font32)
                    {
                        Origin = new Vector2(OutputVideoWidth / 2 + 128 + 32, OutputVideoHeight - 32),
                        VerticalAlignment = VerticalAlignment.Bottom,
                    }, currentSubfile.value.Description, Color.White);
                }
            });

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();

            currentOffset += InputBytesPerFrame;

            OnProgress?.Invoke(currentOffset / (float)InputFileStream.Length);

            if (_exitRequested)
            {
                break;
            }
        }

        Exporter.Finish();
    }
	private static float[] ComputeSpectrumBins(float[] samples, int fftSize, int binCount)
	{
		int length = Math.Min(fftSize, samples.Length);
		if (length <= 0 || binCount <= 0)
		{
			return Array.Empty<float>();
		}

		int n = length;
		float[] magnitudes = new float[n / 2];
		for (int k = 0; k < magnitudes.Length; k++)
		{
			double sumRe = 0;
			double sumIm = 0;
			for (int t = 0; t < n; t++)
			{
				double angle = -2 * Math.PI * k * t / n;
				double c = Math.Cos(angle);
				double s = Math.Sin(angle);
				double v = samples[t];
				sumRe += v * c;
				sumIm += v * s;
			}
			magnitudes[k] = (float)Math.Sqrt((sumRe * sumRe) + (sumIm * sumIm));
		}

		int usableLength = magnitudes.Length;
		int sizePerBin = Math.Max(1, usableLength / binCount);
		float[] result = new float[binCount];
		for (int i = 0; i < binCount; i++)
		{
			int start = i * sizePerBin;
			int end = Math.Min(usableLength, start + sizePerBin);
			if (start >= end)
			{
				result[i] = 0;
				continue;
			}
			float sum = 0;
			for (int j = start; j < end; j++)
			{
				sum += magnitudes[j];
			}
			result[i] = sum / (end - start);
		}

		return result;
	}

	private void DrawAudioVisualizer(IImageProcessingContext ctx, RectangleF region, AudioBuffer audioBuffer)
	{
		// 音频缓冲区为空时不绘制
		if (audioBuffer == null || audioBuffer.TotalSampleCount == 0)
		{
			return;
		}

		int channelCount = audioBuffer.ChannelCount;
		int sampleCount = audioBuffer.SampleCount;
		if (channelCount <= 0 || sampleCount <= 0)
		{
			return;
		}

		// 将多声道音频混合为单声道，便于后续波形和频谱计算
		float[] interleaved = audioBuffer.ToArray();
		float[] mono = new float[sampleCount];
		for (int i = 0; i < sampleCount; i++)
		{
			double sum = 0;
			for (int ch = 0; ch < channelCount; ch++)
			{
				int idx = (i * channelCount) + ch;
				if (idx >= interleaved.Length)
				{
					break;
				}
				sum += interleaved[idx];
			}
			mono[i] = (float)(sum / channelCount);
		}

		// 限制 FFT 尺寸，防止每帧计算过重
		int fftSize = Math.Min(256, mono.Length);
		if (fftSize < 8)
		{
			return;
		}

		// 垂直布局：区域上方绘制波形，下方绘制频谱
		float waveformTop = region.Top;
		float waveformHeight = region.Height * 0.60f; // 上部 60% 用于波形
		float spectrumTop = waveformTop + waveformHeight;
		float spectrumHeight = region.Height - waveformHeight; // 下部区域用于频谱

		// 计算频谱柱数据（64 柱，默认值）
		float[] spectrumBins = ComputeSpectrumBins(mono, fftSize, 64);

		int binCount = spectrumBins.Length;
		if (binCount > 0)
		{
			float maxMag = spectrumBins.Max();
			if (maxMag <= 0)
			{
				maxMag = 1;
			}
			float barWidth = region.Width / binCount;
			for (int i = 0; i < binCount; i++)
			{
				float value = spectrumBins[i] / maxMag;
				value = Math.Clamp(value, 0f, 1f);
				float barHeight = value * spectrumHeight;
				var rect = new RectangleF(
					region.Left + (i * barWidth),
					spectrumTop + (spectrumHeight - barHeight),
					barWidth * 0.8f,
					barHeight);
				ctx.Fill(Color.White, rect);
			}
		}

		// 计算波形折线：从左到右均匀采样 mono 数组，映射到上半区域
		int pointCount = Math.Min(256, mono.Length);
		if (pointCount > 1)
		{
			PointF[] points = new PointF[pointCount];
			float centerY = waveformTop + (waveformHeight / 2f);
			float halfHeight = waveformHeight / 2f;
			for (int i = 0; i < pointCount; i++)
			{
				int srcIndex = (i * mono.Length) / pointCount;
				if (srcIndex >= mono.Length)
				{
					srcIndex = mono.Length - 1;
				}
				float sample = mono[srcIndex];
				sample = Math.Clamp(sample, -1f, 1f);
				float x = region.Left + (i / (float)(pointCount - 1)) * region.Width;
				float y = centerY - (sample * halfHeight);
				points[i] = new PointF(x, y);
			}

			ctx.DrawLine(new(), new SolidBrush(Color.White), 1, points);
		}
	}
}