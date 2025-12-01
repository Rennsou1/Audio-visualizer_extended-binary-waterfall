using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using CSCore;
using CSCore.Codecs;
using SkiaSharp;
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

    // 渲染相关字段
    private SKBitmap _frameContent = null;           // 主帧内容位图
    private SKCanvas _frameCanvas = null;            // 主帧画布（复用，避免每帧创建）
    private SKBitmap _viewportFramebuf = null;       // 瀑布视图位图
    private AudioBuffer _inputAudioBuffer = null;
    private AudioBuffer _outputAudioBuffer = null;
    // CSCore 音频解码相关字段：使用 CodecFactory 自动根据扩展名选择解码器
    private IWaveSource _audioWaveSource = null;
    private ISampleSource _audioSampleSource = null;
    // 用于暂存每次渲染的 float PCM 数据
    private float[] _audioSampleBuffer = null;
    private int _videoFrameX1, _videoFrameX2, _videoFrameY1, _videoFrameY2;
    internal bool _exitRequested = false;
    
    // 歌曲切换动画状态
    private int _lastSubfileIndex = -1;
    
    // 音频可视化复用数组（避免每帧分配）
    private float[] _audioMonoBuffer = null;
    private float[] _audioInterleavedBuffer = null;
    
    // 视频帧缓冲区复用（避免每帧分配）
    private byte[] _reusableVideoBuffer = null;
    
    // 瀑布原始图像复用
    private SKBitmap _reusableWaterfallImage = null;
    // 缩放后的瀑布图像复用（避免每帧 Clone+Resize 导致的内存分配）
    private SKBitmap _reusableScaledWaterfall = null;
    
    // 预渲染的渐变着色器
    private SKShader _cachedTopGradientShader = null;
    private SKShader _cachedBottomGradientShader = null;
    private float _cachedGradientY1 = -1;
    private float _cachedGradientY2 = -1;
    private float _cachedGradientHeight = -1;
    
    // 文本测量缓存（避免重复测量相同文本）
    private Dictionary<(string text, float fontSize), float> _textWidthCache = new();
    private const int MaxTextCacheSize = 256;
    
    // 预渲染的静态 UI 元素
    // 封面图片缓存（避免每帧 Clone + Resize）
    private SKBitmap _cachedScaledCover = null;
    private int _cachedCoverSubfileIndex = -1;
    private int _cachedCoverSize = 0;
    
    // 静态 UI 图层缓存（包含不变的标签文本）
    private SKBitmap _staticUILayer = null;
    private bool _staticUILayerValid = false;
    
    // 高级切换动画状态（基于时间）
    private string _prevDisplayInfo = "";         // 前一首显示信息（用于字符动画）
    private string _prevTimeString = "";          // 前一首时间字符串
    private float[] _prevWaveformPeaks = null;    // 前一首波形数据
    private SKBitmap _prevScaledCover = null;     // 前一首缩放后的封面
    private byte[] _prevCoverHash = null;         // 前一首封面哈希（用于判断是否相同）
    private double _animationStartTime = -1;      // 动画开始时间（秒）
    private double _currentVideoTime = 0;         // 当前视频时间（秒）
    private const double AnimationDurationSeconds = 0.5; // 动画持续时间（秒）
    
    // 字符动画缓存（避免每帧分配列表）
    private readonly List<(char c, float x, float width)> _oldCharPositions = new();
    private readonly List<(char c, float x, float width)> _newCharPositions = new();
    
    // 波形点缓存（用于重用 SKPoint 数组）
    private SKPoint[] _waveformPointCache = null;
    
    // 可复用的 Paint 对象（避免每帧创建）
    private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, SubpixelText = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _imagePaint = new() { FilterQuality = SKFilterQuality.None };  // 最近邻插值
    
    // Ease-out 缓动函数（快到慢）
    private static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);
    
    // Ease-in-out 缓动函数（适合字符动画）
    private static float EaseInOutQuad(float t) => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f;
    
    // 计算封面图像的简单哈希（用于比较是否相同）
    private static byte[] ComputeImageHash(SKBitmap image)
    {
        if (image == null) return null;
        // 简单取样哈希：取四角和中心的像素值
        int w = image.Width, h = image.Height;
        var hash = new byte[20];
        var positions = new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1), (w / 2, h / 2) };
        int idx = 0;
        foreach (var (x, y) in positions)
        {
            var pixel = image.GetPixel(x, y);
            hash[idx++] = pixel.Red;
            hash[idx++] = pixel.Green;
            hash[idx++] = pixel.Blue;
            hash[idx++] = pixel.Alpha;
        }
        return hash;
    }
    
    // 比较两个哈希是否相同
    private static bool HashEquals(byte[] a, byte[] b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
    
    // 测量文本宽度
    private float MeasureTextWidth(string text, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        // 使用缓存避免重复测量相同文本
        var key = (text, fontSize);
        if (_textWidthCache.TryGetValue(key, out float cachedWidth))
        {
            return cachedWidth;
        }
        
        // 缓存未命中，执行测量
        _textPaint.TextSize = fontSize;
        _textPaint.Typeface = _typeface;
        float width = _textPaint.MeasureText(text);
        
        // 缓存清理
        if (_textWidthCache.Count >= MaxTextCacheSize)
        {
            _textWidthCache.Clear();
        }
        
        _textWidthCache[key] = width;
        return width;
    }
    
    // 裁剪文本以适应指定宽度（使用省略号）
    private string ClampTextToWidth(string text, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text;
        float textWidth = MeasureTextWidth(text, fontSize);
        if (textWidth <= maxWidth) return text;
        
        const string ellipsis = "…";
        float ellipsisWidth = MeasureTextWidth(ellipsis, fontSize);
        float availableWidth = maxWidth - ellipsisWidth;
        
        // 二分查找最大可容纳字符数
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (MeasureTextWidth(text[..mid], fontSize) <= availableWidth)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo > 0 ? text[..lo] + ellipsis : ellipsis;
    }
    
    // 计算并填充每个字符的位置信息
    private void PopulateCharacterPositions(string text, float fontSize, List<(char c, float x, float width)> positions)
    {
        positions.Clear();
        if (string.IsNullOrEmpty(text)) return;
        
        _textPaint.Typeface = _typeface;
        _textPaint.TextSize = fontSize;
        
        float currentX = 0f;
        foreach (char c in text)
        {
            string charStr = c.ToString();
            float charWidth = _textPaint.MeasureText(charStr);
            positions.Add((c, currentX, charWidth));
            currentX += charWidth;
        }
    }
    
    // 确保帧画布已创建（复用 SKCanvas 避免重复分配）
    private void EnsureFrameCanvas()
    {
        if (_frameContent == null) return;
        _frameCanvas ??= new SKCanvas(_frameContent);
    }
    
    // 通用文本绘制辅助方法，支持对齐和字体大小
    private void DrawText(float x, float y, float fontSize, string text, SKColor color,
        VerticalAlign vAlign = VerticalAlign.Top, HorizontalAlign hAlign = HorizontalAlign.Left, SKTypeface typeface = null)
    {
        if (string.IsNullOrEmpty(text) || _frameCanvas == null) return;
        typeface ??= _typeface;
        _frameCanvas.DrawTextAndCache(typeface, fontSize, text, x, y, color, hAlign, vAlign);
    }
    
    // 绘制多行文本（自动换行并居中，用于 Intro 文案）
    private void DrawMultilineCentered(string text, float centerX, float centerY, float fontSize, SKColor color, float lineSpacingMultiplier = 1.25f)
    {
        if (string.IsNullOrEmpty(text) || _frameCanvas == null) return;
        string[] lines = text.Split('\n');
        float lineSpacing = fontSize * lineSpacingMultiplier;
        float totalHeight = lineSpacing * (lines.Length - 1);
        for (int i = 0; i < lines.Length; i++)
        {
            float lineY = centerY - totalHeight / 2f + i * lineSpacing;
            DrawText(centerX, lineY, fontSize, lines[i], color, VerticalAlign.Center, HorizontalAlign.Center);
        }
    }
    
    // 预渲染渐变着色器（SKShader）
    private void EnsureGradientShadersCached(float shadowY1, float shadowY2, float gradientHeight)
    {
        // 检查是否需要重新生成缓存
        if (_cachedTopGradientShader != null && 
            Math.Abs(_cachedGradientY1 - shadowY1) < 0.1f &&
            Math.Abs(_cachedGradientY2 - shadowY2) < 0.1f &&
            Math.Abs(_cachedGradientHeight - gradientHeight) < 0.1f)
        {
            return; // 缓存有效，直接返回
        }
        
        // 释放旧缓存
        _cachedTopGradientShader?.Dispose();
        _cachedBottomGradientShader?.Dispose();
        
        // 背景色（深灰色）
        var bgColor = new SKColor(16, 16, 16);
        var bgColorTransparent = new SKColor(16, 16, 16, 0);
        
        // 创建顶部渐变着色器（从不透明到透明）
        _cachedTopGradientShader = SKShader.CreateLinearGradient(
            new SKPoint(0, shadowY1),
            new SKPoint(0, shadowY1 + gradientHeight),
            new[] { bgColor, bgColorTransparent },
            new[] { 0.5f, 1f },
            SKShaderTileMode.Clamp
        );
        
        // 创建底部渐变着色器（从透明到不透明）
        _cachedBottomGradientShader = SKShader.CreateLinearGradient(
            new SKPoint(0, shadowY2 - gradientHeight),
            new SKPoint(0, shadowY2),
            new[] { bgColorTransparent, bgColor },
            new[] { 0f, 0.5f },
            SKShaderTileMode.Clamp
        );
        
        // 更新缓存标记
        _cachedGradientY1 = shadowY1;
        _cachedGradientY2 = shadowY2;
        _cachedGradientHeight = gradientHeight;
    }

    // 公共方法：请求停止生成
    public void RequestStop() => _exitRequested = true;

    // 预渲染静态 UI 图层（仅包含灰色标签，数值在动态层绘制）
    private void EnsureStaticUILayerCached(string avSettingsString, string readSpeedString)
    {
        if (_staticUILayerValid && _staticUILayer != null) return;
        
        // 释放旧缓存
        _staticUILayer?.Dispose();
        
        // 创建透明图层（BGRA8888 格式）
        _staticUILayer = new SKBitmap(OutputVideoWidth, OutputVideoHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(_staticUILayer);
        canvas.Clear(SKColors.Transparent);
        
        // 颜色定义
        var dimGray = new SKColor(105, 105, 105);
        
        // A/V SETTINGS 标签（左上角，x=32 左对齐）
        canvas.DrawTextAndCache(_typeface, _fontSize16, "A/V SETTINGS", 32, 32, dimGray, HorizontalAlign.Left, VerticalAlign.Top);
        
        // ABS. OFFSET 标签（右上角，x=右边界，右对齐）
        canvas.DrawTextAndCache(_typeface, _fontSize16, "ABS. OFFSET", OutputVideoWidth - 32, 32, dimGray, HorizontalAlign.Right, VerticalAlign.Top);
        
        // BITRATE 标签（ABS. OFFSET 左侧，间隔 240px，x=右边界，右对齐）
        canvas.DrawTextAndCache(_typeface, _fontSize16, "BITRATE", OutputVideoWidth - 280, 32, dimGray, HorizontalAlign.Right, VerticalAlign.Top);
        
        _staticUILayerValid = true;
        Logger.Info("静态 UI 图层已预渲染");
    }
    // 获取当前帧的 BGRA 字节数组（用于 WPF 显示）
    public byte[] GetCurrentFrameAsBgra()
    {
        if (_frameContent == null) return null;
        
        try
        {
            int width = _frameContent.Width;
            int height = _frameContent.Height;
            
            // 使用 GetPixelSpan 高效获取像素数据（SkiaSharp 使用 BGRA 格式）
            ReadOnlySpan<byte> srcSpan = _frameContent.GetPixelSpan();
            
            byte[] data = new byte[width * height * 4];
            srcSpan.CopyTo(data);
            
            return data;
        }
        catch
        {
            return null;
        }
    }

    public int CurrentFrameWidth => _frameContent?.Width ?? 0;
    public int CurrentFrameHeight => _frameContent?.Height ?? 0;

    // 预览模式初始化（不需要导出器，？？？）
    public void InitializeForPreview()
    {
        if (InputFileStream == null && !string.IsNullOrEmpty(InputFilePath))
        {
            InputFileStream = System.IO.File.OpenRead(InputFilePath);
        }
        
        InitializeParser();
        ParseSubfiles();
        PrecomputeSubfileWaveforms();
        InitializeFonts();
        UpdateValues();
        
        // 初始化音频缓冲
        _inputAudioBuffer = new(AudioInputSamplesPerFramePerChannel, AudioInputChannelCount);
        _outputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
    }

    #endregion

    #region SkiaSharp-specific

    // 字体相关
    private SKTypeface _typeface;
    private SKTypeface _emojiTypeface;
    
    // 不同大小的字体尺寸（根据分辨率缩放）
    private float _fontSize16, _fontSize24, _fontSize32, _fontSize48;
    
    // 抗锯齿设置
    private bool _fontAntialiasing = true;

    #endregion

    #region General Parameters

    public string InputFilePath { get; set; } = null;
    // 多文件队列：如果设置了此列表，将按顺序播放所有音频文件
    public List<string> InputFilePaths { get; set; } = null;
    // 当前播放的音频文件索引（用于 UI 显示）
    public int CurrentAudioIndex { get; private set; } = 0;
    public string InputAuxiliaryFilePath { get; set; } = null;
    public string OutputFilePath { get; set; } = null;
    public string Title { get; set; } = null;
    public string Author { get; set; } = null;
    public string InputFileFormatId { get; set; } = null;
    public string ExporterId { get; set; } = null;
    // 硬件加速类型（仅适用于 FFmpeg 导出器）
    public HardwareAccelType HardwareAccel { get; set; } = HardwareAccelType.Auto;
    // NVENC 编码配置
    public string NvencPreset { get; set; } = "p1";             // P1 最快速度
    public string NvencTune { get; set; } = "ll";               // ll=低延迟模式
    public string NvencRateControl { get; set; } = "vbr";       // vbr=可变比特率
    public int NvencBFrames { get; set; } = 0;                  // B帧=0（禁用以提高速度）
    public bool NvencTemporalAQ { get; set; } = false;          // 关闭时域AQ
    public bool NvencSpatialAQ { get; set; } = false;           // 关闭空域AQ
    public int NvencAQStrength { get; set; } = 0;               // AQ强度=0（已禁用）
    public int NvencLookahead { get; set; } = 0;                // Lookahead=0（禁用前瞻）
    public bool NvencZeroLatency { get; set; } = true;          // 零延迟模式
    // 视频编码参数
    public uint VideoBitrate { get; set; } = 20_000_000;        // 默认 20 Mbps
    public int VideoCodecIndex { get; set; } = 0;               // 0=H.264, 1=H.265, 2=AV1, 3=VP9
    public int RateControlMode { get; set; } = 0;               // 0=VBR, 1=CRF, 2=CBR
    public int CrfValue { get; set; } = 23;                     // CRF值 0-51
    public int VideoProfile { get; set; } = 0;                  // 0=auto, 1=baseline, 2=main, 3=high
    public int VideoLevel { get; set; } = 0;                    // 0=auto
    public int KeyframeInterval { get; set; } = 0;              // GOP大小，0=自动
    // 音频编码参数
    public uint AudioBitrate { get; set; } = 256_000;           // 默认 256 kbps
    public int AudioCodecIndex { get; set; } = 0;               // 0=AAC
    // 输出格式
    public string OutputFormat { get; set; } = "matroska";      // matroska, mp4, webm, mov, avi
    public int InputBytesPerSecond { get; set; } = 48000 * 2;
    public string FontName { get; set; } = null;
    [CliParameter("Font Antialiasing", "font-antialiasing")]
    public bool FontAntialiasing { get => _fontAntialiasing; set => _fontAntialiasing = value; }

    #endregion

    public int InputBytesPerFrame => InputBytesPerSecond / OutputFps;

    #region Video Parameters

    [CliParameter("Output Video Width", "output-width")]
    public int OutputVideoWidth { get; set; } = 1920;
    [CliParameter("Output Video Height", "output-height")]
    public int OutputVideoHeight { get; set; } = 1080;
    // 分辨率缩放因子（基于 1080p 标准）
    public float ResolutionScale => OutputVideoHeight / 1080f;
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
    public float WaveformLineWidth { get; set; } = 1f; // 波形线条粗细（像素），默认 1

    [CliParameter("Waveform window length in milliseconds", "waveform-length-ms")]
    public float WaveformLengthMs { get; set; } = 50f; // 波形显示窗口长度（毫秒），控制时间轴缩放

    [CliParameter("Waveform display mode (average, diffavg, left, right, stereo)", "waveform-mode")]
    public string WaveformMode { get; set; } = "average"; // 波形显示模式：average/diffavg/left/right/stereo

    [CliParameter("Spectrum bar count", "spectrum-bars")]
    public int SpectrumBarCount { get; set; } = 64; // 频谱柱数量，范围 8~1024

    [CliParameter("Spectrum smoothing factor", "spectrum-smoothing")]
    public float SpectrumSmoothing { get; set; } = 0.6f; // 频谱平滑系数，范围 0.1~1

    [CliParameter("FFT size for spectrum analysis (power of 2)", "fft-size")]
    public int FftSize { get; set; } = 4096; // FFT 大小，必须是 2 的幂次方，范围 512~8192

    [CliParameter("Intro fade duration in seconds", "intro-fade-duration")]
    public float IntroFadeDuration { get; set; } = 1.0f; // 开头免责声明的淡入淡出时长（秒）

    [CliParameter("Outro fade duration in seconds", "outro-fade-duration")]
    public float OutroFadeDuration { get; set; } = 2.0f; // 内容结束后的淡出时长（秒）

    [CliParameter("Intro text content", "intro-text")]
    public string IntroText { get; set; } = "声明\n\n本视频由\nextended-binary-waterfall\n项目进行生成\n\nhttps://github.com/unai-d/extended-binary-waterfall"; // 入场显示的文字内容

    [CliParameter("Intro duration in seconds", "intro-duration")]
    public float IntroDuration { get; set; } = 5.0f; // 开场持续时间（秒）

    [CliParameter("Enable intro fade effect", "intro-fade-enabled")]
    public bool IntroFadeEnabled { get; set; } = true; // 是否启用开场淡入淡出效果

    #endregion

    #region Debug Flags

    public bool LogAllSubfiles { get; set; } = false;

    #endregion

    #region Initialization Methods

    public void Initialize()
    {
        // 验证输入文件路径
        if (string.IsNullOrEmpty(InputFilePath))
        {
            throw new ArgumentException("InputFilePath 不能为空");
        }
        
        if (InputFileStream == null)
        {
            Logger.Info("Opening files…");
            Logger.Debug($"Opening file '{InputFilePath}'…");
            
            if (!System.IO.File.Exists(InputFilePath))
            {
                throw new System.IO.FileNotFoundException($"找不到输入文件: {InputFilePath}");
            }
            
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
        PrecomputeSubfileWaveforms();

        InitializeExporter();

        InitializeFonts();

        Logger.Info("Preparing audio/video generation…");

        UpdateValues();

        // 使用 CSCore 初始化真实音频解码器，并让输出采样率/声道数跟随解码器参数
        InitializeAudioDecoder();

        // 配置画笔的抗锯齿设置
        _fillPaint.IsAntialias = _fontAntialiasing;
        _strokePaint.IsAntialias = _fontAntialiasing;
        _textPaint.IsAntialias = _fontAntialiasing;

        LogGeneratorStatus();
    }

    // 多文件音频源（当 InputFilePaths 有多个文件时使用）
    private MultiFileAudioSource _multiFileAudioSource;
    
    // 多文件二进制数据源（用于瀑布可视化）
    private MultiFileBinarySource _multiFileBinarySource;
    
    // 音频解码器的原始采样率（用于 FFmpeg 重采样）
    public int AudioDecoderSampleRate { get; private set; } = 48000;
    
    // 音频解码器的原始声道数（用于 FFmpeg 重采样）
    public int AudioDecoderChannelCount { get; private set; } = 2;
    
    // 使用 CSCore 初始化音频解码器，支持多文件队列
    private void InitializeAudioDecoder()
    {
        try
        {
            Logger.Info("Initializing audio decoder (CSCore)…");

            // 保存用户期望的输出设置（不要被解码器参数覆盖）
            int targetOutputSampleRate = AudioOutputSampleRate;
            int targetOutputChannelCount = AudioOutputChannelCount;
            
            // 如果有多文件队列，使用 MultiFileAudioSource
            if (InputFilePaths != null && InputFilePaths.Count > 1)
            {
                Logger.Info($"Using multi-file audio source with {InputFilePaths.Count} files");
                _multiFileAudioSource = new MultiFileAudioSource(InputFilePaths);
                _audioSampleSource = _multiFileAudioSource;
                
                var wf = _multiFileAudioSource.WaveFormat;
                // 保存解码器原始参数（用于 FFmpeg 重采样）
                AudioDecoderSampleRate = wf.SampleRate;
                AudioDecoderChannelCount = wf.Channels;
                
                // 计算总时长（采样数 / (采样率 × 声道数)）
                double totalDuration = _multiFileAudioSource.Length / (double)(wf.SampleRate * wf.Channels);
                Logger.Info($"Total audio duration: {TimeSpan.FromSeconds(totalDuration)}");
                
                // 计算所有文件的总大小
                long totalFileSize = 0;
                foreach (var path in InputFilePaths)
                {
                    if (System.IO.File.Exists(path))
                        totalFileSize += new System.IO.FileInfo(path).Length;
                }
                
                // 计算 InputBytesPerSecond（所有文件总大小 / 总时长）
                if (totalDuration > 0.1)
                {
                    InputBytesPerSecond = (int)(totalFileSize / totalDuration);
                    Logger.Info($"Multi-file InputBytesPerSecond: {InputBytesPerSecond} ({totalFileSize} bytes / {totalDuration:F2}s)");
                }
                
                // 初始化多文件二进制源用于瀑布可视化
                _multiFileBinarySource = new MultiFileBinarySource(InputFilePaths, InputBytesPerSecond);
                Logger.Info($"Multi-file binary source initialized: total {_multiFileBinarySource.TotalLength} bytes");
            }
            else
            {
                // 单文件模式
                _audioWaveSource = CodecFactory.Instance.GetCodec(InputFilePath);
                var wf = _audioWaveSource.WaveFormat;

                // 保存解码器原始参数（用于 FFmpeg 重采样）
                AudioDecoderSampleRate = wf.SampleRate;
                AudioDecoderChannelCount = wf.Channels;

                // 计算 InputBytesPerSecond
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

                _audioSampleSource = _audioWaveSource.ToSampleSource();
            }
            
            // 恢复用户期望的输出设置
            // FFmpeg 导出器会将音频从 AudioDecoderSampleRate/AudioDecoderChannelCount 重采样到 AudioOutputSampleRate/AudioOutputChannelCount
            AudioOutputSampleRate = targetOutputSampleRate;
            AudioOutputChannelCount = targetOutputChannelCount;
            Logger.Info($"Audio: decoder={AudioDecoderSampleRate}Hz {AudioDecoderChannelCount}ch -> output={AudioOutputSampleRate}Hz {AudioOutputChannelCount}ch");

            // 准备音频缓冲区（基于解码器参数，因为这是实际读取的数据）
            int decoderSamplesPerChannel = AudioDecoderSampleRate / OutputFps;
            _outputAudioBuffer = new(decoderSamplesPerChannel, AudioDecoderChannelCount);
            _audioSampleBuffer = new float[decoderSamplesPerChannel * AudioDecoderChannelCount];
            _inputAudioBuffer = new(decoderSamplesPerChannel, AudioDecoderChannelCount);
            
            Logger.Info($"Audio decoder initialized: {AudioDecoderSampleRate}Hz, {AudioDecoderChannelCount}ch, {decoderSamplesPerChannel} samples/ch/frame");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize audio decoder: {ex.Message}");
            _audioSampleSource = null;
            _audioWaveSource?.Dispose();
            _audioWaveSource = null;
            _multiFileAudioSource?.Dispose();
            _multiFileAudioSource = null;

            _inputAudioBuffer = new(AudioInputSamplesPerFramePerChannel, AudioInputChannelCount);
            _outputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
        }
    }

    [Conditional("DEBUG")]
    private void LogGeneratorStatus()
    {
        Logger.Debug($"Selected parser: {Parser?.GetType().GetCustomAttribute<ParserAttribute>()?.Name ?? "<null>"}");
        Logger.Debug($"Selected exporter: {Exporter?.GetType().GetCustomAttribute<ExporterAttribute>()?.Name ?? "<null>"}");
        Logger.Debug($"Selected font: {_typeface?.FamilyName ?? "<null>"}");
        Logger.Debug($"Read speed: {InputBytesPerFrame} bytes/frame ({InputBytesPerSecond} bytes/second)");
        Logger.Debug($"Waterfall duration will be around {TimeSpan.FromSeconds(InputFileStream.Length / InputBytesPerSecond)}.");
        Logger.Debug($"Video input:  {WaterfallWidth}×{WaterfallHeight}");
        Logger.Debug($"Audio input:  {AudioInputBytesPerFrame}bpf {AudioInputSamplesPerFrame}spf → {AudioInputSampleRate}Hz {AudioInputChannelCount}ch {8 * AudioInputSampleFormat.GetByteSize()}-bit");
        Logger.Debug($"Audio output: {AudioOutputBytesPerFrame}bpf {AudioOutputSamplesPerFrame}spf → {AudioOutputSampleRate}Hz {AudioOutputChannelCount}ch {8 * AudioOutputSampleFormat.GetByteSize()}-bit");
    }

    private void InitializeFonts()
    {
        Logger.Info("Loading fonts (SkiaSharp)…");
        Logger.Debug($"Requested font: '{FontName}'.");

        // 尝试按用户指定的字体名加载
        if (!string.IsNullOrEmpty(FontName))
        {
            _typeface = TryLoadFont(FontName);
            if (_typeface == null)
            {
                Logger.Error($"Cannot find font '{FontName}', using fallback.");
            }
        }

        // 如果用户指定的字体不可用，尝试备选字体
        if (_typeface == null)
        {
            // 按优先级尝试字体（包含大小写变体）
            string[] fallbackFonts = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? new[] { "Unifont", "unifont", "UNIFONT", "Unifont Upper", "Segoe UI Symbol", "Consolas", "Microsoft YaHei" }
                : new[] { "Unifont", "unifont", "Source Code Pro", "DejaVu Sans Mono", "Liberation Mono" };

            foreach (var fontName in fallbackFonts)
            {
                _typeface = TryLoadFont(fontName);
                if (_typeface != null)
                {
                    Logger.Debug($"Using fallback font: '{fontName}' → '{_typeface.FamilyName}'");
                    break;
                }
            }
        }
        
        // 尝试从常见路径加载 unifont TTF 文件
        if (_typeface == null)
        {
            _typeface = TryLoadUnifontFromFile();
        }

        // 最后回退到默认字体
        _typeface ??= SKTypeface.Default;
        Logger.Debug($"Selected font: '{_typeface?.FamilyName ?? "Default"}'");

        // 尝试加载 emoji 字体
        _emojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji") 
            ?? SKTypeface.FromFamilyName("Noto Color Emoji")
            ?? _typeface;

        // 根据分辨率缩放字体大小
        float scale = ResolutionScale;
        _fontSize48 = 48f * scale;
        _fontSize32 = 32f * scale;
        _fontSize24 = 24f * scale;
        _fontSize16 = 16f * scale;

        // 配置文本画笔的抗锯齿
        _textPaint.IsAntialias = _fontAntialiasing;
        _textPaint.Typeface = _typeface;
    }
    
    // 尝试加载指定名称的字体（使用 SKFontManager 进行精确匹配）
    private SKTypeface TryLoadFont(string fontName)
    {
        if (string.IsNullOrEmpty(fontName)) return null;
        
        // 使用 SKFontManager 进行精确匹配
        var fontManager = SKFontManager.Default;
        var typeface = fontManager.MatchFamily(fontName);
        
        // 检查是否真的找到了请求的字体（而不是回退字体）
        if (typeface != null && 
            typeface.FamilyName.Equals(fontName, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Debug($"Font matched: '{fontName}' → '{typeface.FamilyName}'");
            return typeface;
        }
        
        // 如果 MatchFamily 返回了不同的字体，说明没有找到
        Logger.Trace($"Font not found: '{fontName}' (got '{typeface?.FamilyName ?? "null"}')");
        return null;
    }
    
    // 尝试从常见路径加载 unifont TTF 文件
    private SKTypeface TryLoadUnifontFromFile()
    {
        // Windows 和 Linux 的常见字体路径
        string[] possiblePaths = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "unifont.ttf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "unifont-15.1.05.ttf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts", "unifont.ttf"),
                @"C:\Windows\Fonts\unifont.ttf",
                @"C:\Windows\Fonts\unifont-15.1.05.ttf",
            }
            : new[]
            {
                "/usr/share/fonts/truetype/unifont/unifont.ttf",
                "/usr/share/fonts/unifont/unifont.ttf",
                "/usr/share/fonts/TTF/unifont.ttf",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts", "unifont.ttf"),
            };
        
        foreach (var path in possiblePaths)
        {
            if (System.IO.File.Exists(path))
            {
                try
                {
                    var typeface = SKTypeface.FromFile(path);
                    if (typeface != null)
                    {
                        Logger.Info($"Loaded unifont from file: '{path}'");
                        return typeface;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Trace($"Failed to load font from '{path}': {ex.Message}");
                }
            }
        }
        
        Logger.Warning("unifont not found in system fonts or common file paths. Please install unifont TTF.");
        return null;
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

        // 如果是 FFmpeg 导出器，应用所有编码设置
        if (Exporter is FfmpegExporter ffmpegExporter)
        {
            // 硬件加速设置
            ffmpegExporter.HardwareAccel = HardwareAccel;
            
            // NVENC 配置
            ffmpegExporter.NvencPreset = NvencPreset;
            ffmpegExporter.NvencTune = NvencTune;
            ffmpegExporter.NvencRateControl = NvencRateControl;
            ffmpegExporter.NvencBFrames = NvencBFrames;
            ffmpegExporter.NvencTemporalAQ = NvencTemporalAQ;
            ffmpegExporter.NvencSpatialAQ = NvencSpatialAQ;
            ffmpegExporter.NvencAQStrength = NvencAQStrength;
            ffmpegExporter.NvencLookahead = NvencLookahead;
            
            // 视频编码参数
            ffmpegExporter.OutputVideoBitRate = VideoBitrate;
            ffmpegExporter.VideoCodecIndex = VideoCodecIndex;
            ffmpegExporter.RateControlMode = RateControlMode;
            ffmpegExporter.CrfValue = CrfValue;
            ffmpegExporter.VideoProfile = VideoProfile;
            ffmpegExporter.VideoLevel = VideoLevel;
            ffmpegExporter.KeyframeInterval = KeyframeInterval;
            
            // 音频编码参数
            ffmpegExporter.OutputAudioBitRate = AudioBitrate;
            ffmpegExporter.AudioCodecIndex = AudioCodecIndex;
            
            // 输出容器格式
            ffmpegExporter.OutputFormat = OutputFormat;
            
            Logger.Debug($"FFmpeg: Codec={VideoCodecIndex}, HW={HardwareAccel}, RC={RateControlMode}, CRF={CrfValue}, Bitrate={VideoBitrate/1_000_000}Mbps");
        }

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

    // 解析子文件列表，支持多文件队列
    private void ParseSubfiles()
    {
        // 如果有多文件队列（超过1个文件），为每个文件创建子文件条目
        if (InputFilePaths != null && InputFilePaths.Count > 1)
        {
            Logger.Info($"Building subfiles from audio queue ({InputFilePaths.Count} files)…");
            
            _subfiles = new List<SubFile>();
            long currentOffset = 0;
            long actualByteOffset = 0;    // 实际文件字节偏移累加
            double currentAudioTime = 0;  // 累积音频时间（秒）
            
            foreach (var filePath in InputFilePaths)
            {
                if (!System.IO.File.Exists(filePath)) continue;
                
                // 获取音频时长和实际文件大小
                long fileLength = 0;
                long actualFileSize = 0;
                double durationSeconds = 0;
                try
                {
                    actualFileSize = new System.IO.FileInfo(filePath).Length;
                    using var tempSource = CodecFactory.Instance.GetCodec(filePath);
                    durationSeconds = tempSource.Length / (double)tempSource.WaveFormat.BytesPerSecond;
                    fileLength = (long)(durationSeconds * InputBytesPerSecond);
                }
                catch
                {
                    // 无法获取时长时使用文件大小估算
                    actualFileSize = new System.IO.FileInfo(filePath).Length;
                    fileLength = actualFileSize;
                    durationSeconds = fileLength / (double)InputBytesPerSecond;
                }
                
                var sf = SubFile.FromWholeFile(filePath, fileLength);
                sf.StartOffset = currentOffset;
                sf.EndOffset = currentOffset + fileLength;
                // 设置音频时间信息（用于精确同步）
                sf.AudioStartTime = currentAudioTime;
                sf.AudioDuration = durationSeconds;
                // 设置实际文件字节偏移（用于瀑布可视化）
                sf.ActualByteOffset = actualByteOffset;
                sf.ActualByteLength = actualFileSize;
                _subfiles.Add(sf);
                
                currentOffset += fileLength;
                actualByteOffset += actualFileSize;
                currentAudioTime += durationSeconds;
            }
            
            Logger.Debug($"Created {_subfiles.Count} subfiles from audio queue, total audio time: {currentAudioTime:F2}s, total actual bytes: {actualByteOffset}");
        }
        else
        {
            // 原有的单文件解析逻辑
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

            // 如果没有解析到任何子文件，则构造一个覆盖整个输入文件的虚拟子文件
            if (_subfiles.Count == 0 && InputFileStream != null && !string.IsNullOrEmpty(InputFilePath))
            {
                _subfiles =
                [
                    SubFile.FromWholeFile(InputFilePath, InputFileStream.Length)
                ];
                Logger.Debug("No subfiles parsed; created a virtual subfile covering the whole input file.");
            }
        }

        if (LogAllSubfiles)
        {
            foreach (var sf in _subfiles)
            {
                Logger.Debug($"\t{sf.IconString ?? "–"} '{sf.Path}' {sf.StartOffset:X8}–{sf.EndOffset:X8}");
            }
        }

        // 尝试为每个子文件填充音频元数据
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

                // 作曲家（Composer）- 优先用于显示
                if (string.IsNullOrWhiteSpace(sf.ComposerName))
                {
                    if (tag.Composers != null && tag.Composers.Length > 0)
                    {
                        sf.ComposerName = string.Join(", ", tag.Composers);
                    }
                }
                
                // 艺术家 Artist 作为备选
                if (string.IsNullOrWhiteSpace(sf.ArtistName))
                {
                    if (tag.Performers != null && tag.Performers.Length > 0)
                    {
                        sf.ArtistName = string.Join(", ", tag.Performers);
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

    // 预计算所有子文件的波形 RMS 值（用于底部进度条显示）
    // 使用流式读取 + 完整采样计算，生成准确的波形数据
    private void PrecomputeSubfileWaveforms()
    {
        Logger.Info("Precomputing waveform RMS for subfiles…");
        
        // 每个子文件生成固定数量的 RMS 值
        const int rmsCount = 256;
        // 流式读取缓冲区大小（64KB 采样数据）
        const int readBufferSize = 65536;
        
        foreach (var sf in _subfiles)
        {
            if (sf == null || sf.Length <= 0) continue;
            if (string.IsNullOrWhiteSpace(sf.Path) || !System.IO.File.Exists(sf.Path)) continue;
            
            try
            {
                // 使用 CSCore 解码音频文件获取真正的 PCM 采样
                using var waveSource = CodecFactory.Instance.GetCodec(sf.Path);
                using var sampleSource = waveSource.ToSampleSource();
                
                // 获取音频总采样数
                long totalSamples = sampleSource.Length;
                if (totalSamples <= 0) continue;
                
                int channelCount = sampleSource.WaveFormat.Channels;
                if (channelCount <= 0) channelCount = 2;
                
                // 计算每个 RMS 段对应的采样数
                long samplesPerSegment = totalSamples / rmsCount;
                if (samplesPerSegment < 1) samplesPerSegment = 1;
                
                // RMS 累加器：每个段的平方和与采样计数
                double[] sumSquares = new double[rmsCount];
                long[] sampleCounts = new long[rmsCount];
                
                // 流式读取整个音频文件
                float[] buffer = new float[readBufferSize];
                long currentPosition = 0;
                int totalRead;
                
                while ((totalRead = sampleSource.Read(buffer, 0, readBufferSize)) > 0)
                {
                    // 处理每个采样，累加到对应的 RMS 段
                    for (int i = 0; i < totalRead; i++)
                    {
                        // 计算当前采样属于哪个 RMS 段
                        int segmentIndex = (int)((currentPosition + i) / samplesPerSegment);
                        if (segmentIndex >= rmsCount) segmentIndex = rmsCount - 1;
                        
                        // 累加平方值
                        double sample = buffer[i];
                        sumSquares[segmentIndex] += sample * sample;
                        sampleCounts[segmentIndex]++;
                    }
                    currentPosition += totalRead;
                }
                
                // 计算每个段的 RMS 值
                float[] rmsValues = new float[rmsCount];
                float globalMaxRms = 0f;
                
                for (int i = 0; i < rmsCount; i++)
                {
                    if (sampleCounts[i] > 0)
                    {
                        float rms = (float)Math.Sqrt(sumSquares[i] / sampleCounts[i]);
                        rmsValues[i] = rms;
                        if (rms > globalMaxRms) globalMaxRms = rms;
                    }
                }
                
                // 峰值归一化：将所有值按全局最大 RMS 缩放到 0-1 范围
                if (globalMaxRms > 0.0001f)
                {
                    for (int i = 0; i < rmsCount; i++)
                    {
                        rmsValues[i] = rmsValues[i] / globalMaxRms;
                    }
                }
                
                sf.WaveformPeaks = rmsValues;
                Logger.Debug($"Waveform computed for '{sf.FileName}': {currentPosition} samples, max RMS = {globalMaxRms:F4}");
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to compute waveform for subfile '{sf.Path}': {ex.Message}");
            }
        }
        
        Logger.Info("Waveform precomputation completed.");
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
            
            // 使用分辨率缩放因子来计算瀑布尺寸（基于 1080p 的 768x768）
            float scale = ResolutionScale;
            WaterfallScaledWidth = (int)(768 * scale);
            WaterfallScaledHeight = (int)(768 * scale);

            _videoFrameX1 = OutputVideoWidth / (_subfiles.Count > 0 ? 4 : 2) - WaterfallScaledWidth / 2;
            if (_videoFrameX2 == 0) _videoFrameX2 = _videoFrameX1 + WaterfallScaledWidth;
            _videoFrameY1 = OutputVideoHeight / 2 - WaterfallScaledHeight / 2;
            if (_videoFrameY2 == 0) _videoFrameY2 = _videoFrameY1 + WaterfallScaledHeight;
        }
    }
    
    // 总生成流程
    public void Generate()
    {
        // 极端性能优化：配置运行时和线程池
        ConfigureHighPerformanceMode();
        
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
    
    // 配置高性能模式：调整线程池、GC 和运行时设置
    private static void ConfigureHighPerformanceMode()
    {
        // 大幅增加线程池线程数（完全利用多核 CPU）
        int processorCount = Environment.ProcessorCount;
        int minWorkerThreads = processorCount * 8;
        int minIOThreads = processorCount * 4;
        int maxWorkerThreads = processorCount * 16;
        int maxIOThreads = processorCount * 8;
        
        ThreadPool.SetMinThreads(minWorkerThreads, minIOThreads);
        ThreadPool.SetMaxThreads(maxWorkerThreads, maxIOThreads);
        
        // 配置 GC 为低延迟模式（减少 GC 暂停）
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        
        // 尝试使用服务器 GC（如果可用会更高效）
        // 注意：服务器 GC 需要在 csproj 中配置 <ServerGarbageCollection>true</ServerGarbageCollection>
        
        Logger.Info($"GC已启用: {processorCount} 核心, 线程池 min={minWorkerThreads}/{minIOThreads} max={maxWorkerThreads}/{maxIOThreads}, GC={GCSettings.LatencyMode}");
    }

    // 生成开头的免责声明画面（可配置淡入淡出效果）
    private void GenerateIntro()
    {
        Logger.Info("Generating introduction…");

        int totalFrames = Math.Max(1, (int)(IntroDuration * OutputFps));
        int fadeFrames = IntroFadeEnabled ? Math.Clamp((int)(IntroFadeDuration * OutputFps), 1, Math.Max(1, totalFrames / 3)) : 0;

        int introSamplesPerChannel = Math.Max(1, AudioDecoderSampleRate / OutputFps);
        var silentAudioBuffer = new AudioBuffer(introSamplesPerChannel, AudioDecoderChannelCount);
        silentAudioBuffer.Clear();

        EnsureFrameCanvas();
        if (_frameCanvas == null)
        {
            Logger.Error("Frame canvas is not initialized; cannot render intro.");
            return;
        }

        for (int frameNumber = 0; frameNumber < totalFrames; frameNumber++)
        {
            float opacity = 1f;
            if (IntroFadeEnabled && fadeFrames > 0)
            {
                if (frameNumber < fadeFrames)
                {
                    opacity = frameNumber / (float)fadeFrames;
                }
                else if (frameNumber >= totalFrames - fadeFrames)
                {
                    opacity = (totalFrames - frameNumber) / (float)fadeFrames;
                }
                opacity = Math.Clamp(opacity, 0f, 1f);
            }

            _frameCanvas.Clear(new SKColor(16, 16, 16));

            byte alpha = (byte)(255 * opacity);
            var textColor = new SKColor(255, 255, 255, alpha);
            string countdown = $"Starting in {(totalFrames - frameNumber) / (float)OutputFps:N1} seconds…";

            DrawMultilineCentered(IntroText ?? string.Empty, OutputVideoWidth / 2f, OutputVideoHeight / 2f, _fontSize48, textColor);
            DrawText(OutputVideoWidth / 2f, OutputVideoHeight - 128, _fontSize24, countdown, textColor, VerticalAlign.Center, HorizontalAlign.Center);
            _frameCanvas.DrawProgressBar(frameNumber / (float)totalFrames,
                (int)(OutputVideoWidth * 0.3f), (int)(OutputVideoWidth * 0.7f), OutputVideoHeight - 64, opacity);

            Exporter.PushNewFrame(_frameContent, silentAudioBuffer, _timer.Elapsed.TotalSeconds);
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
        long frameNumber = 0;

        // 计算总帧数：优先基于音频源长度，否则基于文件长度
        long totalFrames;
        
        // 使用浮点数精确计算每帧采样数，避免整数除法的精度丢失
        // 注意：使用解码器声道数，因为我们读取的是解码器的数据
        double exactSamplesPerFrame = (double)AudioDecoderSampleRate / OutputFps * AudioDecoderChannelCount;
        // 用于整数计算的近似值
        int decoderSamplesPerFrame = (int)Math.Ceiling(exactSamplesPerFrame);
        // 音频采样位置累积器（使用 double 保持精度）
        double audioSamplePosition = 0;
        
        if (_audioSampleSource != null)
        {
            // 音频源长度（采样数）/ 每帧精确采样数 = 总帧数
            long audioLength = _audioSampleSource.Length;
            totalFrames = (long)(audioLength / exactSamplesPerFrame);
            Logger.Info($"Total frames based on audio: {totalFrames} (audio length: {audioLength} samples, exact samples/frame: {exactSamplesPerFrame:F3})");
        }
        else
        {
            // 基于文件长度
            totalFrames = InputFileStream.Length / InputBytesPerFrame;
        }
        
        // 计算总字节长度（用于瀑布数据边界检查）
        long totalByteLength = _multiFileBinarySource != null 
            ? _multiFileBinarySource.TotalLength 
            : InputFileStream.Length;

        using var targetFileReader = new BinaryReader(InputFileStream);

        // 循环基于总帧数（音频驱动）
        while (frameNumber < totalFrames)
        {
            // 根据当前音频时间计算正确的字节偏移（解决多文件不同比特率导致的同步问题）
            double currentAudioTime = (double)frameNumber / OutputFps;
            
            // waterfallOffset: 用于瀑布读取的实际文件字节偏移
            // currentOffset: 用于 UI 显示的虚拟字节偏移
            long waterfallOffset = 0;
            
            // 多文件模式：根据音频时间找到对应子文件，计算正确的字节偏移
            if (_subfiles.Count > 0 && _subfiles[0].AudioDuration > 0)
            {
                bool found = false;
                foreach (var sf in _subfiles)
                {
                    // 找到当前时间所在的子文件
                    if (currentAudioTime >= sf.AudioStartTime && 
                        currentAudioTime < sf.AudioStartTime + sf.AudioDuration)
                    {
                        // 在子文件内的相对进度（0-1）
                        double relativeProgress = (currentAudioTime - sf.AudioStartTime) / sf.AudioDuration;
                        // 虚拟字节偏移（用于 UI 显示）
                        currentOffset = sf.StartOffset + (long)(sf.Length * relativeProgress);
                        // 实际字节偏移（用于瀑布读取）
                        waterfallOffset = sf.ActualByteOffset + (long)(sf.ActualByteLength * relativeProgress);
                        found = true;
                        break;
                    }
                }
                // 如果超出所有子文件（播放结束），使用最后位置
                if (!found && _subfiles.Count > 0)
                {
                    var lastSf = _subfiles[_subfiles.Count - 1];
                    currentOffset = lastSf.EndOffset - 1;
                    waterfallOffset = lastSf.ActualByteOffset + lastSf.ActualByteLength - 1;
                }
            }
            else
            {
                // 单文件模式或无音频时间信息：使用传统线性计算
                currentOffset = frameNumber * InputBytesPerFrame;
                waterfallOffset = currentOffset;
            }
            
            playHeadRelPos = 0;
            // 使用实际字节偏移计算瀑布读取位置
            int bytesPerLine = WaterfallWidth * 4;
            long alignedOffset = waterfallOffset.Align(bytesPerLine);
            long frameStartByteOffset = alignedOffset - (WaterfallFrameLength / 2);
            if (frameStartByteOffset < 0)
            {
                // 开头：播放头向下移动（正值）
                playHeadRelPos = (int)-(frameStartByteOffset / bytesPerLine);
                frameStartByteOffset = 0;
            }
            else if (frameStartByteOffset + WaterfallFrameLength >= totalByteLength)
            {
                // 末尾：瀑布停止后，播放头继续向上移动（负值）
                long maxAlignedStart = ((totalByteLength - WaterfallFrameLength) / bytesPerLine) * bytesPerLine;
                if (maxAlignedStart < 0) maxAlignedStart = 0;
                // 计算播放位置超出瀑布中心的行数（负值表示向上）
                long playPositionInFrame = (alignedOffset - maxAlignedStart) / bytesPerLine;
                playHeadRelPos = (int)(playPositionInFrame - WaterfallHeight / 2);
                // 取反使其向上移动
                playHeadRelPos = -playHeadRelPos;
                frameStartByteOffset = maxAlignedStart;
            }

            // 读取瀑布数据：复用视频缓冲区避免每帧分配
            if (_reusableVideoBuffer == null || _reusableVideoBuffer.Length < WaterfallFrameLength)
            {
                _reusableVideoBuffer = new byte[WaterfallFrameLength];
            }
            // 清零缓冲区（确保读取不足时剩余部分为黑色像素）
            Array.Clear(_reusableVideoBuffer, 0, WaterfallFrameLength);
            
            int bytesRead;
            if (_multiFileBinarySource != null)
            {
                // 多文件模式：从多文件二进制源读取实际文件数据
                bytesRead = _multiFileBinarySource.ReadAt(frameStartByteOffset, _reusableVideoBuffer, 0, WaterfallFrameLength);
            }
            else
            {
                // 单文件模式：从 InputFileStream 读取
                InputFileStream.Position = frameStartByteOffset;
                bytesRead = targetFileReader.Read(_reusableVideoBuffer, 0, WaterfallFrameLength);
            }

            // 获取音频缓冲
            if (_audioSampleSource != null)
            {
                // 精确计算本帧需要读取的采样数（使用浮点累积避免精度丢失）
                // 下一帧的采样位置
                double nextSamplePosition = audioSamplePosition + exactSamplesPerFrame;
                // 本帧实际需要读取的采样数（向下取整到整数）
                int samplesThisFrame = (int)nextSamplePosition - (int)audioSamplePosition;
                // 更新累积器
                audioSamplePosition = nextSamplePosition;
                
                if (_audioSampleBuffer == null || _audioSampleBuffer.Length < samplesThisFrame)
                {
                    _audioSampleBuffer = new float[decoderSamplesPerFrame + 16]; // 留一点余量
                }

                int readSamples = _audioSampleSource.Read(_audioSampleBuffer, 0, samplesThisFrame);
                if (readSamples < samplesThisFrame)
                {
                    Array.Clear(_audioSampleBuffer, readSamples, samplesThisFrame - readSamples);
                }

                // 将float PCM 写入输出缓冲供可视化使用
                _outputAudioBuffer.LoadFromInterleavedFloats(_audioSampleBuffer, AudioDecoderChannelCount);
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

            // 将视频字节缓冲转换为位图，并完成垂直翻转
            if (_reusableWaterfallImage == null ||
                _reusableWaterfallImage.Width != WaterfallWidth ||
                _reusableWaterfallImage.Height != WaterfallHeight)
            {
                _reusableWaterfallImage?.Dispose();
                _reusableWaterfallImage = new SKBitmap(new SKImageInfo(WaterfallWidth, WaterfallHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            }

            int srcBytesPerRow = WaterfallWidth * 4;
            int imageHeight = WaterfallHeight;
            var videoBuffer = _reusableVideoBuffer;

            // 使用 GetPixels 获取指针并转换为可写 Span
            IntPtr destPtr = _reusableWaterfallImage.GetPixels();
            int destRowBytes = _reusableWaterfallImage.RowBytes;
            int width = WaterfallWidth;
            int height = WaterfallHeight;

            unsafe
            {
                byte* destBase = (byte*)destPtr;
                Parallel.For(0, height, y =>
                {
                    int destOffset = y * destRowBytes;
                    int srcOffset = (height - 1 - y) * srcBytesPerRow;
                    byte* destRow = destBase + destOffset;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = x * 4;
                        destRow[idx + 0] = videoBuffer[srcOffset + idx + 0];
                        destRow[idx + 1] = videoBuffer[srcOffset + idx + 1];
                        destRow[idx + 2] = videoBuffer[srcOffset + idx + 2];
                        destRow[idx + 3] = 255;
                    }
                });
            }

            // 复用缩放后的瀑布位图
            if (_reusableScaledWaterfall == null ||
                _reusableScaledWaterfall.Width != WaterfallScaledWidth ||
                _reusableScaledWaterfall.Height != WaterfallScaledHeight)
            {
                _reusableScaledWaterfall?.Dispose();
                _reusableScaledWaterfall = new SKBitmap(new SKImageInfo(WaterfallScaledWidth, WaterfallScaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            }

            using (var scaledCanvas = new SKCanvas(_reusableScaledWaterfall))
            {
                scaledCanvas.Clear(SKColors.Transparent);
                var srcRect = new SKRect(0, 0, WaterfallWidth, WaterfallHeight);
                var dstRect = new SKRect(0, 0, WaterfallScaledWidth, WaterfallScaledHeight);
                _imagePaint.FilterQuality = SKFilterQuality.None; // 保持像素风格
                scaledCanvas.DrawBitmap(_reusableWaterfallImage, srcRect, dstRect, _imagePaint);
            }

            _viewportFramebuf = _reusableScaledWaterfall;

            // 计算当前帧覆盖到哪些子文件，并更新右侧列表窗位置
            int currentSubfileKey = -1;
            SubFile currentSubfileValue = null;
            long halfFrame = InputBytesPerFrame / 2;
            for (int i = _subfiles.Count - 1; i >= 0; i--)
            {
                var sf = _subfiles[i];
                if (sf.Intersects(currentOffset - halfFrame, currentOffset + halfFrame))
                {
                    currentSubfileKey = i;
                    currentSubfileValue = sf;
                    break;
                }
            }

            if (currentSubfileKey >= 0)
            {
                subfileWindowIndex = 0.2f * subfileWindowIndex + 0.8f * currentSubfileKey;
            }

            // 5. 实际绘制一帧：左侧瀑布 + 右侧上部列表 + 右下音频可视化 + 顶/底渐变 + 文字信息
            EnsureFrameCanvas();
            _frameCanvas.Clear(new SKColor(16, 16, 16));

            float s = ResolutionScale;
            int rightPanelX1 = _videoFrameX2 + (int)(64 * s);
            int rightPanelX2 = OutputVideoWidth - (int)(32 * s);
            int subfileX1 = rightPanelX1;
            int subfileX2 = rightPanelX2;
            int audioVisX1 = rightPanelX1;
            int audioVisX2 = rightPanelX2;

            float subfileH = 48f * s;
            float shadowY1 = (OutputVideoHeight / 2f) - subfileH * 8.5f;
            float shadowY2 = (OutputVideoHeight / 2f) + subfileH * 8.5f; 
            float rightPanelTop = shadowY1 + subfileH * 2f + 16f * s;
            float rightPanelBottom = shadowY2 - 16f * s;
            float rightPanelHeight = rightPanelBottom - rightPanelTop;
            float listHeight = rightPanelHeight * 0.20f;
            float listTop = rightPanelTop;
            float listBottom = listTop + listHeight;

            int firstSubfileIndex = Math.Max(0, (int)(subfileWindowIndex - 2));
            int lastSubfileIndex = Math.Min(_subfiles.Count - 1, (int)Math.Ceiling(subfileWindowIndex + 7));
            float subfileRowH = 36f * s;
            float subfileY = listTop + subfileRowH / 2f + subfileRowH * 2f - (subfileWindowIndex - firstSubfileIndex) * subfileRowH;
            float minSubfileY = rightPanelTop;

            for (int sfi = firstSubfileIndex; sfi <= lastSubfileIndex; sfi++)
            {
                if (sfi < 0 || sfi >= _subfiles.Count)
                {
                    subfileY += subfileRowH;
                    continue;
                }

                if (subfileY < minSubfileY)
                {
                    subfileY += subfileRowH;
                    continue;
                }

                var subfile = _subfiles[sfi];
                bool isMainSubfile = sfi == currentSubfileKey;

                DrawText(subfileX1, subfileY, _fontSize24, isMainSubfile ? "▶" : " ", SKColors.White, VerticalAlign.Center);
                DrawText(subfileX1 + 24 * s, subfileY, _fontSize24,
                    $"{Utils.GetFileTypeEmoji(subfile)} {Utils.TruncateString(BuildSubfileDisplayLine(subfile), 50)}",
                    SKColors.White, VerticalAlign.Center);
                DrawText(subfileX2, subfileY, _fontSize24, Utils.ToByteSizeString(subfile.Length),
                    SKColors.DimGray, VerticalAlign.Center, HorizontalAlign.Right);

                if (isMainSubfile)
                {
                    double currentAudioTimeLocal = (double)frameNumber / OutputFps;
                    float percentOfSubfile;

                    if (subfile.AudioDuration > 0)
                    {
                        double timeInTrack = Math.Max(0, currentAudioTimeLocal - subfile.AudioStartTime);
                        percentOfSubfile = (float)Math.Clamp(timeInTrack / subfile.AudioDuration, 0, 1);
                    }
                    else
                    {
                        double totalAudioTimeLocal = (double)totalFrames / OutputFps;
                        double subfileStartTime = (subfile.StartOffset / (double)totalByteLength) * totalAudioTimeLocal;
                        double subfileDuration = (subfile.Length / (double)totalByteLength) * totalAudioTimeLocal;
                        double timeInTrack = Math.Max(0, currentAudioTimeLocal - subfileStartTime);
                        percentOfSubfile = (float)Math.Clamp(timeInTrack / subfileDuration, 0, 1);
                    }

                    float progressY = subfileY + 22 * s;
                    DrawText(subfileX1 + 40 * s, progressY, _fontSize16,
                        $"{(int)(percentOfSubfile * 100)} %", SKColors.White,
                        VerticalAlign.Center, HorizontalAlign.Center);
                    _frameCanvas.DrawProgressBar(percentOfSubfile, (int)(subfileX1 + 60 * s), subfileX2, progressY);
                }

                subfileY += subfileRowH;
            }

            if (_viewportFramebuf != null)
            {
                _frameCanvas.DrawBitmap(_viewportFramebuf, _videoFrameX1, _videoFrameY1, _imagePaint);
            }
            DrawText(32, (OutputVideoHeight / 2f) + (playHeadRelPos * (WaterfallScaledHeight / (float)WaterfallHeight)),
                _fontSize32, "▶", SKColors.White, VerticalAlign.Center);

            // 音频可视化区域（在歌曲列表下方，限制在遮罩区域内）
            float audioVisTop = listBottom + 8f * s;
            // 底部限制：不能超过遮罩渐变开始位置（shadowY2 - gradientHeight）
            float audioVisBottom = Math.Min(rightPanelBottom, shadowY2 - subfileH * 2.5f);
            if (audioVisBottom > audioVisTop + 16f && _outputAudioBuffer != null)
            {
                var audioVisRect = SKRect.Create(audioVisX1, audioVisTop, audioVisX2 - audioVisX1, audioVisBottom - audioVisTop);
                DrawAudioVisualizerSkia(audioVisRect, _outputAudioBuffer);
            }

            string albumHeaderText;
            {
                var sfValue = currentSubfileValue;
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

            // 淡出遮罩
            float bottomPanelTop = OutputVideoHeight - 120f * s;
            float correctedShadowY2 = Math.Min(shadowY2, bottomPanelTop);
            
            float gradientHeight = subfileH * 2f;
            EnsureGradientShadersCached(shadowY1, correctedShadowY2, gradientHeight);
            if (_cachedTopGradientShader != null)
            {
                using var gradientPaint = new SKPaint { Shader = _cachedTopGradientShader };
                _frameCanvas.DrawRect(new SKRect(0, shadowY1, OutputVideoWidth, shadowY1 + gradientHeight), gradientPaint);
            }
            if (_cachedBottomGradientShader != null)
            {
                using var gradientPaint = new SKPaint { Shader = _cachedBottomGradientShader };
                _frameCanvas.DrawRect(new SKRect(0, shadowY2 - gradientHeight, OutputVideoWidth, shadowY2), gradientPaint);
            }

            DrawText(subfileX1 + 40, rightPanelTop - 24f, _fontSize24,
                Utils.TruncateString(albumHeaderText ?? string.Empty, 72), new SKColor(105, 105, 105), VerticalAlign.Center);

            // 确保静态UI层已缓存，然后绘制
            EnsureStaticUILayerCached(avSettingsString, readSpeedString);
            if (_staticUILayer != null)
            {
                _frameCanvas.DrawBitmap(_staticUILayer, 0, 0, _imagePaint);
            }

            // 动态绘制：数值位置在标签下方（标签高度 _fontSize16 + 间距 8）
            float valueY = 32 + _fontSize16 + 8;
            
            // A/V SETTINGS 值（左上角）
            DrawText(32, valueY, _fontSize24,
                avSettingsString, SKColors.White, VerticalAlign.Top, HorizontalAlign.Left);
            
            // ABS. OFFSET 值（右上角）
            DrawText(OutputVideoWidth - 32, valueY, _fontSize24,
                $"{currentOffset / 1048576f:N2} MiB\n0x{currentOffset:X8}", SKColors.White,
                VerticalAlign.Top, HorizontalAlign.Right);
            
            // BITRATE 值（BITRATE 标签下方）
            DrawText(OutputVideoWidth - 280, valueY, _fontSize24,
                readSpeedString, SKColors.White, VerticalAlign.Top, HorizontalAlign.Right);

            // Author（居中显示）
            if (!string.IsNullOrEmpty(Author))
            {
                DrawText(OutputVideoWidth / 2f, valueY + _fontSize24 / 2, _fontSize24,
                    Author, SKColors.White, VerticalAlign.Center, HorizontalAlign.Center);
            }

            DrawBottomPlayerUISkia(s, currentOffset, currentSubfileKey, currentSubfileValue, frameNumber, totalFrames, totalByteLength);

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();

            frameNumber++;

            // 当视频数据播放完毕时，保持在最后位置
            // currentOffset 可以超过 InputFileStream.Length，但读取时会被限制

            OnProgress?.Invoke(frameNumber / (float)totalFrames);

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
    
    // 绘制底部音乐播放器 UI
    // totalFrames 和 totalByteLength 用于计算基于音频时间的进度（与实际播放同步）
    private void DrawBottomPlayerUISkia(float s, long currentOffset, int subfileIdx, SubFile subfile, long frameNumber, long totalFrames, long totalByteLength)
    {
        if (_frameCanvas == null) return;
        
        // 更新当前视频时间（基于帧号和帧率）
        _currentVideoTime = (double)frameNumber / OutputFps;
        
        // 计算总音频时长和当前音频时间（基于帧号，与实际播放同步）
        double totalAudioTime = (double)totalFrames / OutputFps;
        double currentAudioTime = (double)frameNumber / OutputFps;
        
        float bottomPanelHeight = 100f * s;
        float bottomY = OutputVideoHeight - bottomPanelHeight - 16f * s;
        float coverSize = 72f * s;
        float coverX = 32f * s;
        float coverY = bottomY + (bottomPanelHeight - coverSize) / 2f;
        float infoX = coverX + coverSize + 16f * s;
        float timeX = OutputVideoWidth - 32f * s;
        
        // 获取当前歌曲信息
        string trackName = "";
        string artistName = "";
        string genreText = "";
        TimeSpan currentTime = TimeSpan.Zero;
        TimeSpan totalTime = TimeSpan.Zero;
        float trackProgress = 0f;
        SKBitmap coverImage = null;
        float[] waveformPeaks = null;
        
        if (subfile != null)
        {
            trackName = !string.IsNullOrWhiteSpace(subfile.TrackTitle) ? subfile.TrackTitle : subfile.FileName;
            artistName = subfile.ArtistName ?? subfile.AlbumArtistName ?? "";
            genreText = subfile.Genre ?? "";
            coverImage = subfile.Icon;
            waveformPeaks = subfile.WaveformPeaks;
            
            // 使用子文件的音频时间信息计算进度（优先）或降级到字节比例计算
            if (subfile.AudioDuration > 0)
            {
                // 使用预计算的音频时间信息（精确同步）
                double timeInTrack = Math.Max(0, currentAudioTime - subfile.AudioStartTime);
                
                totalTime = TimeSpan.FromSeconds(subfile.AudioDuration);
                currentTime = TimeSpan.FromSeconds(Math.Min(timeInTrack, subfile.AudioDuration));
                trackProgress = (float)Math.Clamp(timeInTrack / subfile.AudioDuration, 0, 1);
            }
            else if (totalByteLength > 0 && totalAudioTime > 0)
            {
                // 降级：基于字节比例计算（单文件模式）
                double subfileStartTime = (subfile.StartOffset / (double)totalByteLength) * totalAudioTime;
                double subfileDuration = (subfile.Length / (double)totalByteLength) * totalAudioTime;
                double timeInTrack = Math.Max(0, currentAudioTime - subfileStartTime);
                
                totalTime = TimeSpan.FromSeconds(subfileDuration);
                currentTime = TimeSpan.FromSeconds(Math.Min(timeInTrack, subfileDuration));
                trackProgress = (float)Math.Clamp(timeInTrack / subfileDuration, 0, 1);
            }
        }
        
        // 构建显示文本
        string displayInfo = trackName;
        bool hasArtist = !string.IsNullOrWhiteSpace(artistName);
        bool hasGenre = !string.IsNullOrWhiteSpace(genreText);
        if (hasArtist) displayInfo += $" // {artistName}";
        if (hasGenre) displayInfo += $" [{genreText}]";
        displayInfo = Utils.TruncateString(displayInfo, 55);
        
        string timeString = $"{(int)currentTime.TotalMinutes}:{currentTime.Seconds:D2} / {(int)totalTime.TotalMinutes}:{totalTime.Seconds:D2}";
        
        // 检测歌曲切换，触发动画（基于时间）
        bool isInTransition = false;
        float animT = 1f;
        
        // 计算当前封面哈希
        int targetSize = Math.Max(8, (int)coverSize - 4);
        byte[] currentCoverHash = null;
        if (coverImage != null)
        {
            if (_cachedScaledCover == null || _cachedCoverSubfileIndex != subfileIdx || _cachedCoverSize != targetSize)
            {
                _cachedScaledCover?.Dispose();
                _cachedScaledCover = new SKBitmap(targetSize, targetSize, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var coverCanvas = new SKCanvas(_cachedScaledCover))
                {
                    coverCanvas.Clear(SKColors.Transparent);
                    using var coverPaint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                    coverCanvas.DrawBitmap(coverImage, new SKRect(0, 0, targetSize, targetSize), coverPaint);
                }
                _cachedCoverSubfileIndex = subfileIdx;
                _cachedCoverSize = targetSize;
            }
            currentCoverHash = ComputeImageHash(_cachedScaledCover);
        }
        
        // 检测歌曲切换（注意：这里 displayInfo/timeString 已经是新歌曲的信息）
        if (subfileIdx != _lastSubfileIndex && subfileIdx >= 0)
        {
            // _prevDisplayInfo 和 _prevTimeString 保持上一帧的旧值（已在上一帧设置）
            // 这里不更新它们，让它们在动画结束后更新
            
            // 判断封面是否相同
            bool coverSame = HashEquals(_prevCoverHash, currentCoverHash);
            if (!coverSame && _cachedScaledCover != null)
            {
                _prevScaledCover?.Dispose();
                _prevScaledCover = _cachedScaledCover.Copy();
            }
            _prevCoverHash = currentCoverHash;
            
            // 保存前一首波形
            if (_prevWaveformPeaks == null || waveformPeaks == null || _prevWaveformPeaks.Length != waveformPeaks?.Length)
            {
                _prevWaveformPeaks = waveformPeaks?.ToArray();
            }
            
            // 开始动画（基于时间）
            _animationStartTime = _currentVideoTime;
            _lastSubfileIndex = subfileIdx;
        }
        
        // 计算动画进度（基于时间，帧率无关）
        if (_animationStartTime >= 0 && _currentVideoTime < _animationStartTime + AnimationDurationSeconds)
        {
            isInTransition = true;
            float rawT = (float)((_currentVideoTime - _animationStartTime) / AnimationDurationSeconds);
            animT = EaseOutCubic(Math.Clamp(rawT, 0f, 1f));
        }
        else
        {
            // 动画结束后，更新 prev 值为当前值
            _prevDisplayInfo = displayInfo;
            _prevTimeString = timeString;
        }
        
        float labelY = bottomY + 4f * s;
        float valueY = labelY + 18f * s;
        float waveformY = valueY + 32f * s;
        float waveformX1 = infoX;
        float waveformX2 = timeX;
        float waveformHeight = 20f * s;
        
        // 封面底板与描边
        var coverRect = SKRect.Create(coverX, coverY, coverSize, coverSize);
        _fillPaint.Color = new SKColor(32, 32, 32);
        _frameCanvas.DrawRect(coverRect, _fillPaint);
        _strokePaint.Color = new SKColor(200, 200, 200);
        _strokePaint.StrokeWidth = 2f;
        _frameCanvas.DrawRect(coverRect, _strokePaint);
        
        bool coverSameAsPrev = HashEquals(_prevCoverHash, currentCoverHash);
        float coverSlideOffset = (isInTransition && !coverSameAsPrev) ? (1f - animT) * coverSize * 0.4f : 0f;
        
        // 绘制前一首封面（向左滑出，仅当封面不同）
        if (isInTransition && _prevScaledCover != null && !coverSameAsPrev)
        {
            float prevAlpha = 1f - animT;
            float prevOffsetX = -coverSlideOffset;
            var prevDest = SKRect.Create(coverX + 2 + prevOffsetX, coverY + 2, targetSize, targetSize);
            using var prevPaint = new SKPaint { FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * prevAlpha)) };
            _frameCanvas.DrawBitmap(_prevScaledCover, prevDest, prevPaint);
        }
        
        // 绘制当前封面
        if (_cachedScaledCover != null)
        {
            float currAlpha = (isInTransition && !coverSameAsPrev) ? animT : 1f;
            float currOffsetX = (isInTransition && !coverSameAsPrev) ? coverSlideOffset : 0f;
            var currDest = SKRect.Create(coverX + 2 + currOffsetX, coverY + 2, targetSize, targetSize);
            using var currPaint = new SKPaint { FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * currAlpha)) };
            _frameCanvas.DrawBitmap(_cachedScaledCover, currDest, currPaint);
        }
        else
        {
            byte noteAlpha = (byte)(255 * (isInTransition ? animT : 1f));
            DrawText(coverX + coverSize / 2, coverY + coverSize / 2, _fontSize48, "♪",
                new SKColor(100, 100, 100, noteAlpha), VerticalAlign.Center, HorizontalAlign.Center);
        }
        
        // 文字与标签
        var labelColor = new SKColor(105, 105, 105);
        
        // 绘制静态标签
        DrawText(infoX, labelY, _fontSize16, "Title:", labelColor);
        DrawText(timeX, labelY, _fontSize16, "Time:", labelColor, VerticalAlign.Top, HorizontalAlign.Right);
        
        // 曲目信息字符级动画
        DrawTextWithCharacterAnimationSkia(_fontSize32, displayInfo, _prevDisplayInfo, infoX, valueY, timeX - infoX - 120f * s, animT, isInTransition);
        
        // 时间显示：淡入淡出
        if (isInTransition && !string.IsNullOrEmpty(_prevTimeString))
        {
            // 淡出旧时间
            byte fadeOutAlpha = (byte)(255 * (1f - animT));
            DrawText(timeX, valueY, _fontSize32, _prevTimeString,
                new SKColor(255, 255, 255, fadeOutAlpha), VerticalAlign.Top, HorizontalAlign.Right);
            
            // 淡入新时间
            byte fadeInAlpha = (byte)(255 * animT);
            DrawText(timeX, valueY, _fontSize32, timeString,
                new SKColor(255, 255, 255, fadeInAlpha), VerticalAlign.Top, HorizontalAlign.Right);
        }
        else
        {
            // 非过渡状态，直接绘制
            DrawText(timeX, valueY, _fontSize32, timeString, SKColors.White, VerticalAlign.Top, HorizontalAlign.Right);
        }
        
        // 波形动画：前一首从中间向上滑出+淡出，新波形从下向上滑入+淡入 
        var waveformRect = SKRect.Create(waveformX1, waveformY, Math.Max(0.1f, waveformX2 - waveformX1), waveformHeight);
        _fillPaint.Color = new SKColor(40, 40, 40);
        _frameCanvas.DrawRect(waveformRect, _fillPaint);
        
        float barWidth = 2f;
        float barSpacing = 1f;
        int totalBars = (int)(waveformRect.Width / (barWidth + barSpacing));
        float progressX = waveformRect.Left + waveformRect.Width * trackProgress;
        float gapWidth = 4f * s;
        
        // 波形滑动偏移（快到慢，animT 从 0 到 1）
        // waveSlideOffset 在动画开始时最大，结束时为 0
        float waveSlideOffset = isInTransition ? (1f - animT) * waveformHeight * 1.2f : 0f;
        byte currWaveAlpha = (byte)(255 * (isInTransition ? animT : 1f));
        byte prevWaveAlpha = (byte)(255 * (1f - animT));
        
        // 同时绘制前一首波形和当前波形
        for (int i = 0; i < totalBars; i++)
        {
            float barX = waveformRect.Left + i * (barWidth + barSpacing);
            if (barX > progressX - gapWidth && barX < progressX + gapWidth) continue;
            
            // 绘制前一首波形（从中间向上滑出 + 淡出）
            if (isInTransition && _prevWaveformPeaks != null && _prevWaveformPeaks.Length > 0)
            {
                int prevPeakIndex = (int)((float)i / totalBars * _prevWaveformPeaks.Length);
                prevPeakIndex = Math.Clamp(prevPeakIndex, 0, _prevWaveformPeaks.Length - 1);
                float prevHeightRatio = 0.15f + _prevWaveformPeaks[prevPeakIndex] * 0.85f;
                float prevBarHeight = waveformHeight * prevHeightRatio * 0.85f;
                // 向上滑出：从中间位置向上移动（减去偏移）
                float prevBarY = waveformRect.Top + (waveformHeight - prevBarHeight) / 2f - waveSlideOffset;
                
                // 只在条形完全在区域内时绘制
                if (prevBarY >= waveformRect.Top && prevBarY + prevBarHeight <= waveformRect.Top + waveformHeight)
                {
                    _fillPaint.Color = new SKColor(180, 180, 180, prevWaveAlpha);
                    _frameCanvas.DrawRect(SKRect.Create(barX, prevBarY, barWidth, prevBarHeight), _fillPaint);
                }
            }
            
            // 绘制当前波形（从下向上滑入 + 淡入）
            float heightRatio;
            bool hasRealWaveform = waveformPeaks != null && waveformPeaks.Length > 0;
            if (hasRealWaveform)
            {
                int peakIndex = (int)((float)i / totalBars * waveformPeaks.Length);
                peakIndex = Math.Clamp(peakIndex, 0, waveformPeaks.Length - 1);
                heightRatio = 0.15f + waveformPeaks[peakIndex] * 0.85f;
            }
            else
            {
                heightRatio = 0.3f + 0.6f * (float)Math.Abs(Math.Sin(i * 0.4 + subfileIdx * 0.1));
            }
            
            float barHeight = waveformHeight * heightRatio * 0.85f;
            // 从下滑入：从底部位置向上移动到中间（加上偏移，随着动画进行偏移减小）
            float barY = waveformRect.Top + (waveformHeight - barHeight) / 2f + waveSlideOffset;
            
            // 只在条形完全在区域内时绘制
            if (barY >= waveformRect.Top && barY + barHeight <= waveformRect.Top + waveformHeight)
            {
                _fillPaint.Color = barX < progressX
                    ? new SKColor(100, 100, 100, currWaveAlpha)
                    : new SKColor(220, 220, 220, currWaveAlpha);
                _frameCanvas.DrawRect(SKRect.Create(barX, barY, barWidth, barHeight), _fillPaint);
            }
        }
        
        // 播放头
        float headlineWidth = 2f;
        float borderWidth = 1f;
        _fillPaint.Color = new SKColor(30, 30, 30);
        _frameCanvas.DrawRect(SKRect.Create(progressX - headlineWidth / 2 - borderWidth, waveformRect.Top - 2f, borderWidth, waveformHeight + 4f), _fillPaint);
        _fillPaint.Color = SKColors.White;
        _frameCanvas.DrawRect(SKRect.Create(progressX - headlineWidth / 2, waveformRect.Top - 2f, headlineWidth, waveformHeight + 4f), _fillPaint);
        _fillPaint.Color = new SKColor(30, 30, 30);
        _frameCanvas.DrawRect(SKRect.Create(progressX + headlineWidth / 2, waveformRect.Top - 2f, borderWidth, waveformHeight + 4f), _fillPaint);
        
        if (!isInTransition)
        {
            _prevDisplayInfo = displayInfo;
            _prevTimeString = timeString;
        }
    }

    // 绘制带动画的文字
    // 使用 DrawText 确保 Y 坐标计算与非动画状态一致
    private void DrawTextWithCharacterAnimationSkia(float fontSize, string newText, string oldText,
        float x, float y, float maxWidth, float animT, bool isInTransition, bool rightAlign = false)
    {
        if (_frameCanvas == null) return;
        newText ??= string.Empty;
        oldText ??= string.Empty;
        
        // 裁剪文本
        if (maxWidth > 0)
        {
            newText = ClampTextToWidth(newText, fontSize, maxWidth);
            oldText = ClampTextToWidth(oldText, fontSize, maxWidth);
        }
        if (string.IsNullOrEmpty(newText) && string.IsNullOrEmpty(oldText)) return;
        
        // 非动画状态：直接绘制新文本
        if (!isInTransition || string.IsNullOrEmpty(oldText))
        {
            DrawText(x, y, fontSize, newText, SKColors.White,
                VerticalAlign.Top, rightAlign ? HorizontalAlign.Right : HorizontalAlign.Left);
            return;
        }
        
        // 动画状态：使用 DrawText 确保 Y 坐标一致
        float slideDistance = 30f * ResolutionScale;
        var hAlign = rightAlign ? HorizontalAlign.Right : HorizontalAlign.Left;
        
        // 旧文本：向左滑动 + 淡出
        byte fadeOutAlpha = (byte)(255 * (1f - animT));
        float oldOffsetX = -slideDistance * animT;
        DrawText(x + oldOffsetX, y, fontSize, oldText, 
            new SKColor(255, 255, 255, fadeOutAlpha), VerticalAlign.Top, hAlign);
        
        // 新文本：从右滑入 + 淡入
        byte fadeInAlpha = (byte)(255 * animT);
        float newOffsetX = slideDistance * (1f - animT);
        DrawText(x + newOffsetX, y, fontSize, newText, 
            new SKColor(255, 255, 255, fadeInAlpha), VerticalAlign.Top, hAlign);
    }

    // 生成结尾淡出画面
    private void GenerateOutro()
    {
        Logger.Info("Generating outro fade…");
        int fadeFrames = Math.Max(1, (int)(OutroFadeDuration * OutputFps));

        int sampleCount = Math.Max(1, _outputAudioBuffer?.SampleCount ?? AudioOutputSamplesPerFramePerChannel);
        int channelCount = Math.Max(1, _outputAudioBuffer?.ChannelCount ?? AudioOutputChannelCount);
        var silentBuffer = new AudioBuffer(sampleCount, channelCount);
        silentBuffer.Clear();

        EnsureFrameCanvas();
        if (_frameCanvas == null)
        {
            Logger.Error("Frame canvas is not initialized; cannot render outro.");
            return;
        }

        for (int frameNum = 0; frameNum < fadeFrames; frameNum++)
        {
            float opacity = 1f - (frameNum / (float)fadeFrames);
            byte overlayAlpha = (byte)(255 * (1f - Math.Clamp(opacity, 0f, 1f)));

            _fillPaint.Color = new SKColor(16, 16, 16, overlayAlpha);
            _frameCanvas.DrawRect(SKRect.Create(0, 0, OutputVideoWidth, OutputVideoHeight), _fillPaint);

            Exporter.PushNewFrame(_frameContent, silentBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();
            OnProgress?.Invoke(1f);

            if (_exitRequested)
            {
                break;
            }
        }
    }

    // 频谱平滑缓冲
    private float[] _smoothedSpectrum = null;
    // 音频历史缓冲（用于更大的 FFT 窗口以提高低频分辨率）
    private float[] _audioHistoryBuffer = null;
    private int _audioHistoryWritePos = 0;
    // FFT 旋转因子缓存
    private double[] _fftCosTable = null;
    private double[] _fftSinTable = null;
    private int _fftCachedSize = 0;
    // FFT 输入输出缓冲区复用
    private double[] _fftReal = null;
    private double[] _fftImag = null;
    private float[] _fftMagnitudes = null;
    // 位逆序查找表
    private int[] _bitReverseTable = null;
    // Hann 窗口缓存
    private float[] _hannWindow = null;

    // 验证并获取有效的 FFT 大小（必须是 2 的幂次方）
    private int GetValidFftSize()
    {
        int size = Math.Clamp(FftSize, 512, 8192);
        // 确保是 2 的幂次方
        int power = (int)Math.Log2(size);
        return 1 << power;
    }

    // 预计算位逆序查找表
    private void PrecomputeBitReverseTable(int n)
    {
        if (_bitReverseTable != null && _bitReverseTable.Length == n) return;
        
        _bitReverseTable = new int[n];
        int bits = (int)Math.Log2(n);
        for (int i = 0; i < n; i++)
        {
            int result = 0;
            int x = i;
            for (int b = 0; b < bits; b++)
            {
                result = (result << 1) | (x & 1);
                x >>= 1;
            }
            _bitReverseTable[i] = result;
        }
    }

    // 预计算 Hann 窗口
    private void PrecomputeHannWindow(int n)
    {
        if (_hannWindow != null && _hannWindow.Length == n) return;
        
        _hannWindow = new float[n];
        double factor = 2.0 * Math.PI / (n - 1);
        for (int i = 0; i < n; i++)
        {
            _hannWindow[i] = 0.5f * (1f - (float)Math.Cos(factor * i));
        }
    }

    // SIMD 加速的 Cooley-Tukey FFT 实现
    private void ComputeFFT(double[] real, double[] imag, int n)
    {
        // 使用预计算的位逆序表进行重排
        PrecomputeBitReverseTable(n);
        for (int i = 0; i < n; i++)
        {
            int j = _bitReverseTable[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // 缓存旋转因子
        if (_fftCachedSize != n)
        {
            _fftCosTable = new double[n / 2];
            _fftSinTable = new double[n / 2];
            double angleStep = -2.0 * Math.PI / n;
            for (int i = 0; i < n / 2; i++)
            {
                double angle = angleStep * i;
                _fftCosTable[i] = Math.Cos(angle);
                _fftSinTable[i] = Math.Sin(angle);
            }
            _fftCachedSize = n;
        }

        // Cooley-Tukey 蝶形运算（SIMD 优化的内层循环）
        int vectorSize = Vector<double>.Count;
        
        for (int size = 2; size <= n; size *= 2)
        {
            int halfSize = size / 2;
            int tableStep = n / size;
            
            for (int i = 0; i < n; i += size)
            {
                int j = 0;
                
                // SIMD 向量化处理（如果支持且数据量足够）
                if (Vector.IsHardwareAccelerated && halfSize >= vectorSize)
                {
                    for (; j <= halfSize - vectorSize; j += vectorSize)
                    {
                        // 加载旋转因子
                        var cosVec = new Vector<double>(_fftCosTable, j * tableStep);
                        var sinVec = new Vector<double>(_fftSinTable, j * tableStep);
                        
                        // 加载实部和虚部
                        var realHi = new Vector<double>(real, i + j + halfSize);
                        var imagHi = new Vector<double>(imag, i + j + halfSize);
                        var realLo = new Vector<double>(real, i + j);
                        var imagLo = new Vector<double>(imag, i + j);
                        
                        // 复数乘法: (a+bi)(c+di) = (ac-bd) + (ad+bc)i
                        var tRe = realHi * cosVec - imagHi * sinVec;
                        var tIm = realHi * sinVec + imagHi * cosVec;
                        
                        // 蝶形运算
                        (realLo + tRe).CopyTo(real, i + j);
                        (imagLo + tIm).CopyTo(imag, i + j);
                        (realLo - tRe).CopyTo(real, i + j + halfSize);
                        (imagLo - tIm).CopyTo(imag, i + j + halfSize);
                    }
                }
                
                // 处理剩余元素（标量运算）
                for (; j < halfSize; j++)
                {
                    int idx = j * tableStep;
                    double tRe = real[i + j + halfSize] * _fftCosTable[idx] - imag[i + j + halfSize] * _fftSinTable[idx];
                    double tIm = real[i + j + halfSize] * _fftSinTable[idx] + imag[i + j + halfSize] * _fftCosTable[idx];
                    
                    real[i + j + halfSize] = real[i + j] - tRe;
                    imag[i + j + halfSize] = imag[i + j] - tIm;
                    real[i + j] += tRe;
                    imag[i + j] += tIm;
                }
            }
        }
    }

    // SIMD 加速的幅度计算
    private void ComputeMagnitudesSimd(double[] real, double[] imag, float[] magnitudes, int halfN, int n)
    {
        double scale = 2.0 / n;
        int vectorSize = Vector<double>.Count;
        int i = 1;
        
        // SIMD 向量化处理
        if (Vector.IsHardwareAccelerated && halfN >= vectorSize + 1)
        {
            var scaleVec = new Vector<double>(scale);
            for (; i <= halfN - vectorSize; i += vectorSize)
            {
                var re = new Vector<double>(real, i);
                var im = new Vector<double>(imag, i);
                var magSquared = re * re + im * im;
                
                // 逐元素计算平方根并转换为 float
                for (int k = 0; k < vectorSize && i + k < halfN; k++)
                {
                    magnitudes[i + k] = (float)(Math.Sqrt(magSquared[k]) * scale);
                }
            }
        }
        
        // 处理剩余元素
        for (; i < halfN; i++)
        {
            magnitudes[i] = (float)(Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]) * scale);
        }
    }

    // 音频可视化绘制
    private void DrawAudioVisualizerSkia(SKRect region, AudioBuffer audioBuffer)
    {
        if (audioBuffer == null || audioBuffer.TotalSampleCount == 0) return;

        int channelCount = audioBuffer.ChannelCount;
        int sampleCount = audioBuffer.SampleCount;
        if (channelCount <= 0 || sampleCount <= 0) return;

        // 复用数组（避免每帧分配）
        int interleavedLen = sampleCount * channelCount;
        if (_audioInterleavedBuffer == null || _audioInterleavedBuffer.Length < interleavedLen)
        {
            _audioInterleavedBuffer = new float[interleavedLen];
        }
        var interleaved = audioBuffer.ToArray();
        Array.Copy(interleaved, _audioInterleavedBuffer, Math.Min(interleaved.Length, interleavedLen));

        // 复用单声道数组（避免每帧分配）
        if (_audioMonoBuffer == null || _audioMonoBuffer.Length < sampleCount)
        {
            _audioMonoBuffer = new float[sampleCount];
        }
        for (int i = 0; i < sampleCount; i++)
        {
            float sum = 0f;
            for (int ch = 0; ch < channelCount; ch++)
            {
                int idx = i * channelCount + ch;
                if (idx < interleavedLen) sum += _audioInterleavedBuffer[idx];
            }
            _audioMonoBuffer[i] = sum / channelCount;
        }

        // 布局：上 55% 波形，下 45% 频谱
        float waveH = region.Height * 0.55f;
        float specH = region.Height * 0.45f;
        float waveTop = region.Top;
        float specTop = waveTop + waveH;

        // 绘制波形
        DrawWaveformSkia(SKRect.Create(region.Left, waveTop, region.Width, waveH), _audioMonoBuffer);

        // 绘制频谱
        DrawSpectrumSkia(SKRect.Create(region.Left, specTop, region.Width, specH), _audioMonoBuffer);
    }

    // 波形（折线）
    private void DrawWaveformSkia(SKRect region, float[] samples)
    {
        if (samples.Length < 2) return;

        // 显示窗口
        int windowSize = (int)(AudioOutputSampleRate * WaveformLengthMs / 1000f);
        windowSize = Math.Clamp(windowSize, 64, samples.Length);
        int startIdx = samples.Length - windowSize;

        // 归一化
        float maxAbs = 0.001f;
        for (int i = 0; i < windowSize; i++)
        {
            float abs = Math.Abs(samples[startIdx + i]);
            if (abs > maxAbs) maxAbs = abs;
        }

        float centerY = region.MidY;
        float amplitude = region.Height * 0.45f;
        float lineWidth = Math.Max(1.5f, WaveformLineWidth * ResolutionScale);

        // 下采样点数（折线顶点数）
        int pointCount = Math.Min(512, windowSize);
        if (_waveformPointCache == null || _waveformPointCache.Length < pointCount)
        {
            _waveformPointCache = new SKPoint[pointCount];
        }
        var points = _waveformPointCache;

        for (int i = 0; i < pointCount; i++)
        {
            // 每个点对应的样本索引
            int sampleIdx = startIdx + (i * windowSize / pointCount);
            float v = samples[sampleIdx] / maxAbs;

            float x = region.Left + (i / (float)(pointCount - 1)) * region.Width;
            float y = centerY - v * amplitude;
            points[i] = new SKPoint(x, y);
        }
        _strokePaint.Color = SKColors.White;
        _strokePaint.StrokeWidth = lineWidth;
        _frameCanvas.DrawPoints(SKPointMode.Polygon, points.AsSpan(0, pointCount).ToArray(), _strokePaint);
    }

    // 频谱绘制：使用历史缓冲实现大 FFT 窗口 + 对数频率映射 + 时间平滑
    private void DrawSpectrumSkia(SKRect region, float[] samples)
    {
        int fftSize = GetValidFftSize();
        
        if (_audioHistoryBuffer == null || _audioHistoryBuffer.Length != fftSize)
        {
            _audioHistoryBuffer = new float[fftSize];
            _audioHistoryWritePos = 0;
        }
        
        for (int i = 0; i < samples.Length; i++)
        {
            _audioHistoryBuffer[_audioHistoryWritePos] = samples[i];
            _audioHistoryWritePos = (_audioHistoryWritePos + 1) % fftSize;
        }

        int n = fftSize;
        int halfN = n / 2;

        if (_fftReal == null || _fftReal.Length != n)
        {
            _fftReal = new double[n];
            _fftImag = new double[n];
            _fftMagnitudes = new float[halfN];
        }

        PrecomputeHannWindow(n);

        for (int i = 0; i < n; i++)
        {
            int idx = (_audioHistoryWritePos + i) % n;
            _fftReal[i] = _audioHistoryBuffer[idx] * _hannWindow[i];
            _fftImag[i] = 0;
        }

        ComputeFFT(_fftReal, _fftImag, n);
        ComputeMagnitudesSimd(_fftReal, _fftImag, _fftMagnitudes, halfN, n);

        // 频率范围：20Hz ~ 采样率/2（覆盖完整频谱）
        float freqPerBin = (float)AudioOutputSampleRate / n;
        float minFreq = 20f;
        float maxFreq = AudioOutputSampleRate / 2f * 0.98f;

        int barCount = Math.Clamp(SpectrumBarCount, 16, 128);
        float[] bars = new float[barCount];

        float logMin = (float)Math.Log10(minFreq);
        float logMax = (float)Math.Log10(maxFreq);
        float logRange = logMax - logMin;

        for (int i = 0; i < barCount; i++)
        {
            float t0 = i / (float)barCount;
            float t1 = (i + 1) / (float)barCount;
            float freqLo = (float)Math.Pow(10, logMin + t0 * logRange);
            float freqHi = (float)Math.Pow(10, logMin + t1 * logRange);

            float binLo = freqLo / freqPerBin;
            float binHi = freqHi / freqPerBin;
            int bin0 = Math.Max(1, (int)binLo);
            int bin1 = Math.Min(halfN - 1, (int)Math.Ceiling(binHi));
            if (bin1 < bin0) bin1 = bin0;

            float maxMag = 0f;
            for (int k = bin0; k <= bin1; k++)
            {
                if (_fftMagnitudes[k] > maxMag) maxMag = _fftMagnitudes[k];
            }

            float db = 20f * (float)Math.Log10(maxMag + 1e-10f);
            bars[i] = Math.Clamp((db + 60f) / 60f, 0f, 1f);
        }

        if (_smoothedSpectrum == null || _smoothedSpectrum.Length != barCount)
        {
            _smoothedSpectrum = new float[barCount];
            Array.Copy(bars, _smoothedSpectrum, barCount);
        }

        float release = Math.Clamp(SpectrumSmoothing, 0.3f, 0.95f);
        for (int i = 0; i < barCount; i++)
        {
            if (bars[i] > _smoothedSpectrum[i])
                _smoothedSpectrum[i] = _smoothedSpectrum[i] * 0.2f + bars[i] * 0.8f;
            else
                _smoothedSpectrum[i] = _smoothedSpectrum[i] * release + bars[i] * (1f - release);
        }

        float barWidth = region.Width / barCount;
        float gap = barWidth * 0.15f;
        float actualWidth = barWidth - gap;

        _fillPaint.Color = SKColors.White;
        for (int i = 0; i < barCount; i++)
        {
            float h = _smoothedSpectrum[i] * region.Height;
            if (h < 0.5f) continue;
            float x = region.Left + i * barWidth + gap / 2f;
            float y = region.Bottom - h;
            _frameCanvas.DrawRect(SKRect.Create(x, y, actualWidth, h), _fillPaint);
        }
    }

    private string BuildSubfileDisplayLine(SubFile subfile)
    {
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

        // 作曲家：优先使用 Composer，其次 Artist
        string composer = subfile.ComposerName ?? subfile.ArtistName;
        if (!string.IsNullOrWhiteSpace(composer))
        {
            line += " // " + composer;
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