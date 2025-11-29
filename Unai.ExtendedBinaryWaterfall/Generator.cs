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
using TagLib;

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
    // 频谱上一帧的柱状值，用于做简单的指数平滑，降低频谱动态速度
    private float[] _lastSpectrumBins = null;
    // 频谱峰值保持数组：记录每个柱的历史峰值，缓慢衰减
    private float[] _spectrumPeakHold = null;
    private int _videoFrameX1, _videoFrameX2, _videoFrameY1, _videoFrameY2;
    internal bool _exitRequested = false;

    // 公共方法：请求停止生成
    public void RequestStop() => _exitRequested = true;

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

    #region Visualizer Parameters

    [CliParameter("Waveform line width (pixels)", "waveform-line-width")]
    public float WaveformLineWidth { get; set; } = 4f; // 波形线条粗细（像素），默认 4

    [CliParameter("Waveform window length in milliseconds", "waveform-length-ms")]
    public float WaveformLengthMs { get; set; } = 50f; // 波形显示窗口长度（毫秒），控制时间轴缩放

    [CliParameter("Waveform display mode (average, diffavg, left, right, stereo)", "waveform-mode")]
    public string WaveformMode { get; set; } = "average"; // 波形显示模式：average/diffavg/left/right/stereo

    [CliParameter("Spectrum bar count", "spectrum-bars")]
    public int SpectrumBarCount { get; set; } = 64; // 频谱柱数量，范围 8~1024

    [CliParameter("Spectrum smoothing factor", "spectrum-smoothing")]
    public float SpectrumSmoothing { get; set; } = 0.6f; // 频谱平滑系数，范围 0.1~1

    [CliParameter("Intro fade duration in seconds", "intro-fade-duration")]
    public float IntroFadeDuration { get; set; } = 1.0f; // 开头免责声明的淡入淡出时长（秒）

    [CliParameter("Outro fade duration in seconds", "outro-fade-duration")]
    public float OutroFadeDuration { get; set; } = 2.0f; // 内容结束后的淡出时长（秒）

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
            InputFileStream = System.IO.File.OpenRead(InputFilePath);
        }

        if (InputAuxiliaryFileStream == null)
        {
            if (InputAuxiliaryFilePath != null)
            {
                Logger.Debug($"Opening file '{InputAuxiliaryFilePath}'…");
                InputAuxiliaryFileStream = System.IO.File.OpenRead(InputAuxiliaryFilePath);
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

    // 使用 CSCore 初始化音频解码器：根据输入文件自动选择合适的解码器，
    // 并让 AudioOutputSampleRate / AudioOutputChannelCount 直接跟随解码器参数。
    // 如果初始化失败，则回退到原先基于字节流的伪音频配置。
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

            // 如果可以估算音频总时长，则重算 InputBytesPerSecond，使“字节时间轴”和音频时间轴大致对齐
            long decodedBytes = _audioWaveSource.Length;
            int bytesPerSecond = wf.BytesPerSecond;
            if (decodedBytes > 0 && bytesPerSecond > 0 && InputFileStream != null && InputFileStream.Length > 0)
            {
                double durationSeconds = decodedBytes / (double)bytesPerSecond;
                if (durationSeconds > 0.1)
                {
                    InputBytesPerSecond = (int)(InputFileStream.Length / durationSeconds);
                }
            }

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

        // 尝试为每个子文件填充音频元数据（如果对应路径是可由 TagLib 识别的音频文件）
        PopulateSubfileMetadata();
    }

    /// 
    /// 使用 TagLib 为每个子文件填充基础音频元数据：专辑、碟号、曲号、曲名、艺术家和风格。
    /// 解析失败时记录 Debug 日志，不影响整体流程。
    
    private void PopulateSubfileMetadata()
    {
        foreach (var sf in _subfiles)
        {
            // 目录或路径为空时跳过
            if (sf == null || sf.IsDirectory || string.IsNullOrWhiteSpace(sf.Path))
            {
                continue;
            }

            try
            {
                using var tagFile = TagLib.File.Create(sf.Path);
                var tag = tagFile.Tag;
                if (tag == null)
                {
                    continue;
                }

                // 专辑名：优先使用 TagLib 的 Album
                if (string.IsNullOrWhiteSpace(sf.AlbumTitle) && !string.IsNullOrWhiteSpace(tag.Album))
                {
                    sf.AlbumTitle = tag.Album;
                }

                // 专辑作者/专辑艺术家：使用 TagLib 的 AlbumArtists
                if (string.IsNullOrWhiteSpace(sf.AlbumArtistName) && tag.AlbumArtists != null && tag.AlbumArtists.Length > 0)
                {
                    sf.AlbumArtistName = string.Join(", ", tag.AlbumArtists);
                }

                // 碟号 / 曲号
                if (!sf.DiscNumber.HasValue && tag.Disc != 0)
                {
                    sf.DiscNumber = (int)tag.Disc;
                }
                if (!sf.TrackNumber.HasValue && tag.Track != 0)
                {
                    sf.TrackNumber = (int)tag.Track;
                }

                // 曲名：如无则回退文件名
                if (string.IsNullOrWhiteSpace(sf.TrackTitle))
                {
                    if (!string.IsNullOrWhiteSpace(tag.Title))
                    {
                        sf.TrackTitle = tag.Title;
                    }
                    else
                    {
                        sf.TrackTitle = sf.FileName;
                    }
                }

                // 艺术家 / 作曲家
                if (string.IsNullOrWhiteSpace(sf.ArtistName))
                {
                    if (tag.Performers != null && tag.Performers.Length > 0)
                    {
                        sf.ArtistName = string.Join(", ", tag.Performers);
                    }
                    else if (tag.Composers != null && tag.Composers.Length > 0)
                    {
                        sf.ArtistName = string.Join(", ", tag.Composers);
                    }
                }

                // 风格
                if (string.IsNullOrWhiteSpace(sf.Genre) && tag.Genres != null && tag.Genres.Length > 0)
                {
                    sf.Genre = string.Join(", ", tag.Genres);
                }
            }
            catch (Exception ex)
            {
                // 某些子文件路径不是实际音频文件时可能会抛异常，这里只做调试输出
                Logger.Debug($"Failed to read metadata for subfile '{sf.Path}': {ex.Message}");
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

    // 根据当前输出分辨率更新瀑布图缩放和在画面中的位置
    internal void UpdateValues()
    {
        if (_frameContent == null || _frameContent.Width != OutputVideoWidth || _frameContent.Height != OutputVideoHeight)
        {
            _frameContent = new(OutputVideoWidth, OutputVideoHeight);
            var pixelCount = OutputVideoWidth * OutputVideoHeight;

            WaterfallScaledWidth = (int)(WaterfallWidth * (pixelCount / 691200f));
            WaterfallScaledHeight = (int)(WaterfallHeight * (pixelCount / 691200f));

            _videoFrameX1 = OutputVideoWidth / (_subfiles.Count > 0 ? 4 : 2) - WaterfallScaledWidth / 2;
            if (_videoFrameX2 == 0) _videoFrameX2 = _videoFrameX1 + WaterfallScaledWidth;
            _videoFrameY1 = OutputVideoHeight / 2 - WaterfallScaledHeight / 2;
            if (_videoFrameY2 == 0) _videoFrameY2 = _videoFrameY1 + WaterfallScaledHeight;
        }
    }

    // 总生成流程
    public void Generate()
    {
        _timer.Start();

        GenerateIntro();
        if (_exitRequested)
        {
            OnFinish?.Invoke();
            return;
        }

        GenerateMainVideo();

        OnFinish?.Invoke();
    }

    // 生成开头的免责声明画面（带淡入淡出效果）
    private void GenerateIntro()
    {
        Logger.Info("Generating introduction…");

        var totalFrames = 5 * OutputFps;
        // 淡入淡出的帧数（由 IntroFadeDuration 参数控制）
        int fadeFrames = (int)(IntroFadeDuration * OutputFps);
        fadeFrames = Math.Clamp(fadeFrames, 1, (int)(totalFrames / 3)); // 最多占总时长的 1/3

        for (long frameNumber = 0; frameNumber < totalFrames; frameNumber++)
        {
            // 计算当前帧的不透明度
            // 淡入：前 fadeFrames 帧从 0 渐变到 1
            // 淡出：后 fadeFrames 帧从 1 渐变到 0
            // 中间：保持 1
            float opacity = 1f;
            if (frameNumber < fadeFrames)
            {
                // 淡入阶段
                opacity = frameNumber / (float)fadeFrames;
            }
            else if (frameNumber >= totalFrames - fadeFrames)
            {
                // 淡出阶段
                opacity = (totalFrames - frameNumber) / (float)fadeFrames;
            }
            opacity = Math.Clamp(opacity, 0f, 1f);

            // 背景色
            _frameContent.Mutate(ctx => ctx.Clear(new Rgba32(16, 16, 16, 255)));

            // 根据 opacity 计算文字颜色（通过 alpha 通道实现淡入淡出）
            byte alpha = (byte)(255 * opacity);
            var textColor = Color.FromRgba(255, 255, 255, alpha);

            _frameContent.Mutate(av => av
                .DrawText(new RichTextOptions(_font48)
                {
                    Origin = new Vector2(OutputVideoWidth / 2f, OutputVideoHeight / 2f),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                }, "声明\n\n本视频由\nAudio-visualizer_extended-binary-waterfall\n项目进行生成", textColor)
                .DrawText(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(OutputVideoWidth / 2f, OutputVideoHeight - 128),
                    HorizontalAlignment = HorizontalAlignment.Center,
                }, $"Starting in {(totalFrames - frameNumber) / (float)OutputFps:N1} seconds…", textColor)
                .DrawProgressBar(frameNumber / (float)totalFrames,
                    (int)(OutputVideoWidth * 0.3f),
                    (int)(OutputVideoWidth * 0.7f),
                    OutputVideoHeight - 64,
                    opacity)); // 传递 opacity 给进度条

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();

            OnProgress?.Invoke(frameNumber / (float)totalFrames);

            if (_exitRequested)
            {
                break;
            }
        }
    }

    // 主体瀑布 + 右侧列表 + 右下音频可视化
    private void GenerateMainVideo()
    {
        Logger.Info("Generating binary waterfall…");

        string avSettingsString =
            $"{AudioOutputSampleRate} Hz, PCM {(AudioOutputSampleFormat.IsSigned() ? "signed" : "unsigned")} {8 * AudioOutputSampleFormat.GetByteSize()}-bit, " +
            $"{(AudioOutputChannelCount == 2 ? "stereo" : $"{AudioOutputChannelCount} ch")}\n" +
            $"RGBA (32bpp), {WaterfallWidth} px/line";
        string readSpeedString = $"{InputBytesPerSecond / 1024} KiB/s";

        float subfileWindowIndex = 0f;
        long currentOffset = 0;
        int playHeadRelPos = 0;

        using var targetFileReader = new BinaryReader(InputFileStream);

        while (currentOffset < InputFileStream.Length)
        {
            playHeadRelPos = 0;
            long frameStartByteOffset = currentOffset.Align(WaterfallWidth * 4) - (WaterfallFrameLength / 2);
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

            InputFileStream.Position = frameStartByteOffset;
            byte[] currentVideoBuffer = targetFileReader.ReadBytes(WaterfallFrameLength);

            // 获取音频缓冲
            if (_audioSampleSource != null)
            {
                int samplesPerFrame = AudioOutputSamplesPerFrame;
                if (_audioSampleBuffer == null || _audioSampleBuffer.Length < samplesPerFrame)
                {
                    _audioSampleBuffer = new float[samplesPerFrame];
                }

                int readSamples = _audioSampleSource.Read(_audioSampleBuffer, 0, samplesPerFrame);
                if (readSamples < samplesPerFrame)
                {
                    Array.Clear(_audioSampleBuffer, readSamples, samplesPerFrame - readSamples);
                }

                // 将交织 float PCM 写入输出缓冲供可视化使用
                _outputAudioBuffer.LoadFromInterleavedFloats(_audioSampleBuffer, AudioOutputChannelCount);
            }
            else
            {
                long audioFrameStartByteOffset = currentOffset.Align(AudioInputSampleFormat.GetByteSize()) - (InputBytesPerFrame / 2);
                if (audioFrameStartByteOffset < 0)
                {
                    audioFrameStartByteOffset = 0;
                }
                else if (audioFrameStartByteOffset + InputBytesPerFrame >= InputFileStream.Length)
                {
                    audioFrameStartByteOffset = InputFileStream.Length - InputBytesPerFrame;
                }
                long audioFrameEndByteOffset = audioFrameStartByteOffset + InputBytesPerFrame;

                InputFileStream.Position = audioFrameStartByteOffset;
                byte[] currentAudioBuffer = targetFileReader.ReadBytes(InputBytesPerFrame);

                _inputAudioBuffer.LoadFromByteArray(currentAudioBuffer, AudioInputSampleFormat);
                _outputAudioBuffer = new AudioBuffer(_inputAudioBuffer)
                    .Resample(AudioOutputSamplesPerFramePerChannel)
                    .RemixChannels(AudioOutputChannelCount);
            }

            // 3. 将视频字节缓冲转换为 Image，并翻转+缩放成用于绘制的瀑布视图
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
            _viewportFramebuf.Mutate(vctx => vctx
                .Flip(FlipMode.Vertical)
                .Resize(WaterfallScaledWidth, WaterfallScaledHeight, new NearestNeighborResampler()));

            // 4. 计算当前帧覆盖到哪些子文件，并更新右侧列表窗位置
            var subfilesInFrame = _subfiles
                .Select((sf, i) => new { key = i, value = sf })
                .Where(kvp => kvp.value.Intersects(currentOffset - (InputBytesPerFrame / 2),
                    currentOffset + (InputBytesPerFrame / 2)))
                .ToList();
            var currentSubfile = subfilesInFrame.LastOrDefault();

            if (currentSubfile != null)
            {
                subfileWindowIndex = 0.2f * subfileWindowIndex + 0.8f * currentSubfile.key;
            }

            // 5. 实际绘制一帧：左侧瀑布 + 右侧上部列表 + 右下音频可视化 + 顶/底渐变 + 文字信息
            _frameContent.Mutate(ctx =>
            {
                // 5.1 清屏
                ctx.Clear(new Rgba32(16, 16, 16, 255));

                // 5.2 右侧整体面板：上部列表 + 下部音频可视化
                int rightPanelX1 = _videoFrameX2 + 64;       // 瀑布右侧留出间距
                int rightPanelX2 = OutputVideoWidth - 32;    // 靠右留边
                int subfileX1 = rightPanelX1;
                int subfileX2 = rightPanelX2;
                int audioVisX1 = rightPanelX1;
                int audioVisX2 = rightPanelX2;

                float subfileH = 48f; // 每一行子文件条目的高度

                // 顶部/底部淡出渐变以画面中线为基准，用于营造整体氛围
                float shadowY1 = (OutputVideoHeight / 2f) - subfileH * 8.5f;
                float shadowY2 = (OutputVideoHeight / 2f) + subfileH * 6.5f;

                // 右侧面板整体垂直范围：放在两个淡出区域之间，避免列表/波形/频谱被淡出遮挡
                float rightPanelTop = shadowY1 + subfileH * 2f + 16f;
                float rightPanelBottom = shadowY2 - 16f;
                float rightPanelHeight = rightPanelBottom - rightPanelTop;
                float listHeight = rightPanelHeight * 0.20f; // 顶部 20% 用于列表
                float listTop = rightPanelTop;
                float listBottom = listTop + listHeight;

                int firstSubfileIndex = (int)(subfileWindowIndex - 7);
                int lastSubfileIndex = (int)Math.Ceiling(subfileWindowIndex + 7);

                float subfileY = listTop + subfileH / 2f - (subfileWindowIndex - firstSubfileIndex) * subfileH;

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
                    }, $"{Utils.GetFileTypeEmoji(subfile)} {Utils.TruncateString(BuildSubfileDisplayLine(subfile), 80)}", new SolidBrush(Color.White), null)
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

                // 5.3 左侧瀑布视图
                ctx.DrawImage(_viewportFramebuf, new Point(_videoFrameX1, _videoFrameY1), 1f)
                .DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(32, (OutputVideoHeight / 2f) + (playHeadRelPos * (WaterfallScaledHeight / (float)WaterfallHeight))),
                    VerticalAlignment = VerticalAlignment.Center,
                }, "▶", Color.White);

                // 5.4 右下音频可视化（波形 + 频谱）
                float audioVisTop = listBottom + 16f;
                float audioVisBottom = rightPanelBottom;
                if (audioVisBottom > audioVisTop + 16f && _outputAudioBuffer != null)
                {
                    var audioVisRegion = new RectangleF(audioVisX1, audioVisTop, audioVisX2 - audioVisX1, audioVisBottom - audioVisTop);
                    DrawAudioVisualizer(ctx, audioVisRegion, _outputAudioBuffer);
                }

                // 5.5 顶部/底部渐变遮罩 + 专辑/目录抬头文字
                string albumHeaderText;
                {
                    var sfValue = currentSubfile?.value;
                    if (sfValue != null && !string.IsNullOrWhiteSpace(sfValue.AlbumTitle))
                    {
                        albumHeaderText = sfValue.AlbumTitle;
                        if (!string.IsNullOrWhiteSpace(sfValue.AlbumArtistName))
                        {
                            albumHeaderText += " // " + sfValue.AlbumArtistName;
                        }
                    }
                    else
                    {
                        albumHeaderText = sfValue?.FileDirectory ?? string.Empty;
                    }
                }

                ctx.Fill(
                    new LinearGradientBrush(
                        new PointF(0, shadowY1),
                        new PointF(0, shadowY1 + subfileH * 2f),
                        GradientRepetitionMode.None,
                        new(0.5f, Color.FromRgba(16, 16, 16, 255)),
                        new(1, Color.FromRgba(16, 16, 16, 0))
                    ),
                    new RectangleF(0, shadowY1, OutputVideoWidth, subfileH * 2f)
                )
                .Fill(
                    new LinearGradientBrush(
                        new PointF(0, shadowY2),
                        new PointF(0, shadowY2 + subfileH * 2f),
                        GradientRepetitionMode.None,
                        new(0, Color.FromRgba(16, 16, 16, 0)),
                        new(0.5f, Color.FromRgba(16, 16, 16, 255))
                    ),
                    new RectangleF(0, shadowY2, OutputVideoWidth, subfileH * 2f)
                )
                .DrawTextAndCache(new RichTextOptions(_font24)
                {
                    // 将专辑/目录文字放在右侧面板顶部附近，避免被顶部淡出渐变盖住
                    Origin = new Vector2(subfileX1 + 40, rightPanelTop - 24f),
                    VerticalAlignment = VerticalAlignment.Center,
                }, Utils.TruncateString(albumHeaderText ?? string.Empty, 72), Color.DimGray);

                // 5.6 状态信息 / 标题 / 作者等
                ctx.DrawTextAndCache(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(32, 32),
                }, "A/V SETTINGS", Color.DimGray)
                .DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(32, 32 + 24),
                }, avSettingsString, Color.White)
                .DrawTextAndCache(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(OutputVideoWidth - 32, 32),
                    HorizontalAlignment = HorizontalAlignment.Right,
                }, "ABS. OFFSET", Color.DimGray)
                .DrawText(new RichTextOptions(_font32)
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
                .DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(OutputVideoWidth - 256, 32 + 24),
                    HorizontalAlignment = HorizontalAlignment.Right,
                }, readSpeedString, Color.White);

                if (Author != null)
                {
                    ctx.DrawTextAndCache(new RichTextOptions(_font32)
                    {
                        Origin = new Vector2(OutputVideoWidth / 2f, 32 + 24),
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
                    .DrawTextAndCache(new RichTextOptions(_font32)
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
                    ctx.DrawText(new RichTextOptions(_font32)
                    {
                        Origin = new Vector2(OutputVideoWidth / 2f + 128 + 32, OutputVideoHeight - 32),
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

        // 生成结尾淡出效果
        if (!_exitRequested)
        {
            GenerateOutro();
        }

        Exporter.Finish();
    }

    // 生成结尾淡出画面
    private void GenerateOutro()
    {
        Logger.Info("Generating outro fade…");
        int fadeFrames = (int)(OutroFadeDuration * OutputFps);
        if (fadeFrames < 1) fadeFrames = 1;

        for (int frameNumber = 0; frameNumber < fadeFrames; frameNumber++)
        {
            float opacity = 1f - (frameNumber / (float)fadeFrames);
            byte overlayAlpha = (byte)(255 * (1f - Math.Clamp(opacity, 0f, 1f)));

            _frameContent.Mutate(ctx =>
            {
                ctx.Fill(Color.FromRgba(16, 16, 16, overlayAlpha),
                    new RectangleF(0, 0, OutputVideoWidth, OutputVideoHeight));
            });

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();
            if (_exitRequested) break;
        }
    }

    // 计算频谱：Hann窗 + DFT + 对数频率映射
    private static float[] ComputeSpectrumBins(float[] samples, int fftSize, int binCount, float gamma = 2.0f)
    {
        int length = Math.Min(fftSize, samples.Length);
        if (length <= 0 || binCount <= 0) return Array.Empty<float>();

        int n = length;
        float[] windowed = new float[n];
        float windowSum = 0f;
        for (int i = 0; i < n; i++)
        {
            float window = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * i / (n - 1)));
            windowed[i] = samples[i] * window;
            windowSum += window;
        }

        float normFactor = 2.0f / windowSum;
        int halfN = n / 2;
        float[] magnitudes = new float[halfN];
        for (int k = 0; k < halfN; k++)
        {
            double sumRe = 0, sumIm = 0;
            for (int t = 0; t < n; t++)
            {
                double angle = -2.0 * Math.PI * k * t / n;
                sumRe += windowed[t] * Math.Cos(angle);
                sumIm += windowed[t] * Math.Sin(angle);
            }
            magnitudes[k] = (float)(Math.Sqrt(sumRe * sumRe + sumIm * sumIm) * normFactor);
        }

        float[] result = new float[binCount];
        for (int i = 0; i < binCount; i++)
        {
            float posLow = i / (float)binCount;
            float posHigh = (i + 1) / (float)binCount;
            float freqLow = (float)Math.Pow(posLow, gamma);
            float freqHigh = (float)Math.Pow(posHigh, gamma);

            int kLow = Math.Max(1, (int)(freqLow * (halfN - 1)));
            int kHigh = Math.Max(kLow, (int)(freqHigh * (halfN - 1)));
            if (kHigh >= halfN) kHigh = halfN - 1;

            float maxMag = 0f;
            for (int k = kLow; k <= kHigh; k++)
                if (magnitudes[k] > maxMag) maxMag = magnitudes[k];
            result[i] = maxMag;
        }
        return result;
    }

    // 音频可视化绘制：上半部分为波形，下半部分为频谱
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

        // 将交织 PCM 拆分为每通道数组，并计算平均波形 mono，便于后续频谱与默认波形计算
        float[] interleaved = audioBuffer.ToArray(); // 当前帧交织格式 PCM
        float[][] channels = new float[channelCount][]; // 每个通道独立波形
        for (int ch = 0; ch < channelCount; ch++)
        {
            channels[ch] = new float[sampleCount];
        }
        float[] mono = new float[sampleCount]; // 各通道平均后的单声道波形
        for (int i = 0; i < sampleCount; i++)
        {
            double sum = 0;
            int usedChannels = 0;
            for (int ch = 0; ch < channelCount; ch++)
            {
                int idx = (i * channelCount) + ch;
                if (idx >= interleaved.Length)
                {
                    break;
                }
                float v = interleaved[idx];
                channels[ch][i] = v;
                sum += v;
                usedChannels++;
            }
            mono[i] = usedChannels > 0 ? (float)(sum / usedChannels) : 0f;
        }

        // 频谱 FFT 尺寸：使用较大的窗口以获得更好的低频分辨率
        // 48kHz 采样率下：1024 点 FFT → 约 47 Hz/bin 分辨率
        // 这比 256 点的 187 Hz/bin 分辨率好很多，能更准确地显示低频
        int fftSize = Math.Min(1024, mono.Length);
        if (fftSize < 64)
        {
            return;
        }

        // 垂直布局：区域上方绘制波形，下方绘制频谱
        float waveformTop = region.Top;
        float waveformHeight = region.Height * 0.60f; // 上部 60% 用于波形
        float spectrumTop = waveformTop + waveformHeight;
        float spectrumHeight = region.Height - waveformHeight; // 下部区域用于频谱

        // 频谱参数：柱数量和平滑系数，来源于用户可配置参数
        int barCount = Math.Clamp(SpectrumBarCount, 8, 1024); // 频谱柱数量 8~1024
        float smoothing = SpectrumSmoothing;
        if (float.IsNaN(smoothing) || float.IsInfinity(smoothing))
        {
            smoothing = 0.6f;
        }
        smoothing = Math.Clamp(smoothing, 0.1f, 1f); // 平滑系数限制在 0.1~1

        // 计算频谱柱数据
        float[] spectrumRaw = ComputeSpectrumBins(mono, fftSize, barCount);
        int binCount = spectrumRaw.Length;
        if (binCount > 0)
        {
            // 先转换到 dB 域，使用固定参考电平而非逐帧归一化
            // 这样可以保持不同音量段之间的相对差异
            const float minDb = -80f;   // 噪声底（dB）
            const float maxDb = 0f;     // 参考电平（满幅度）
            const float dbRange = maxDb - minDb;

            float[] currentDbValues = new float[binCount];
            for (int i = 0; i < binCount; i++)
            {
                float magnitude = spectrumRaw[i];
                // 幅度转 dBFS: 20 * log10(magnitude)，满幅度正弦波 = 0 dBFS
                float db = 20f * (float)Math.Log10(magnitude + 1e-10f);
                currentDbValues[i] = db;
            }

            // 初始化平滑缓冲
            if (_lastSpectrumBins == null || _lastSpectrumBins.Length != binCount)
            {
                _lastSpectrumBins = new float[binCount];
                Array.Copy(currentDbValues, _lastSpectrumBins, binCount);
            }

            // 快速上升、适度下降的平滑策略（在 dB 域进行）
            // 攻击：几乎瞬时响应（系数接近 1）
            // 释放：根据用户 smoothing 参数调节，但保持足够的动态感
            float attackCoeff = 0.9f;                      // 上升时的跟随系数（越大越快）
            float releaseCoeff = smoothing;                // 直接使用用户设置的平滑系数作为释放速度

            for (int i = 0; i < binCount; i++)
            {
                float prev = _lastSpectrumBins[i];
                float curr = currentDbValues[i];

                if (curr > prev)
                {
                    // 信号上升：几乎瞬时跟随，让频谱对节拍有即时反应
                    _lastSpectrumBins[i] = prev + (curr - prev) * attackCoeff;
                }
                else
                {
                    // 信号下降：根据 smoothing 参数控制衰减速度
                    // smoothing 越大，衰减越快（更有动感）；越小，衰减越慢（更平滑）
                    _lastSpectrumBins[i] = prev + (curr - prev) * releaseCoeff;
                }
            }

            // 高频补偿：人耳对高频不太敏感，适当提升高频显示
            float[] dbToDraw = new float[binCount];
            for (int j = 0; j < binCount; j++)
            {
                float t = j / (float)Math.Max(1, binCount - 1); // 0..1
                // 高频提升曲线：低频不变，高频最多提升 12 dB
                float boost = t * t * 12f;
                dbToDraw[j] = _lastSpectrumBins[j] + boost;
            }

            // 峰值保持：跟踪每个柱的历史峰值，缓慢衰减
            // 这是专业频谱仪的常见功能，可以更好地显示瞬态峰值
            if (_spectrumPeakHold == null || _spectrumPeakHold.Length != binCount)
            {
                _spectrumPeakHold = new float[binCount];
                for (int i = 0; i < binCount; i++)
                {
                    _spectrumPeakHold[i] = minDb;
                }
            }

            // 峰值衰减速度：每帧下降约 0.5 dB（60fps 下约 30 dB/秒）
            const float peakDecayPerFrame = 0.5f;
            for (int i = 0; i < binCount; i++)
            {
                float currentDb = dbToDraw[i];
                if (currentDb > _spectrumPeakHold[i])
                {
                    // 新峰值：立即更新
                    _spectrumPeakHold[i] = currentDb;
                }
                else
                {
                    // 缓慢衰减
                    _spectrumPeakHold[i] -= peakDecayPerFrame;
                    if (_spectrumPeakHold[i] < minDb)
                    {
                        _spectrumPeakHold[i] = minDb;
                    }
                }
            }

            // 绘制频谱柱和峰值指示器
            float barWidth = region.Width / binCount;
            float barGap = barWidth * 0.15f;  // 柱子之间的间隙
            float actualBarWidth = barWidth - barGap;

            for (int i = 0; i < binCount; i++)
            {
                float barX = region.Left + (i * barWidth) + (barGap / 2f);

                // 将 dB 值映射到 [0, 1] 显示高度
                float normalized = (dbToDraw[i] - minDb) / dbRange;
                normalized = Math.Clamp(normalized, 0f, 1f);

                float barHeight = normalized * spectrumHeight;
                if (barHeight < 1f) barHeight = 1f;  // 最小 1 像素

                // 绘制主频谱柱
                var barRect = new RectangleF(
                    barX,
                    spectrumTop + (spectrumHeight - barHeight),
                    actualBarWidth,
                    barHeight);
                ctx.Fill(Color.White, barRect);

                // 绘制峰值指示器（细线）
                float peakNormalized = (_spectrumPeakHold[i] - minDb) / dbRange;
                peakNormalized = Math.Clamp(peakNormalized, 0f, 1f);
                float peakY = spectrumTop + (spectrumHeight * (1f - peakNormalized));

                // 只有当峰值高于当前柱高时才绘制峰值指示器
                if (peakNormalized > normalized + 0.02f)
                {
                    var peakRect = new RectangleF(
                        barX,
                        peakY,
                        actualBarWidth,
                        2f);  // 2 像素高的峰值指示线
                    ctx.Fill(Color.Gray, peakRect);
                }
            }
        }

        // 波形部分：根据 WaveformLengthMs 与 WaveformMode 生成可视窗口波形，并进行归一化
        int windowSamples = (int)(AudioOutputSampleRate * WaveformLengthMs / 1000f); // 目标窗口长度（样本数）
        if (windowSamples < 8)
        {
            windowSamples = 8;
        }
        if (windowSamples > sampleCount)
        {
            windowSamples = sampleCount;
        }
        int windowStart = sampleCount - windowSamples; // 使用当前帧最后一段样本作为可视窗口

        string mode = (WaveformMode ?? "average").ToLowerInvariant(); // 波形显示模式
        bool drawStereo = false;
        float[] waveMain = null;   // 单声道波形
        float[] waveLeft = null;   // 立体声左通道
        float[] waveRight = null;  // 立体声右通道

        bool hasLeft = channelCount >= 1;
        bool hasRight = channelCount >= 2;

        switch (mode)
        {
            case "left":
                // 只显示左声道
                waveMain = new float[windowSamples];
                for (int i = 0; i < windowSamples; i++)
                {
                    waveMain[i] = hasLeft ? channels[0][windowStart + i] : 0f;
                }
                break;

            case "right":
                // 只显示右声道（若无右声道则退回左声道）
                waveMain = new float[windowSamples];
                if (hasRight)
                {
                    for (int i = 0; i < windowSamples; i++)
                    {
                        waveMain[i] = channels[1][windowStart + i];
                    }
                }
                else if (hasLeft)
                {
                    for (int i = 0; i < windowSamples; i++)
                    {
                        waveMain[i] = channels[0][windowStart + i];
                    }
                }
                else
                {
                    Array.Clear(waveMain, 0, waveMain.Length);
                }
                break;

            case "diffavg":
                // 左右声道差值：反映立体声宽度
                waveMain = new float[windowSamples];
                if (hasLeft && hasRight)
                {
                    for (int i = 0; i < windowSamples; i++)
                    {
                        float l = channels[0][windowStart + i];
                        float r = channels[1][windowStart + i];
                        waveMain[i] = (l - r) * 0.5f;
                    }
                }
                else
                {
                    Array.Clear(waveMain, 0, waveMain.Length);
                }
                break;

            case "stereo":
                // 立体声：同时绘制左右两个波形，并共享同一归一化因子
                drawStereo = true;
                waveLeft = new float[windowSamples];
                waveRight = new float[windowSamples];
                for (int i = 0; i < windowSamples; i++)
                {
                    waveLeft[i] = hasLeft ? channels[0][windowStart + i] : 0f;
                    if (hasRight)
                    {
                        waveRight[i] = channels[1][windowStart + i];
                    }
                    else
                    {
                        waveRight[i] = waveLeft[i];
                    }
                }
                break;

            default:
                // average（默认）：所有声道平均
                waveMain = new float[windowSamples];
                for (int i = 0; i < windowSamples; i++)
                {
                    waveMain[i] = mono[windowStart + i];
                }
                break;
        }

        // 对选中的波形进行归一化，使波形在可视区域内充分占用高度
        const float minNorm = 1e-6f;
        if (drawStereo && waveLeft != null && waveRight != null)
        {
            // stereo 模式：左右声道共享同一个归一化因子
            float maxAbs = 0f;
            for (int i = 0; i < windowSamples; i++)
            {
                float a = Math.Abs(waveLeft[i]);
                if (a > maxAbs) maxAbs = a;
                float b = Math.Abs(waveRight[i]);
                if (b > maxAbs) maxAbs = b;
            }
            if (maxAbs < minNorm)
            {
                maxAbs = 1f;
            }
            float inv = 1f / maxAbs;
            for (int i = 0; i < windowSamples; i++)
            {
                waveLeft[i] *= inv;
                waveRight[i] *= inv;
            }
        }
        else if (waveMain != null)
        {
            float maxAbs = 0f;
            for (int i = 0; i < windowSamples; i++)
            {
                float a = Math.Abs(waveMain[i]);
                if (a > maxAbs) maxAbs = a;
            }
            if (maxAbs < minNorm)
            {
                maxAbs = 1f;
            }
            float inv = 1f / maxAbs;
            for (int i = 0; i < windowSamples; i++)
            {
                waveMain[i] *= inv;
            }
        }

        // 使用 Min/Max 包络线渲染波形（类似 DAW 风格）
        // 每个像素列计算对应样本范围的最小值和最大值，绘制垂直线段
        // 这样可以保留瞬态细节，比单点采样更专业
        // 列数设为区域实际宽度，最大 2048 像素以支持高分辨率显示
        int columnCount = (int)region.Width;
        if (columnCount < 8) columnCount = 8;
        if (columnCount > 2048) columnCount = 2048;

        float lineWidth = WaveformLineWidth;
        if (lineWidth <= 0f || float.IsNaN(lineWidth) || float.IsInfinity(lineWidth))
        {
            lineWidth = 2f;
        }

        var waveformBrush = new SolidBrush(Color.White);

        if (!drawStereo && waveMain != null)
        {
            float centerY = waveformTop + (waveformHeight / 2f);
            float halfHeight = waveformHeight / 2f;

            // 计算每列对应的样本数
            float samplesPerColumn = windowSamples / (float)columnCount;

            for (int col = 0; col < columnCount; col++)
            {
                // 该列对应的样本范围
                int startSample = (int)(col * samplesPerColumn);
                int endSample = (int)((col + 1) * samplesPerColumn);
                if (endSample > windowSamples) endSample = windowSamples;
                if (startSample >= endSample) continue;

                // 找出该范围内的最小和最大值
                float minVal = float.MaxValue;
                float maxVal = float.MinValue;
                for (int s = startSample; s < endSample; s++)
                {
                    float v = waveMain[s];
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }

                // 转换为屏幕坐标并绘制垂直线段
                float x = region.Left + (col / (float)(columnCount - 1)) * region.Width;
                float yMin = centerY - (maxVal * halfHeight); // maxVal 在上方
                float yMax = centerY - (minVal * halfHeight); // minVal 在下方

                // 确保至少有 1 像素高度
                if (yMax - yMin < 1f) yMax = yMin + 1f;

                ctx.Fill(waveformBrush, new RectangleF(x - lineWidth / 2f, yMin, lineWidth, yMax - yMin));
            }
        }
        else if (drawStereo && waveLeft != null && waveRight != null)
        {
            // 立体声：上下两个波形区域
            float centerYLeft = waveformTop + (waveformHeight * 0.25f);
            float halfHeightLeft = waveformHeight * 0.22f;  // 留一点间距
            float centerYRight = waveformTop + (waveformHeight * 0.75f);
            float halfHeightRight = waveformHeight * 0.22f;

            float samplesPerColumn = windowSamples / (float)columnCount;

            for (int col = 0; col < columnCount; col++)
            {
                int startSample = (int)(col * samplesPerColumn);
                int endSample = (int)((col + 1) * samplesPerColumn);
                if (endSample > windowSamples) endSample = windowSamples;
                if (startSample >= endSample) continue;

                // 左声道 min/max
                float minL = float.MaxValue, maxL = float.MinValue;
                float minR = float.MaxValue, maxR = float.MinValue;
                for (int s = startSample; s < endSample; s++)
                {
                    float vL = waveLeft[s];
                    float vR = waveRight[s];
                    if (vL < minL) minL = vL;
                    if (vL > maxL) maxL = vL;
                    if (vR < minR) minR = vR;
                    if (vR > maxR) maxR = vR;
                }

                float x = region.Left + (col / (float)(columnCount - 1)) * region.Width;

                // 左声道
                float yMinL = centerYLeft - (maxL * halfHeightLeft);
                float yMaxL = centerYLeft - (minL * halfHeightLeft);
                if (yMaxL - yMinL < 1f) yMaxL = yMinL + 1f;
                ctx.Fill(waveformBrush, new RectangleF(x - lineWidth / 2f, yMinL, lineWidth, yMaxL - yMinL));

                // 右声道
                float yMinR = centerYRight - (maxR * halfHeightRight);
                float yMaxR = centerYRight - (minR * halfHeightRight);
                if (yMaxR - yMinR < 1f) yMaxR = yMinR + 1f;
                ctx.Fill(waveformBrush, new RectangleF(x - lineWidth / 2f, yMinR, lineWidth, yMaxR - yMinR));
            }
        }
    }

    private string BuildSubfileDisplayLine(SubFile subfile)
    {
        // 构造单行显示文本：Disc.Track- Title // Artist [Genre]
        // 按需省略符号，保持在一行内，最终再由外层做截断
        if (subfile == null)
        {
            return string.Empty;
        }

        string line = string.Empty;

        // 碟号 / 曲号前缀
        string prefix = string.Empty;
        if (subfile.DiscNumber.HasValue)
        {
            prefix = subfile.DiscNumber.Value.ToString();
        }
        if (subfile.TrackNumber.HasValue)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                prefix += ".";
            }
            prefix += subfile.TrackNumber.Value.ToString();
        }
        if (!string.IsNullOrEmpty(prefix))
        {
            line += prefix + "- ";
        }

        // 曲名：优先 TrackTitle，没有则退回文件名
        string title = !string.IsNullOrWhiteSpace(subfile.TrackTitle)
            ? subfile.TrackTitle
            : subfile.FileName;
        line += title;

        // 艺术家：存在时添加 " // Artist"
        if (!string.IsNullOrWhiteSpace(subfile.ArtistName))
        {
            line += " // " + subfile.ArtistName;
        }

        // 风格：存在时添加 " [Genre]"
        if (!string.IsNullOrWhiteSpace(subfile.Genre))
        {
            line += " [" + subfile.Genre + "]";
        }

        return line;
    }
    #endregion
}